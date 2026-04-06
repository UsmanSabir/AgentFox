namespace AgentFox.Doctor;

using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// A lightweight LLM-backed agent that reads, modifies, and writes appsettings.json.
/// Can be invoked from health check TryFix, the REPL, or any channel.
/// </summary>
public class DoctorAgent
{
    private readonly IChatClient _chatClient;
    private readonly string _configFilePath;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public DoctorAgent(IChatClient chatClient, string configFilePath)
    {
        _chatClient = chatClient;
        _configFilePath = configFilePath;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Process a free-form configuration request (REPL / channel).</summary>
    public async Task<string> ProcessRequestAsync(string userRequest, CancellationToken ct = default)
    {
        DoctorUI.PrintComponentHeader("Doctor Agent — Configuration");

        var currentJson = ReadConfigFile();
        if (currentJson == null)
            return "Cannot read appsettings.json";

        var prompt =
            $"You are a configuration assistant for AgentFox. " +
            $"You will be given the current appsettings.json and a user request. " +
            $"Return ONLY the complete updated JSON — no explanation, no markdown, no code fences. " +
            $"Preserve all existing settings unless the request requires changing them.\n\n" +
            $"Current config:\n{currentJson}\n\n" +
            $"User request: {userRequest}";

        return await RunUpdateFlowAsync(currentJson, prompt, ct);
    }

    /// <summary>Fix a specific configuration issue found by a health check.</summary>
    public async Task<FixResult> FixConfigIssueAsync(string issueDescription, CancellationToken ct = default)
    {
        DoctorUI.PrintComponentHeader("Doctor Agent — Auto-fix Configuration");

        var currentJson = ReadConfigFile();
        if (currentJson == null)
            return new FixResult(false, "Cannot read appsettings.json");

        var prompt =
            $"You are a configuration assistant for AgentFox. " +
            $"A health check has detected a configuration issue. " +
            $"Fix the issue and return ONLY the complete updated JSON — no explanation, no markdown, no code fences. " +
            $"Preserve all existing settings that are not related to the issue.\n\n" +
            $"Current config:\n{currentJson}\n\n" +
            $"Issue to fix: {issueDescription}";

        var result = await RunUpdateFlowAsync(currentJson, prompt, ct);
        bool success = result.StartsWith("✓") || result.Contains("written");
        return new FixResult(success, result, RequiresRestart: true);
    }

    // ── Internal flow ─────────────────────────────────────────────────────────

    private async Task<string> RunUpdateFlowAsync(string currentJson, string prompt, CancellationToken ct)
    {
        // 1. Ask the LLM to generate updated config
        DoctorUI.ReportHealthy("Asking LLM to generate updated configuration...");
        string updatedJson;
        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a JSON configuration assistant. Output ONLY valid JSON. No markdown, no explanation."),
                new(ChatRole.User, prompt)
            };
            var completion = await _chatClient.GetResponseAsync(messages, cancellationToken: ct);
            updatedJson = (completion.Text ?? "").Trim();

            // Strip markdown code fences if the model added them anyway
            if (updatedJson.StartsWith("```"))
            {
                updatedJson = string.Join('\n',
                    updatedJson.Split('\n')
                        .Skip(1)
                        .TakeWhile(l => !l.TrimStart().StartsWith("```")));
            }
        }
        catch (Exception ex)
        {
            DoctorUI.ReportCritical($"LLM request failed: {ex.Message}");
            return $"LLM request failed: {ex.Message}";
        }

        // 2. Validate it parses as JSON
        JsonNode? updatedNode;
        try
        {
            updatedNode = JsonNode.Parse(updatedJson);
            if (updatedNode == null) throw new Exception("Null root");
        }
        catch (Exception ex)
        {
            DoctorUI.ReportCritical($"LLM returned invalid JSON: {ex.Message}");
            return $"LLM returned invalid JSON — no changes written";
        }

        // 3. Show diff (which top-level keys changed or were added)
        ShowDiff(currentJson, updatedNode);

        // 4. Confirm before writing
        if (!DoctorUI.Confirm("Write updated configuration to appsettings.json?", defaultValue: true))
            return "Cancelled — no changes written";

        // 5. Write
        try
        {
            var formatted = updatedNode.ToJsonString(_jsonOpts);
            File.WriteAllText(_configFilePath, formatted);
            DoctorUI.ReportFixApplied($"appsettings.json updated ({formatted.Length} bytes)");
            DoctorUI.ReportWarning("Restart AgentFox for configuration changes to take effect");
            return $"✓ appsettings.json written — restart required";
        }
        catch (Exception ex)
        {
            DoctorUI.ReportFixFailed($"Write failed: {ex.Message}");
            return $"Write failed: {ex.Message}";
        }
    }

    private void ShowDiff(string originalJson, JsonNode updatedNode)
    {
        try
        {
            var originalNode = JsonNode.Parse(originalJson);
            if (originalNode is not JsonObject origObj || updatedNode is not JsonObject updatedObj)
            {
                DoctorUI.ReportWarning("Cannot diff — not a JSON object");
                return;
            }

            var changedKeys = new List<string>();
            var addedKeys   = new List<string>();

            foreach (var (key, val) in updatedObj)
            {
                if (!origObj.ContainsKey(key))
                    addedKeys.Add(key);
                else if ((origObj[key]?.ToJsonString() ?? "") != (val?.ToJsonString() ?? ""))
                    changedKeys.Add(key);
            }

            if (changedKeys.Count == 0 && addedKeys.Count == 0)
            {
                DoctorUI.ReportHealthy("No differences detected from current config");
                return;
            }

            foreach (var k in changedKeys)
                DoctorUI.ReportWarning($"Modified:  {k}");
            foreach (var k in addedKeys)
                DoctorUI.ReportHealthy($"Added:     {k}");
        }
        catch
        {
            DoctorUI.ReportWarning("Could not compute diff");
        }
    }

    private string? ReadConfigFile()
    {
        try
        {
            return File.ReadAllText(_configFilePath);
        }
        catch (Exception ex)
        {
            DoctorUI.ReportCritical($"Cannot read {_configFilePath}: {ex.Message}");
            return null;
        }
    }
}
