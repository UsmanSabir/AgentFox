namespace AgentFox.Doctor.Onboarding;

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFox.Runtime.Services;

/// <summary>
/// Guides the user through initial AgentFox configuration.
/// <para>
/// Two modes:
/// <list type="bullet">
///   <item><b>Command mode</b> — non-interactive, write config from CLI args and exit.</item>
///   <item><b>Interactive mode</b> — step-by-step wizard with Spectre.Console prompts.</item>
/// </list>
/// </para>
/// </summary>
public class OnboardingWizard
{
    private readonly string _configFilePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented       = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ── Provider catalogue ────────────────────────────────────────────────────

    private sealed record ProviderInfo(
        string Display,
        string ConfigKey,
        string DefaultBaseUrl,
        bool   NeedsApiKey,
        string ExampleModels);

    private static readonly ProviderInfo[] Providers =
    [
        new("OpenAI  (GPT-4o, GPT-4o-mini, o1)",          "OpenAI",      "https://api.openai.com/v1",    true,  "gpt-4o / gpt-4o-mini"),
        new("Anthropic  (Claude Opus, Sonnet, Haiku)",     "Anthropic",   "https://api.anthropic.com",    true,  "claude-opus-4-5 / claude-sonnet-4-5"),
        new("Ollama  ⟶  runs locally, completely free",    "Ollama",      "http://localhost:11434",       false, "llama3.2 / phi4-mini / mistral"),
        new("OpenRouter  (pay-per-use, many providers)",   "OpenRouter",  "https://openrouter.ai/api/v1", true,  "openai/gpt-4o / anthropic/claude-3-5-sonnet"),
        new("LM Studio  ⟶  local, OpenAI-compatible",     "OpenAI",      "http://localhost:1234/v1",     false, "any model loaded in LM Studio"),
        new("Azure OpenAI",                                "AzureOpenAI", "",                             true,  "gpt-4 / gpt-35-turbo"),
        new("Google AI  (Gemini)",                         "GoogleGenAI", "",                             true,  "gemini-2.0-flash / gemini-1.5-pro"),
    ];

    private const string FinishLabel = "◆  Save & finish";

    public OnboardingWizard(string configFilePath)
        => _configFilePath = configFilePath;

    // ── Command mode ──────────────────────────────────────────────────────────

    /// <summary>
    /// Non-interactive path.  Parses named args and writes the LLM section directly.
    /// <code>
    /// agentfox --onboarding --provider openai --model gpt-4o --apikey sk-... [--baseUrl ...]
    /// </code>
    /// </summary>
    public Task RunCommandModeAsync(string[] args)
    {
        var named = ParseNamedArgs(args);

        string? provider = named.GetValueOrDefault("provider");
        string? model    = named.GetValueOrDefault("model");
        string? apiKey   = named.GetValueOrDefault("apikey") ?? named.GetValueOrDefault("api-key");
        string? baseUrl  = named.GetValueOrDefault("baseurl")
                        ?? named.GetValueOrDefault("baseUrl")
                        ?? named.GetValueOrDefault("base-url");

        // Accept "--model openai" as provider if it matches a known key
        if (provider == null && model != null)
        {
            var byKey = Providers.FirstOrDefault(p =>
                p.ConfigKey.Equals(model, StringComparison.OrdinalIgnoreCase));
            if (byKey != null) { provider = model; model = null; }
        }

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
        {
            OnboardingUI.PrintWarning("Both --provider and --model are required in command mode.");
            OnboardingUI.PrintInfo("Example: agentfox --onboarding --provider openai --model gpt-4o --apikey sk-...");
            return Task.CompletedTask;
        }

        var info   = Providers.FirstOrDefault(p => p.ConfigKey.Equals(provider, StringComparison.OrdinalIgnoreCase));
        string key = info?.ConfigKey ?? provider;
        string url = baseUrl ?? info?.DefaultBaseUrl ?? "";

        var config = ReadOrCreateConfig();
        var llm    = BuildLlmNode(key, model, apiKey, url);
        config["LLM"] = llm;

        WriteConfig(config);
        OnboardingUI.PrintSuccess($"LLM configured: {key} / {model}");
        OnboardingUI.PrintDone(_configFilePath);
        return Task.CompletedTask;
    }

    // ── Interactive wizard ────────────────────────────────────────────────────

