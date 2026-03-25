using Microsoft.Agents.AI;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;

namespace AgentFox.Memory;

public static class MarkdownSessionReader
{
    public static async Task<(T Metadata, long BodyStartPosition)> ReadHeaderAsync<T>(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);

        var yamlBuilder = new StringBuilder();
        string? line;

        bool inYaml = false;
        long bytesRead = 0;

        // We need to account for the encoding's preamble (BOM) if it exists
        var preamble = Encoding.UTF8.GetPreamble();
        bytesRead += preamble.Length;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            // Calculate the length of the line in bytes including the newline character
            // Note: Using UTF8.GetByteCount to handle special characters correctly
            int lineByteCount = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            bytesRead += lineByteCount;

            if (!inYaml)
            {
                if (line.Trim() == "---") inYaml = true;
                continue;
            }

            if (line.Trim() == "---")
            {
                // Now bytesRead points exactly to the start of the next line (the body)
                break;
            }

            yamlBuilder.AppendLine(line);
        }

        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var metadata = deserializer.Deserialize<T>(yamlBuilder.ToString());

        return (metadata, bytesRead);
    }


    public static async IAsyncEnumerable<string> ReadBodyLinesAsync(string filePath, long startPosition)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(startPosition, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);

        while (!reader.EndOfStream)
        {
            yield return await reader.ReadLineAsync() ?? "";
        }
    }
    
    public static async Task<AgentSession> LoadSessionAsync(string filePath, AIAgent agent)
    {
        var (header, bodyStart) = await ReadHeaderAsync<AgentSessionHeader>(filePath);

        var messages = new List<ChatMessage>();
        ChatRole? currentRole = null; // Track the role instead of the message object
        var contentBuffer = new StringBuilder();

        await foreach (var line in ReadBodyLinesAsync(filePath, bodyStart))
        {
            if (line.StartsWith("### "))
            {
                // 1. If we have a previous role/content, finalize that message now
                if (currentRole != null)
                {
                    messages.Add(new ChatMessage(currentRole.Value, contentBuffer.ToString().Trim()));
                    contentBuffer.Clear();
                }

                // 2. Identify the new role
                var roleName = line.Replace("### ", "").Trim().ToLower();
                currentRole = roleName switch
                {
                    "user" => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    "system" => ChatRole.System,
                    _ => new ChatRole(roleName)
                };
            }
            else if (currentRole != null)
            {
                contentBuffer.AppendLine(line);
            }
        }

        // 3. Add the very last message from the buffer
        if (currentRole != null)
        {
            messages.Add(new ChatMessage(currentRole.Value, contentBuffer.ToString().Trim()));
        }

        var session = await agent.CreateSessionAsync();

        // 4. Set the value in the StateBag
        // Ensure the key matches what your ChatHistoryProvider expects
        session.StateBag.SetValue("ChatHistory", messages);

        return session;
    }

    public static async Task<string> ReadRawYamlAsync(string filePath)
    {
        //using var mmf = MemoryMappedFile.CreateFromFile(filePath);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);

        var lines = new List<string>();
        string? line;
        bool inYaml = false;

        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!inYaml)
            {
                if (line == "---") inYaml = true;
                continue;
            }

            if (line == "---") break;

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }



    public static async Task WriteSessionAsync(string filePath, JsonElement serializedSession, string modelName = "Unknown")
    {
        // 1. Extract metadata from the StateBag for the YAML header
        // The framework stores Provider states under specific keys in the JSON
        string sessionId = Guid.NewGuid().ToString();
        if (serializedSession.TryGetProperty("State", out var state)){
            // Look for ID in top-level state
            if (state.TryGetProperty("ConversationId", out var idProp))
            {
                sessionId = idProp.GetString() ?? sessionId;
            }
            // OR look inside your specific Provider's state block
            else if (state.TryGetProperty("ConversationMetadata", out var providerState) &&
                     providerState.TryGetProperty("Id", out var nestedId))
            {
                sessionId = nestedId.GetString() ?? sessionId;
            }
        }
        

        // 2. Build the YAML Frontmatter
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"SessionId: {sessionId}");
        sb.AppendLine($"CreatedAt: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Model: {modelName}");
        sb.AppendLine("---");
        sb.AppendLine();

        // 3. Extract Chat History for the Markdown Body
        // Note: The key depends on your ChatHistoryProvider's StateKey. 
        // Default is often "ChatHistory" or your custom provider's key.
        if (state.TryGetProperty("ChatHistory", out var historyElement))
        {
            var messages = historyElement.GetProperty("Messages").EnumerateArray();
            foreach (var msg in messages)
            {
                var role = msg.GetProperty("Role").GetString();
                var content = msg.GetProperty("Content").GetString();

                sb.AppendLine($"### {role}");
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        // 4. Write to File
        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
    }
}

public class AgentSessionHeader
{
    public string SessionId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Model { get; set; } = "";
}