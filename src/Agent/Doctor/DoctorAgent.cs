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

        var maskedJson = MaskSecrets(currentJson, out var secrets);
        if (maskedJson == null)
            return "Cannot parse appsettings.json — refusing to send it unmasked to the model";

        var prompt =
            $"You are a configuration assistant for AgentFox. " +
            $"You will be given the current appsettings.json and a user request. " +
            $"Return ONLY the complete updated JSON — no explanation, no markdown, no code fences. " +
            $"Preserve all existing settings unless the request requires changing them.\n" +
            SecretMaskInstruction +
            $"\nCurrent config:\n{maskedJson}\n\n" +
            $"User request: {userRequest}";

        return await RunUpdateFlowAsync(currentJson, prompt, secrets, ct);
    }

    /// <summary>Fix a specific configuration issue found by a health check.</summary>
    public async Task<FixResult> FixConfigIssueAsync(string issueDescription, CancellationToken ct = default)
    {
        DoctorUI.PrintComponentHeader("Doctor Agent — Auto-fix Configuration");

        var currentJson = ReadConfigFile();
        if (currentJson == null)
            return new FixResult(false, "Cannot read appsettings.json");

        var maskedJson = MaskSecrets(currentJson, out var secrets);
        if (maskedJson == null)
            return new FixResult(false, "Cannot parse appsettings.json — refusing to send it unmasked to the model");

        var prompt =
            $"You are a configuration assistant for AgentFox. " +
            $"A health check has detected a configuration issue. " +
            $"Fix the issue and return ONLY the complete updated JSON — no explanation, no markdown, no code fences. " +
            $"Preserve all existing settings that are not related to the issue.\n" +
            SecretMaskInstruction +
            $"\nCurrent config:\n{maskedJson}\n\n" +
            $"Issue to fix: {issueDescription}";

        var result = await RunUpdateFlowAsync(currentJson, prompt, secrets, ct);
        bool success = result.StartsWith("✓") || result.Contains("written");
        return new FixResult(success, result, RequiresRestart: true);
    }

    // ── Internal flow ─────────────────────────────────────────────────────────

    private async Task<string> RunUpdateFlowAsync(
        string currentJson,
        string prompt,
        Dictionary<string, JsonNode?> secrets,
        CancellationToken ct)
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

        // 2b. Put the real credentials back. The model only ever saw the mask, so every masked
        // value it echoed — and every one it dropped on the floor while rewriting — is restored
        // from the file it never read.
        RestoreSecrets(updatedNode, secrets);

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

    // ── Credential masking ────────────────────────────────────────────────────
    //
    // The Doctor's whole job is to hand appsettings.json to a model and write back what it
    // returns — which means the operator's API keys, bot tokens, and connection strings would
    // otherwise be uploaded to a provider on every `doctor config` call. Secrets go out masked
    // and come back restored from disk, so the model edits the shape of the file without ever
    // seeing its credentials.

    internal const string SecretMask = "********";

    private const string SecretMaskInstruction =
        "Credential values (API keys, tokens, passwords) have been replaced with \"" + SecretMask +
        "\". Leave them exactly as \"" + SecretMask + "\" — they are restored from disk after you " +
        "reply, and inventing a value would overwrite a working credential. Only write a real " +
        "credential when the request explicitly supplies one.\n";

    /// <summary>
    /// Returns the configuration JSON with every credential value replaced by
    /// <see cref="SecretMask"/>, and the originals collected by <c>a:b:c</c> path for
    /// <see cref="RestoreSecrets"/>. Null when the file cannot be parsed — the caller must then
    /// refuse rather than fall back to sending the raw text.
    /// </summary>
    internal static string? MaskSecrets(string json, out Dictionary<string, JsonNode?> secrets)
    {
        secrets = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        JsonNode? root;
        try
        {
            // appsettings.json carries // comments and trailing commas by convention.
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch
        {
            return null;
        }
        if (root is null) return null;

        MaskNode(root, string.Empty, secrets);
        return root.ToJsonString(_jsonOpts);
    }

    private static void MaskNode(JsonNode? node, string path, Dictionary<string, JsonNode?> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    var childPath = path.Length == 0 ? key : $"{path}:{key}";
                    if (AgentFox.Plugins.Security.SecretGuard.IsSecretName(key) &&
                        value is JsonValue && value.GetValueKind() == JsonValueKind.String &&
                        !string.IsNullOrEmpty(value.GetValue<string>()))
                    {
                        secrets[childPath] = value.DeepClone();
                        obj[key] = SecretMask;
                    }
                    else
                    {
                        MaskNode(value, childPath, secrets);
                    }
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                    MaskNode(array[i], $"{path}:{i}", secrets);
                break;
        }
    }

    internal static void RestoreSecrets(JsonNode updated, Dictionary<string, JsonNode?> secrets)
    {
        foreach (var (path, original) in secrets)
        {
            var segments = path.Split(':');
            var parent = ResolveParent(updated, segments, out var leaf);
            if (parent is null) continue;

            switch (parent)
            {
                // The model echoed the mask, dropped the key entirely, or replaced it with
                // something that is not a real credential — restore in all three cases. A value
                // the request explicitly supplied is left alone.
                case JsonObject obj when !obj.ContainsKey(leaf) || IsMaskOrEmpty(obj[leaf]):
                    obj[leaf] = original?.DeepClone();
                    break;
                case JsonArray array when int.TryParse(leaf, out var index)
                                          && index >= 0 && index < array.Count
                                          && IsMaskOrEmpty(array[index]):
                    array[index] = original?.DeepClone();
                    break;
            }
        }
    }

    /// <summary>Walks to the container holding the last path segment, creating nothing.</summary>
    private static JsonNode? ResolveParent(JsonNode root, string[] segments, out string leaf)
    {
        leaf = segments[^1];
        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current switch
            {
                JsonObject obj => obj.TryGetPropertyValue(segments[i], out var next) ? next : null,
                JsonArray array when int.TryParse(segments[i], out var index)
                                     && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (current is null) return null;
        }
        return current;
    }

    private static bool IsMaskOrEmpty(JsonNode? node)
    {
        if (node is null) return true;
        if (node.GetValueKind() != JsonValueKind.String) return false;
        var value = node.GetValue<string>();
        return string.IsNullOrEmpty(value) || value == SecretMask;
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
