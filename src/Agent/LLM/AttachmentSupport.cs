using AgentFox.Plugins.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace AgentFox.LLM;

/// <summary>
/// What the currently configured model can accept as a chat attachment.
/// </summary>
/// <remarks>
/// Images and PDFs are only offered when the model actually understands them — sending an
/// image part to a text-only model either errors out at the provider or, worse, is silently
/// dropped so the user believes the model looked at their screenshot. Plain-text files are
/// always available because they are inlined into the prompt as text, which every model reads.
/// </remarks>
public sealed class AttachmentCapabilities
{
    /// <summary>Master switch — false disables the paperclip in the web UI entirely.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Model accepts image parts (PNG/JPEG/GIF/WebP).</summary>
    public bool Images { get; init; }

    /// <summary>Model accepts native PDF document parts.</summary>
    public bool Documents { get; init; }

    /// <summary>Text-like files, inlined into the prompt. Supported by every model.</summary>
    public bool TextFiles { get; init; } = true;

    public int MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxFilesPerMessage { get; init; } = 5;

    /// <summary>Total decoded bytes allowed across one request.</summary>
    public int MaxTotalBytes { get; init; } = 20 * 1024 * 1024;

    /// <summary>Provider/model the decision was made for, echoed for display and debugging.</summary>
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    /// <summary>"config" when explicitly pinned in appsettings, "detected" when inferred from the model name.</summary>
    public string Source { get; init; } = "detected";

    /// <summary>Concrete media types the client may offer, for the file picker's accept list.</summary>
    public IReadOnlyList<string> AcceptedMediaTypes
    {
        get
        {
            if (!Enabled) return Array.Empty<string>();
            var types = new List<string>();
            if (Images) types.AddRange(AttachmentSupport.ImageMediaTypes);
            if (Documents) types.Add("application/pdf");
            if (TextFiles) types.AddRange(AttachmentSupport.TextAcceptHints);
            return types;
        }
    }

    /// <summary>True when nothing at all may be attached.</summary>
    public bool AnySupported => Enabled && (Images || Documents || TextFiles);
}

/// <summary>
/// An attachment that passed policy checks, with its bytes decoded and its final media
/// type resolved. Text-like files carry their decoded <see cref="Text"/> so the agent can
/// inline them without re-sniffing the encoding.
/// </summary>
public sealed class ResolvedAttachment
{
    public required string Name { get; init; }
    public required string MediaType { get; init; }
    public required byte[] Bytes { get; init; }

    /// <summary>Non-null for text-like files; the agent inlines this instead of sending bytes.</summary>
    public string? Text { get; init; }

    public bool IsText => Text != null;
}

/// <summary>
/// Resolves attachment capabilities from configuration and validates incoming attachments
/// against them. Policy lives here (the HTTP edge calls it); the agent only does the
/// mechanical conversion into message content.
/// </summary>
public static class AttachmentSupport
{
    internal static readonly string[] ImageMediaTypes =
        ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>
    /// Extension hints handed to the file picker's <c>accept</c> attribute. Browsers report
    /// no MIME type for most source files, so listing extensions is what actually works.
    /// </summary>
    internal static readonly string[] TextAcceptHints =
    [
        "text/plain", "text/markdown", "text/csv", "application/json", "application/xml",
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini",
        ".log", ".html", ".css", ".sql", ".sh", ".ps1", ".bat",
        ".cs", ".ts", ".js", ".jsx", ".tsx", ".py", ".java", ".go", ".rs", ".rb", ".php",
        ".c", ".h", ".cpp", ".hpp", ".swift", ".kt", ".scala", ".svelte", ".vue"
    ];