    /// <summary>Full interactive wizard — shows a menu after the mandatory LLM step.</summary>
    public async Task RunInteractiveModeAsync(CancellationToken ct = default)
    {
        OnboardingUI.PrintBanner();
        var config = ReadOrCreateConfig();

        // Step 1 — LLM is mandatory
        await RunLlmStepAsync(config, ct);

        // Optional steps: user chooses from a menu after each step
        var steps = new List<(string Key, string Display, Func<JsonObject, CancellationToken, Task> Run)>
        {
            ("workspace",  "Workspace directory",                          RunWorkspaceStepAsync),
            ("memory",     "Memory storage  (SQLite or Markdown)",         RunMemoryStepAsync),
            ("service",    $"Install as system service  ({ServiceManagerFactory.GetCurrentPlatformName()})", RunServiceStepAsync),
            ("models",     "Configure additional models",                  RunAdditionalModelsStepAsync),
            ("compaction", "Conversation compaction",                      RunCompactionStepAsync),
            ("composio",   "Composio  ⟶  GitHub, Slack, Gmail & 100+ more", RunComposioStepAsync),
            ("mcp",        "MCP servers  (advanced)",                      RunMcpStepAsync),
        };

        var done = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var choices = steps
                .Where(s => !done.Contains(s.Key))
                .Select(s => s.Display)
                .Prepend(FinishLabel)
                .ToList();

            // All optional steps completed
            if (choices.Count == 1) break;

            OnboardingUI.PrintLine();
            var choice = OnboardingUI.Choose("What would you like to set up next?", choices);

            if (choice == FinishLabel) break;

            var step = steps.First(s => s.Display == choice);
            OnboardingUI.PrintLine();
            await step.Run(config, ct);
            done.Add(step.Key);
        }

