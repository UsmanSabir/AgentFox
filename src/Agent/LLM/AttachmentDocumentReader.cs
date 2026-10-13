using AgentFox.MCP;
using Microsoft.Extensions.Configuration;

namespace AgentFox.LLM;

/// <summary>
/// Hands chat attachments the model cannot read natively — Word, PowerPoint, Excel, EPUB, and a
/// PDF for a model without native PDF input — to the document-reading MCP server (anymd, bundled
/// under <c>MCP:BundledServers</c>). The file is saved where that server may read it and the
/// model is told the path, so it reads the document with the server's own tools and can page
/// through a long one by token budget instead of having it pasted whole into the prompt.
/// <para>
/// Settings live in <c>LLM:Attachments:DocumentReader</c> (<c>Server</c>, <c>Directory</c>,
/// <c>RetentionDays</c>); the defaults match the bundled anymd entry. <c>Directory</c> must lie
/// inside that server's <c>--allow-dir</c>, or every read is refused.
/// </para>
/// <para>
/// Retention: a saved attachment is a copy of user data written by this host, so it expires
/// after <see cref="RetentionDays"/> and every save sweeps the folder first — the sweep runs
/// inside the one write that already touches the directory, so there is no worker to forget.
/// </para>
/// </summary>
public sealed class AttachmentDocumentReader
{
    /// <summary>What the bundled reader converts, by extension, with the media type each resolves to.</summary>
    internal static readonly Dictionary<string, string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"]  = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"]  = "application/vnd.ms-excel",
        [".ods"]  = "application/vnd.oasis.opendocument.spreadsheet",
        [".epub"] = "application/epub+zip",
    };

    private readonly Func<bool> _isConnected;
    private readonly Func<bool> _modelReadsPdf;

    public string ServerName { get; }
    public string Directory { get; }
    public int RetentionDays { get; }

    /// <summary>True while the reader's MCP server is connected. Checked live: a server that
    /// failed to start must not be offered, or the model is pointed at a file it cannot open.</summary>
    public bool IsAvailable => _isConnected();

    internal AttachmentDocumentReader(
        string serverName, string directory, int retentionDays, Func<bool> isConnected, Func<bool> modelReadsPdf)
    {
        ServerName = serverName;
        Directory = directory;
        RetentionDays = retentionDays;
        _isConnected = isConnected;
        _modelReadsPdf = modelReadsPdf;
    }

    public static AttachmentDocumentReader FromConfig(IConfiguration config, McpManager? mcp, string baseDirectory)
    {
        var section = config.GetSection("LLM:Attachments:DocumentReader");
        var server = section["Server"] is { Length: > 0 } s ? s : "anymd";
        var directory = McpLaunch.Expand(section["Directory"] is { Length: > 0 } d ? d : "{workspace}/documents/attachments", baseDirectory);
        var days = int.TryParse(section["RetentionDays"], out var parsed) && parsed > 0 ? parsed : 7;

        return new AttachmentDocumentReader(
            server,
            Path.GetFullPath(directory),
            days,
            () => mcp?.Servers.ContainsKey(server) == true,
            // Read live: LLM:Model reloads, and a switch to a PDF-native model must take effect.
            () => AttachmentSupport.Resolve(config).Documents);
    }

    /// <summary>True when <paramref name="mediaType"/> is a document this reader takes. A PDF goes
    /// to the model natively when it accepts one, so turning anymd on changes nothing for it.</summary>
    public bool Handles(string mediaType) =>
        DocumentExtensions.ContainsValue(mediaType)
        && !(mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) && _modelReadsPdf());

    /// <summary>
    /// Saves one attachment under <c>Directory/&lt;conversation&gt;/</c> and returns its full path.
    /// The file name is reduced to a bare, filesystem-safe name and prefixed with a timestamp, so
    /// two uploads of "report.docx" in one conversation never overwrite each other.
    /// </summary>
    public string Save(string? conversationId, string fileName, byte[] bytes, DateTime nowUtc)
    {
        Sweep(nowUtc);

        var folder = Path.Combine(Directory, SafeSegment(conversationId, "unsorted"));
        System.IO.Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"{nowUtc:yyyyMMdd-HHmmss}-{SafeSegment(Path.GetFileName(fileName), "attachment")}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The text the model receives in place of the file.</summary>
    public string Describe(string name, string mediaType, string path) =>
        $"<attachment name=\"{name}\" type=\"{mediaType}\" path=\"{path}\">\n" +
        $"This file was not sent to you inline. Read it with the `read` tool from the `{ServerName}` MCP server, " +
        $"passing this path as `source`; use `pages` or `max_tokens` with `cursor` for a long document, and " +
        $"`search` to find passages in it. The saved copy is deleted after {RetentionDays} day(s).\n" +
        "</attachment>";

    /// <summary>Deletes saved attachments older than the retention, then any folder left empty.</summary>
    internal int Sweep(DateTime nowUtc)
    {
        var deleted = McpLaunch.SweepCache(Directory, RetentionDays, nowUtc);
        if (!System.IO.Directory.Exists(Directory)) return deleted;

        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
        {
            try
            {
                if (!System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
                    System.IO.Directory.Delete(folder);
            }
            catch { /* best-effort */ }
        }
        return deleted;
    }

    private static string SafeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
        cleaned = cleaned.Trim('.', ' ');
        if (cleaned.Length == 0) return fallback;
        if (cleaned.Length <= 120) return cleaned;

        // Shorten the stem, never the extension: the reader picks a parser by it.
        var ext = Path.GetExtension(cleaned);
        return cleaned[..(120 - ext.Length)] + ext;
    }
}