    /// <summary>
    /// Model-name fragments that indicate vision input. Matched against the model name rather
    /// than the provider on purpose: this codebase routinely points the "OpenAI" provider at a
    /// local OpenAI-compatible endpoint serving an arbitrary open-weights model, so the provider
    /// name says nothing about what the model can actually see.
    /// </summary>
    private static readonly string[] VisionModelFragments =
    [
        // Hosted
        "gpt-4o", "gpt-4.1", "gpt-4-turbo", "gpt-4-vision", "gpt-5", "chatgpt-4o",
        "o1", "o3", "o4-mini",
        "claude-3", "claude-4", "claude-opus", "claude-sonnet", "claude-haiku",
        "gemini",
        "grok-2-vision", "grok-3", "grok-4",
        // Open weights
        "llava", "bakllava", "moondream", "minicpm-v", "llama3.2-vision", "llama-3.2-vision",
        "llama4", "llama-4", "pixtral", "gemma3", "gemma-3", "mistral-small3", "mistral-small-3",
        "qwen2-vl", "qwen2.5-vl", "qwen3-vl", "internvl", "phi-3-vision", "phi-3.5-vision",
        "phi-4-multimodal", "granite3.2-vision", "glm-4v", "cogvlm", "aya-vision", "smolvlm",
        "-vl", "vision"
    ];

    /// <summary>Model-name fragments whose providers accept a PDF as a native document part.</summary>
    private static readonly string[] DocumentModelFragments =
    [
        "claude-3-5", "claude-3-7", "claude-4", "claude-opus", "claude-sonnet", "claude-haiku",
        "gpt-4o", "gpt-4.1", "gpt-5", "gemini"
    ];

    /// <summary>
    /// Fragments that must NOT be treated as vision-capable even though a broader fragment
    /// above would match them (e.g. "o1" appearing inside an unrelated model name is handled
    /// by whole-token matching, but these are genuine text-only members of a vision family).
    /// </summary>
    private static readonly string[] TextOnlyOverrides =
    [
        "gpt-4o-audio", "gpt-4o-realtime", "gpt-4o-transcribe", "gpt-4o-mini-tts"
    ];

    private static readonly Dictionary<string, string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",       [".md"] = "text/markdown",   [".markdown"] = "text/markdown",
        [".csv"] = "text/csv",         [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json",[".xml"] = "application/xml",[".yaml"] = "application/yaml",
        [".yml"] = "application/yaml", [".toml"] = "text/plain",    [".ini"] = "text/plain",
        [".cfg"] = "text/plain",       [".conf"] = "text/plain",    [".log"] = "text/plain",
        [".html"] = "text/html",       [".htm"] = "text/html",      [".css"] = "text/css",
        [".sql"] = "text/plain",       [".sh"] = "text/plain",      [".ps1"] = "text/plain",
        [".bat"] = "text/plain",       [".cs"] = "text/plain",      [".csproj"] = "application/xml",
        [".ts"] = "text/plain",        [".tsx"] = "text/plain",     [".js"] = "text/plain",
        [".jsx"] = "text/plain",       [".mjs"] = "text/plain",     [".py"] = "text/plain",
        [".java"] = "text/plain",      [".go"] = "text/plain",      [".rs"] = "text/plain",
        [".rb"] = "text/plain",        [".php"] = "text/plain",     [".c"] = "text/plain",
        [".h"] = "text/plain",         [".cpp"] = "text/plain",     [".hpp"] = "text/plain",
        [".swift"] = "text/plain",     [".kt"] = "text/plain",      [".scala"] = "text/plain",
        [".svelte"] = "text/plain",    [".vue"] = "text/plain",     [".razor"] = "text/html",
        [".gradle"] = "text/plain",    [".props"] = "application/xml"
    };