        WriteConfig(config);
        OnboardingUI.PrintDone(_configFilePath);
    }

    // ── Step: Language model (mandatory) ─────────────────────────────────────

    private Task RunLlmStepAsync(JsonObject config, CancellationToken ct)
    {
        bool alreadySet = IsLlmConfigured(config);
        string desc = alreadySet
            ? "A language model is already configured. You can keep it or replace it."
            : "The language model is the brain of AgentFox. This is the only required setting.";

        OnboardingUI.PrintStepHeader("Language Model  (AI provider)", desc);

        if (alreadySet)
        {
            var existing = config["LLM"] as JsonObject;
            string ep    = existing!["Provider"]?.GetValue<string>() ?? "";
            string em    = existing["Model"]?.GetValue<string>()     ?? "";
            OnboardingUI.PrintInfo($"Currently configured: {ep}  /  {em}");
            OnboardingUI.PrintLine();

            if (!OnboardingUI.Confirm("Update the language model?", defaultValue: false))
            {
                OnboardingUI.PrintSuccess("Keeping existing language model.");
                OnboardingUI.PrintLine();
                return Task.CompletedTask;
            }
        }

        // Choose provider ─────────────────────────────────────────────────────
        OnboardingUI.PrintLine();
        var provDisplay = OnboardingUI.Choose("Which AI provider are you using?",
            Providers.Select(p => p.Display));
        var prov = Providers.First(p => p.Display == provDisplay);
        OnboardingUI.PrintLine();

        // Model name
        string fallbackModel = FirstExample(prov.ExampleModels);
        if (!string.IsNullOrWhiteSpace(prov.ExampleModels))
            OnboardingUI.PrintInfo($"Examples: {prov.ExampleModels}");

        string model = OnboardingUI.AskText("Model name:", fallbackModel) ?? fallbackModel;

        // API key
        string? apiKey = null;
        if (prov.NeedsApiKey)
        {
            string? existingKey = (config["LLM"] as JsonObject)?["ApiKey"]?.GetValue<string>();
            bool    hasExisting = !string.IsNullOrWhiteSpace(existingKey);

            if (hasExisting)
                OnboardingUI.PrintInfo("Press Enter to keep the existing key.");

            var entered = OnboardingUI.AskText("API key:", secret: true);
            apiKey = entered ?? (hasExisting ? existingKey : null);

            if (string.IsNullOrWhiteSpace(apiKey))
                OnboardingUI.PrintWarning("No API key provided — add one to appsettings.json before using AgentFox.");
        }
        else
        {
            string? existingKey = (config["LLM"] as JsonObject)?["ApiKey"]?.GetValue<string>();
            bool hasExisting = !string.IsNullOrWhiteSpace(existingKey);
            apiKey = hasExisting ? existingKey : "api-key-value"; // Placeholder to indicate that no key is needed, but keep existing if present
        }

        // Base URL
        string? baseUrl = null;
        if (!string.IsNullOrWhiteSpace(prov.DefaultBaseUrl))
        {
            baseUrl = OnboardingUI.AskText("Base URL:", prov.DefaultBaseUrl) ?? prov.DefaultBaseUrl;
        }
        else if (prov.ConfigKey is "AzureOpenAI" or "GoogleGenAI")
        {
            baseUrl = OnboardingUI.AskText("Base URL (e.g. https://your-resource.openai.azure.com/):");
        }

        config["LLM"] = BuildLlmNode(prov.ConfigKey, model, apiKey, baseUrl);

        OnboardingUI.PrintLine();
        OnboardingUI.PrintSuccess($"Language model: {prov.ConfigKey}  /  {model}");
        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Step: Workspace directory ─────────────────────────────────────────────

    private Task RunWorkspaceStepAsync(JsonObject config, CancellationToken ct)
    {
        OnboardingUI.PrintStepHeader("Workspace Directory",
            "The folder where AgentFox stores sessions, memory, and workspace files.");

        // %ProgramData% is accessible by both the logged-in user and a Windows service account
        string suggestedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AgentFox");

        string? current = config["Workspaces"]?.AsArray()
            ?.FirstOrDefault()?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(current))
            OnboardingUI.PrintInfo($"Current: {current}");

        OnboardingUI.PrintInfo($"Suggested: {suggestedPath}  (works for both user and Windows service)");
        OnboardingUI.PrintLine();

        var input = OnboardingUI.AskText("Workspace path:", current ?? suggestedPath);
        string path = input ?? current ?? suggestedPath;

        try { Directory.CreateDirectory(path); }
        catch { OnboardingUI.PrintWarning($"Could not create '{path}' — you may need to create it manually."); }

        var arr = new JsonArray();
        arr.Add(JsonValue.Create(path));
        config["Workspaces"] = arr;

        OnboardingUI.PrintSuccess($"Workspace: {path}");
        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Step: Memory storage ──────────────────────────────────────────────────

    private Task RunMemoryStepAsync(JsonObject config, CancellationToken ct)
    {
        OnboardingUI.PrintStepHeader("Memory Storage",
            "How AgentFox persists facts and conversation history between sessions.");

        OnboardingUI.PrintInfo("SQLite with embeddings  — fast semantic search; needs an embedding model.");
        OnboardingUI.PrintInfo("Markdown files          — human-readable; easy to inspect; no extra model needed.");
        OnboardingUI.PrintLine();

        var choice = OnboardingUI.Choose("Which backend would you prefer?",
        [
            "SQLite with embeddings  (recommended)",
            "Markdown files  (simpler, no extra model needed)",
        ]);

        string backend = choice.StartsWith("SQLite") ? "Sqlite" : "Markdown";

        var mem = (config["Memory"] as JsonObject) ?? new JsonObject();
        mem["LongTermStorage"] = backend;
        config["Memory"] = mem;

        OnboardingUI.PrintSuccess($"Memory backend: {backend}");
        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Step: System service ──────────────────────────────────────────────────

    private async Task RunServiceStepAsync(JsonObject config, CancellationToken ct)
    {
        string platform = ServiceManagerFactory.GetCurrentPlatformName();

        (string mechanic, string requiresNote, string fallbackCmd) = platform switch
        {
            "Windows" => (
                "Windows Service  (sc.exe)",
                "Requires running this wizard as Administrator.",
                "Run as Administrator:  agentfox --install-service"),
            "Linux" => (
                "systemd unit  (/etc/systemd/system/agentfox.service)",
                "Requires root / sudo to write the unit file.",
                "sudo agentfox --install-service"),
            "macOS" => (
                "launchd  (/Library/LaunchDaemons/  or  ~/Library/LaunchAgents/)",
                "System-wide daemon requires sudo; per-user agent does not.",
                "sudo agentfox --install-service"),
            _ => (platform, "", "agentfox --install-service")
        };

        OnboardingUI.PrintStepHeader($"Install as system service  ({platform})",
            "Run AgentFox in the background, starting automatically on boot.");

        if (!ServiceManagerFactory.IsSupported())
        {
            OnboardingUI.PrintWarning($"Service management is not supported on '{platform}'.");
            OnboardingUI.PrintLine();
            return;
        }

        OnboardingUI.PrintInfo($"Mechanism:  {mechanic}");
        OnboardingUI.PrintInfo("Runs AgentFox without a terminal — ideal for always-on setups.");
        if (!string.IsNullOrEmpty(requiresNote))
            OnboardingUI.PrintInfo(requiresNote);
        OnboardingUI.PrintLine();

        if (!OnboardingUI.Confirm($"Install AgentFox as a system service on {platform}?", defaultValue: false))
        {
            OnboardingUI.PrintInfo("Skipped. You can install later with:  agentfox --install-service");
            OnboardingUI.PrintLine();
            return;
        }

        // Collect settings ────────────────────────────────────────────────────
        var portStr = OnboardingUI.AskText("HTTP port for the web API:", "8080");
        int port    = int.TryParse(portStr, out int p) ? p : 8080;

        bool runAsAdmin = true;
        if (platform == "macOS")
        {
            var scope = OnboardingUI.Choose("Installation scope:", [
                "System-wide daemon  (requires sudo, starts at boot for all users)",
                "Per-user agent      (no sudo needed, starts when you log in)",
            ]);
            runAsAdmin = scope.StartsWith("System");
        }

        // Persist service section into the in-memory config
        var svc = (config["Services"] as JsonObject) ?? new JsonObject();
        svc["Enabled"]    = true;
        svc["Port"]       = port;
        svc["AutoStart"]  = true;
        svc["RunAsAdmin"] = runAsAdmin;
        config["Services"] = svc;

        // Write config to disk now so the service process can read it on first start
        WriteConfig(config);

        // On Windows, if not admin, collect credentials HERE before the spinner starts.
        // AnsiConsole prompts cannot run inside AnsiConsole.Status().
        string? installUser = null, installPassword = null, installDomain = null;
        if (platform == "Windows" &&
            !AgentFox.Runtime.Services.Windows.WindowsServiceManager.IsAdministrator())
        {
            OnboardingUI.PrintLine();
            OnboardingUI.PrintWarning("Not running as Administrator — credentials are needed to install the service.");
            if (!OnboardingUI.Confirm("Provide administrator credentials now?", defaultValue: true) ||
                !AgentFox.Runtime.Services.Windows.WindowsServiceManager
                    .TryGetUserCredentials(out installUser, out installPassword, out installDomain))
            {
                //OnboardingUI.PrintInfo($"Skipped. Install manually with:  {fallbackCmd}");
                //OnboardingUI.PrintLine();
                //return;
            }
        }

        // Attempt installation ─────────────────────────────────────────────────
        OnboardingUI.PrintLine();
        var serviceConfig = new ServiceConfig
        {
            ServiceName      = "AgentFox",
            Port             = port,
            AutoStart        = true,
            RunAsAdmin       = runAsAdmin,
            LogPath          = "{workspace}/logs/service.log",
            InstallUserName  = installUser,
            InstallPassword  = installPassword,
            InstallDomain    = installDomain,
        };

        ServiceResult? result = null;
        await OnboardingUI.RunWithSpinner($"Installing {platform} service...", async () =>
        {
            var manager = ServiceManagerFactory.Create(serviceConfig);
            result = await manager.InstallAsync();
            if (result?.Success == true)
            {
                try
                {
                    var serviceResult = await manager.StartAsync();
                    if (!serviceResult.Success)
                    {
                        // Service started successfully
                        OnboardingUI.PrintWarning( "Failed to start service. " + serviceResult.Message + ". "+ serviceResult.Details);
                    }
                }
                catch{ /* Ignore error */ }
            }
        });

        if (result?.Success == true)
        {
            OnboardingUI.PrintSuccess(result.Message);
            if (!string.IsNullOrWhiteSpace(result.Details))
                OnboardingUI.PrintInfo(result.Details);
            OnboardingUI.PrintSuccess("Service installed. Start it now with:  agentfox --start-service");
        }
        else
        {
            string msg = result?.Message ?? "Unknown error";
            OnboardingUI.PrintWarning($"Installation failed: {msg}");
            if (!string.IsNullOrWhiteSpace(result?.Details))
                OnboardingUI.PrintInfo(result.Details.Replace(Environment.NewLine, " "));
            OnboardingUI.PrintInfo($"Try again manually with elevated privileges:  {fallbackCmd}");
        }

        OnboardingUI.PrintLine();
    }

    // ── Step: Additional models ───────────────────────────────────────────────

    private Task RunAdditionalModelsStepAsync(JsonObject config, CancellationToken ct)
    {
        OnboardingUI.PrintStepHeader("Additional Models",
            "Dedicate specialized models to different roles for better cost/speed trade-offs.");
        OnboardingUI.PrintInfo("All of these are optional — AgentFox uses the primary LLM when not set.");
        OnboardingUI.PrintLine();

        var roles = new[]
        {
            ("CheapModel",     "Low-cost model for simple or repetitive tasks"),
            ("FastModel",      "Quick-response model for short answers"),
            ("ReasoningModel", "High-capability model for complex reasoning"),
        };

        var models = (config["Models"] as JsonObject) ?? new JsonObject();

        foreach (var (key, desc) in roles)
        {
            if (!OnboardingUI.Confirm($"Configure {key}  ({desc})?", defaultValue: false)) continue;

            string provider = OnboardingUI.AskText("  Provider:", "Ollama")             ?? "Ollama";
            string model    = OnboardingUI.AskText("  Model name:", "llama3.2")         ?? "llama3.2";
            string baseUrl  = OnboardingUI.AskText("  Base URL:", "http://localhost:11434") ?? "http://localhost:11434";

            string? apiKey = null;
            if (!provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                var k = OnboardingUI.AskText("  API key  (leave blank if not needed):", secret: true);
                apiKey = string.IsNullOrWhiteSpace(k) ? null : k;
            }

            models[key] = BuildLlmNode(provider, model, apiKey, baseUrl);
            OnboardingUI.PrintSuccess($"{key} → {provider}  /  {model}");
        }

        config["Models"] = models;
        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Step: Compaction ──────────────────────────────────────────────────────

    private Task RunCompactionStepAsync(JsonObject config, CancellationToken ct)
    {
        OnboardingUI.PrintStepHeader("Conversation Compaction",
            "Summarize long conversations automatically to keep token usage low.");
        OnboardingUI.PrintInfo("When a conversation grows very long, AgentFox compresses it into a summary.");
        OnboardingUI.PrintInfo("This is optional — most users can leave it disabled until they need it.");
        OnboardingUI.PrintLine();

        bool enable = OnboardingUI.Confirm("Enable conversation compaction?", defaultValue: false);

        var compaction = (config["Compaction"] as JsonObject) ?? new JsonObject();
        compaction["Enabled"] = enable;

        if (enable)
        {
            compaction["TriggerThreshold"]       = 400;
            compaction["SummarizationThreshold"] = 800;
            compaction["TruncationThreshold"]    = 1200;
            OnboardingUI.PrintSuccess("Compaction enabled  (thresholds: 400 / 800 / 1200 messages).");
        }
        else
        {
            OnboardingUI.PrintInfo("Compaction disabled.");
        }

        config["Compaction"] = compaction;
        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Step: Composio ────────────────────────────────────────────────────────

    private Task RunComposioStepAsync(JsonObject config, CancellationToken ct)
    {
        OnboardingUI.PrintStepHeader("Composio  —  External Service Integrations",
            "Connect AgentFox to GitHub, Slack, Gmail, Jira, and 100+ other services.");
        OnboardingUI.PrintInfo("Composio is a skill provider. Sign up for free, then paste your API key here.");
        OnboardingUI.PrintLine();

        if (!OnboardingUI.Confirm("Set up Composio integration?", defaultValue: false))
        {
            OnboardingUI.PrintInfo("Skipped. Add Composio:ApiKey to appsettings.json any time later.");
            OnboardingUI.PrintLine();
            return Task.CompletedTask;
        }

        if (OnboardingUI.Confirm("Open https://platform.composio.dev/ in your browser?", defaultValue: true))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("https://platform.composio.dev/")
                    { UseShellExecute = true });
                OnboardingUI.PrintInfo("Browser opened — copy your API key, then paste it below.");
                OnboardingUI.PrintLine();
            }
            catch
            {
                OnboardingUI.PrintInfo("Could not open browser. Visit https://platform.composio.dev/ manually.");
            }
        }

        var key = OnboardingUI.AskText("Composio API key:", secret: true);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var composio = (config["Composio"] as JsonObject) ?? new JsonObject();
            composio["ApiKey"] = key;
            config["Composio"] = composio;
            OnboardingUI.PrintSuccess("Composio API key saved.");
        }
        else
        {
            OnboardingUI.PrintInfo("No key entered — Composio will remain disabled.");
        }

        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Step: MCP servers ─────────────────────────────────────────────────────

    private Task RunMcpStepAsync(JsonObject config, CancellationToken ct)
    {
        OnboardingUI.PrintStepHeader("MCP Servers  (Model Context Protocol)",
            "Plug in external tool servers — databases, internal APIs, custom services.");
        OnboardingUI.PrintInfo("This is an advanced setting. Most users can skip and add servers in appsettings.json later.");
        OnboardingUI.PrintLine();

        if (!OnboardingUI.Confirm("Add an MCP server?", defaultValue: false))
        {
            OnboardingUI.PrintInfo("Skipped.");
            OnboardingUI.PrintLine();
            return Task.CompletedTask;
        }

        var servers  = new JsonArray();
        bool another = true;

        while (another)
        {
            string name = OnboardingUI.AskText("  Server name:", "my-mcp-server")          ?? "my-mcp-server";
            string url  = OnboardingUI.AskText("  Server URL:", "http://localhost:3000")    ?? "http://localhost:3000";
            var    auth = OnboardingUI.AskText("  Auth token  (leave blank if not needed):", secret: true);

            var server = new JsonObject { ["Name"] = name, ["Url"] = url, ["TimeoutSeconds"] = 30 };
            if (!string.IsNullOrWhiteSpace(auth))
                server["Headers"] = new JsonObject { ["Authorization"] = $"Bearer {auth}" };

            servers.Add(server);
            OnboardingUI.PrintSuccess($"Server '{name}' added.");
            OnboardingUI.PrintLine();

            another = OnboardingUI.Confirm("Add another MCP server?", defaultValue: false);
        }

        var mcp = (config["MCP"] as JsonObject) ?? new JsonObject();
        mcp["Servers"] = servers;
        config["MCP"]  = mcp;

        OnboardingUI.PrintLine();
        return Task.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsLlmConfigured(JsonObject config)
        => config["LLM"] is JsonObject llm
        && !string.IsNullOrWhiteSpace(llm["Provider"]?.GetValue<string>())
        && !string.IsNullOrWhiteSpace(llm["Model"]?.GetValue<string>());

    private static JsonObject BuildLlmNode(string provider, string model, string? apiKey, string? baseUrl)
    {
        var node = new JsonObject
        {
            ["Provider"]       = provider,
            ["Model"]          = model,
            ["TimeoutSeconds"] = 3600,
        };
        if (!string.IsNullOrWhiteSpace(apiKey))  node["ApiKey"]  = apiKey;
        if (!string.IsNullOrWhiteSpace(baseUrl)) node["BaseUrl"] = baseUrl;
        return node;
    }

    private static string FirstExample(string examples)
        => examples.Contains('/')
            ? examples.Split('/')[0].Trim()
            : examples.Trim();

    private JsonObject ReadOrCreateConfig()
    {
        if (!File.Exists(_configFilePath))
            return new JsonObject();

        try
        {
            var text = File.ReadAllText(_configFilePath);
            return JsonNode.Parse(text, nodeOptions: null,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = true,
                           CommentHandling     = JsonCommentHandling.Skip,
                       }) as JsonObject
                   ?? new JsonObject();
        }
        catch
        {
            OnboardingUI.PrintWarning($"Could not parse existing config at {_configFilePath}. Starting with an empty config.");
            return new JsonObject();
        }
    }

    private void WriteConfig(JsonObject config)
    {
        try
        {
            var dir = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_configFilePath, config.ToJsonString(JsonOpts));
        }
        catch (Exception ex)
        {
            OnboardingUI.PrintWarning($"Failed to write configuration: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseNamedArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--") && !args[i + 1].StartsWith("--"))
                result[args[i][2..]] = args[i + 1];
        }
        return result;
    }
}