    private static readonly Dictionary<string, string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",   [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",   [".webp"] = "image/webp", [".pdf"] = "application/pdf"
    };

    // ── Capability resolution ────────────────────────────────────────────────

    /// <summary>
    /// Resolves what may be attached, from <c>LLM:Attachments</c> overrides where present and
    /// from the configured model name otherwise.
    /// </summary>
    public static AttachmentCapabilities Resolve(IConfiguration config)
    {
        var provider = config["LLM:Provider"] ?? string.Empty;
        var model    = config["LLM:Model"] ?? string.Empty;

        var section  = config.GetSection("LLM:Attachments");
        var enabled  = ReadBool(section["Enabled"]) ?? true;
        var images   = ReadBool(section["Images"]);
        var docs     = ReadBool(section["Documents"]);
        var textFiles = ReadBool(section["TextFiles"]) ?? true;

        var detectedImages = SupportsVision(model);
        var detectedDocs   = SupportsDocuments(model);

        return new AttachmentCapabilities
        {
            Enabled            = enabled,
            Images             = images ?? detectedImages,
            Documents          = docs ?? detectedDocs,
            TextFiles          = textFiles,
            MaxFileSizeBytes   = Math.Max(1, ReadInt(section["MaxFileSizeMb"]) ?? 10) * 1024 * 1024,
            MaxFilesPerMessage = Math.Max(1, ReadInt(section["MaxFilesPerMessage"]) ?? 5),
            MaxTotalBytes      = Math.Max(1, ReadInt(section["MaxTotalSizeMb"]) ?? 20) * 1024 * 1024,
            Provider           = provider,
            Model              = model,
            Source             = images.HasValue || docs.HasValue ? "config" : "detected"
        };
    }

    /// <summary>True when the model name indicates it accepts image input.</summary>
    public static bool SupportsVision(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var name = model.ToLowerInvariant();
        if (TextOnlyOverrides.Any(name.Contains)) return false;
        return VisionModelFragments.Any(name.Contains);
    }

    /// <summary>True when the model's provider accepts a PDF as a native document part.</summary>
    public static bool SupportsDocuments(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var name = model.ToLowerInvariant();
        if (TextOnlyOverrides.Any(name.Contains)) return false;
        return DocumentModelFragments.Any(name.Contains);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes and policy-checks <paramref name="attachments"/>. Returns false with a
    /// user-facing <paramref name="error"/> when anything is rejected — the caller should
    /// fail the turn rather than proceed with a partial set, so the user is never left
    /// thinking the model saw a file it never received.
    /// </summary>
    public static bool TryResolve(
        IReadOnlyList<ChatAttachment>? attachments,
        AttachmentCapabilities caps,
        out List<ResolvedAttachment> resolved,
        out string? error)
    {
        resolved = new List<ResolvedAttachment>();
        error = null;

        if (attachments == null || attachments.Count == 0)
            return true;

        if (!caps.Enabled || !caps.AnySupported)
        {
            error = "File attachments are disabled for this deployment.";
            return false;
        }

        if (attachments.Count > caps.MaxFilesPerMessage)
        {
            error = $"Too many attachments: {attachments.Count} (limit {caps.MaxFilesPerMessage} per message).";
            return false;
        }

        long total = 0;
        foreach (var attachment in attachments)
        {
            var name = string.IsNullOrWhiteSpace(attachment.Name) ? "attachment" : attachment.Name.Trim();

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(attachment.Data ?? string.Empty);
            }
            catch (FormatException)
            {
                error = $"'{name}' could not be decoded — attachment data must be base64.";
                return false;
            }

            if (bytes.Length == 0)
            {
                error = $"'{name}' is empty.";
                return false;
            }

            if (bytes.Length > caps.MaxFileSizeBytes)
            {
                error = $"'{name}' is {FormatSize(bytes.Length)} — the limit is {FormatSize(caps.MaxFileSizeBytes)} per file.";
                return false;
            }

            total += bytes.Length;
            if (total > caps.MaxTotalBytes)
            {
                error = $"Attachments total {FormatSize(total)} — the limit is {FormatSize(caps.MaxTotalBytes)} per message.";
                return false;
            }

            var mediaType = ResolveMediaType(name, attachment.MediaType);

            if (IsTextMediaType(mediaType))
            {
                if (!caps.TextFiles)
                {
                    error = $"'{name}' is a text file, which is not accepted by this deployment.";
                    return false;
                }
                if (!TryDecodeUtf8(bytes, out var text))
                {
                    error = $"'{name}' is not valid UTF-8 text.";
                    return false;
                }
                resolved.Add(new ResolvedAttachment { Name = name, MediaType = mediaType, Bytes = bytes, Text = text });
                continue;
            }

            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                if (!caps.Images)
                {
                    error = $"'{name}' is an image, but the current model ({Describe(caps)}) does not accept image input.";
                    return false;
                }
                if (!ImageMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"'{name}' uses an unsupported image format ({mediaType}). Use PNG, JPEG, GIF, or WebP.";
                    return false;
                }
                resolved.Add(new ResolvedAttachment { Name = name, MediaType = mediaType, Bytes = bytes });
                continue;
            }

            if (mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (!caps.Documents)
                {
                    error = $"'{name}' is a PDF, but the current model ({Describe(caps)}) does not accept document input.";
                    return false;
                }
                resolved.Add(new ResolvedAttachment { Name = name, MediaType = mediaType, Bytes = bytes });
                continue;
            }

            error = $"'{name}' has an unsupported type ({mediaType}).";
            return false;
        }

        return true;
    }

    private static string Describe(AttachmentCapabilities caps) =>
        string.IsNullOrWhiteSpace(caps.Model) ? caps.Provider : caps.Model;

    // ── Prompt conversion ────────────────────────────────────────────────────

    /// <summary>
    /// Turns already-accepted attachments into message content parts. Deliberately lenient:
    /// policy was enforced by <see cref="TryResolve"/> at the HTTP edge, and anything that
    /// still fails to decode here is skipped rather than failing an in-flight turn.
    /// <para>
    /// Text-like files are inlined as delimited text blocks so every model — including
    /// text-only local models — can read them. Images and PDFs become <see cref="DataContent"/>
    /// parts, preceded by a short text label so the model knows the file's name.
    /// </para>
    /// </summary>
    public static (List<AIContent> Contents, string TranscriptNote) ConvertForPrompt(
        IReadOnlyList<ChatAttachment>? attachments)
    {
        var contents = new List<AIContent>();
        var notes = new List<string>();

        if (attachments == null || attachments.Count == 0)
            return (contents, string.Empty);

        foreach (var attachment in attachments)
        {
            var name = string.IsNullOrWhiteSpace(attachment.Name) ? "attachment" : attachment.Name.Trim();

            byte[] bytes;
            try { bytes = Convert.FromBase64String(attachment.Data ?? string.Empty); }
            catch (FormatException) { continue; }
            if (bytes.Length == 0) continue;

            var mediaType = ResolveMediaType(name, attachment.MediaType);

            if (IsTextMediaType(mediaType) && TryDecodeUtf8(bytes, out var text))
            {
                contents.Add(new TextContent(
                    $"<attachment name=\"{name}\" type=\"{mediaType}\">\n{text}\n</attachment>"));
            }
            else
            {
                // The label is a separate part so it survives providers that reorder or
                // strip metadata from binary parts — the model still learns the file name.
                contents.Add(new TextContent($"<attachment name=\"{name}\" type=\"{mediaType}\" />"));
                contents.Add(new DataContent(bytes, mediaType));
            }

            notes.Add($"{name} ({mediaType}, {FormatSize(bytes.Length)})");
        }

        var transcriptNote = notes.Count == 0
            ? string.Empty
            : $"\n\n_Attached: {string.Join("; ", notes)}_";

        return (contents, transcriptNote);
    }

    // ── Media type helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Picks the media type to trust. The browser's value wins when it is specific; otherwise
    /// (empty, or the useless <c>application/octet-stream</c> it reports for most source files)
    /// the extension decides, defaulting to plain text so unknown code files stay usable.
    /// </summary>
    public static string ResolveMediaType(string fileName, string? reported)
    {
        var ext = Path.GetExtension(fileName);

        if (!string.IsNullOrWhiteSpace(ext))
        {
            if (BinaryExtensions.TryGetValue(ext, out var binary)) return binary;
            if (TextExtensions.TryGetValue(ext, out var text)) return text;
        }

        if (!string.IsNullOrWhiteSpace(reported) &&
            !reported.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return reported.Split(';')[0].Trim().ToLowerInvariant();

        return "text/plain";
    }

    public static bool IsTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType is "application/json" or "application/xml" or "application/yaml" or
                     "application/x-yaml" or "application/javascript" or "application/sql";

    /// <summary>
    /// Strict UTF-8 decode. A file that fails here is binary content wearing a text
    /// extension; inlining its mojibake would only poison the prompt.
    /// </summary>
    private static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes)
                .TrimStart('﻿');
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
    };

    private static bool? ReadBool(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : null;

    private static int? ReadInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;
}
