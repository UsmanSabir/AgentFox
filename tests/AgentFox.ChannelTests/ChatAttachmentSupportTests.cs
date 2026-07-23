using AgentFox.LLM;
using AgentFox.Plugins.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace AgentFox.ChannelTests;

/// <summary>
/// Covers the "if the model supports it" half of chat attachments: which files the configured
/// model is allowed to receive, and what an accepted file turns into on the wire.
/// </summary>
[TestClass]
public sealed class ChatAttachmentSupportTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static ChatAttachment File(string name, string? mediaType, byte[] bytes) =>
        new() { Name = name, MediaType = mediaType, Data = Convert.ToBase64String(bytes) };

    private static ChatAttachment TextFile(string name, string content) =>
        File(name, "text/plain", Encoding.UTF8.GetBytes(content));

    private static ChatAttachment ImageFile(string name = "shot.png", int size = 64) =>
        File(name, "image/png", Enumerable.Range(0, size).Select(i => (byte)i).ToArray());

    // ── Capability detection ────────────────────────────────────────────────

    [TestMethod]
    public void Vision_IsDetectedFromModelName_NotProviderName()
    {
        // The "OpenAI" provider routinely points at a local OpenAI-compatible endpoint serving
        // an arbitrary open-weights model, so the provider name must not imply vision.
        var textOnly = AttachmentSupport.Resolve(
            Config(("LLM:Provider", "OpenAI"), ("LLM:Model", "qwen2.5-14b-instruct")));
        Assert.IsFalse(textOnly.Images, "A text-only model must not advertise image support.");
        Assert.IsFalse(textOnly.Documents);
        Assert.IsTrue(textOnly.TextFiles, "Text files are inlined as text and always work.");
        Assert.IsTrue(textOnly.AnySupported);

        var vision = AttachmentSupport.Resolve(
            Config(("LLM:Provider", "OpenAI"), ("LLM:Model", "gpt-4o")));
        Assert.IsTrue(vision.Images);
        Assert.IsTrue(vision.Documents);
    }

    [TestMethod]
    [DataRow("claude-sonnet-4-5", true, true)]
    [DataRow("gemini-2.5-pro", true, true)]
    [DataRow("llava:13b", true, false)]
    [DataRow("llama3.2-vision", true, false)]
    [DataRow("qwen2.5-vl-7b", true, false)]
    [DataRow("phi4-mini", false, false)]
    [DataRow("deepseek-r1", false, false)]
    [DataRow("llama3.2", false, false)]
    public void ModelNames_MapToExpectedCapabilities(string model, bool images, bool documents)
    {
        Assert.AreEqual(images, AttachmentSupport.SupportsVision(model), $"vision for {model}");
        Assert.AreEqual(documents, AttachmentSupport.SupportsDocuments(model), $"documents for {model}");
    }

    [TestMethod]
    public void ConfigOverride_WinsOverDetection()
    {
        // Detection cannot know about a locally served vision model with an unusual name,
        // so an explicit override has to be able to turn images on.
        var caps = AttachmentSupport.Resolve(Config(
            ("LLM:Provider", "OpenAI"),
            ("LLM:Model", "my-custom-multimodal"),
            ("LLM:Attachments:Images", "true")));

        Assert.IsTrue(caps.Images);
        Assert.AreEqual("config", caps.Source);
    }

    [TestMethod]
    public void MasterSwitch_DisablesEverything()
    {
        var caps = AttachmentSupport.Resolve(Config(
            ("LLM:Model", "gpt-4o"), ("LLM:Attachments:Enabled", "false")));

        Assert.IsFalse(caps.AnySupported);
        Assert.AreEqual(0, caps.AcceptedMediaTypes.Count);

        Assert.IsFalse(AttachmentSupport.TryResolve([TextFile("a.txt", "hi")], caps, out _, out var error));
        StringAssert.Contains(error!, "disabled");
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [TestMethod]
    public void ImageToTextOnlyModel_IsRejectedWithAnExplanation()
    {
        // The failure mode this guards against is silent: dropping the image and letting the
        // user believe the model looked at their screenshot.
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "qwen2.5-14b-instruct")));

        Assert.IsFalse(AttachmentSupport.TryResolve([ImageFile()], caps, out _, out var error));
        StringAssert.Contains(error!, "qwen2.5-14b-instruct");
        StringAssert.Contains(error!, "image");
    }

    [TestMethod]
    public void PdfToVisionOnlyModel_IsRejected()
    {
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "llava:13b")));
        Assert.IsTrue(caps.Images);
        Assert.IsFalse(caps.Documents);

        var pdf = File("report.pdf", "application/pdf", Encoding.UTF8.GetBytes("%PDF-1.7"));
        Assert.IsFalse(AttachmentSupport.TryResolve([pdf], caps, out _, out var error));
        StringAssert.Contains(error!, "PDF");
    }

    [TestMethod]
    public void TextFiles_AreAcceptedByEveryModel()
    {
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "phi4-mini")));

        Assert.IsTrue(AttachmentSupport.TryResolve([TextFile("notes.md", "# hi")], caps, out var resolved, out var error));
        Assert.IsNull(error);
        Assert.AreEqual(1, resolved.Count);
        Assert.IsTrue(resolved[0].IsText);
        Assert.AreEqual("# hi", resolved[0].Text);
    }

    [TestMethod]
    public void SourceFileWithNoBrowserMediaType_IsTreatedAsText()
    {
        // Browsers report "" or application/octet-stream for .cs/.py/.ts, so the extension has
        // to decide — otherwise ordinary code files would be rejected as unknown binaries.
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "phi4-mini")));
        var file = File("Program.cs", "application/octet-stream", Encoding.UTF8.GetBytes("class C {}"));

        Assert.IsTrue(AttachmentSupport.TryResolve([file], caps, out var resolved, out _));
        Assert.IsTrue(resolved[0].IsText);
        Assert.AreEqual("class C {}", resolved[0].Text);
    }

    [TestMethod]
    public void BinaryContentWearingATextExtension_IsRejected()
    {
        // Inlining undecodable bytes would poison the prompt with mojibake.
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "phi4-mini")));
        var file = File("data.txt", "text/plain", [0xFF, 0xFE, 0x00, 0x80, 0x81]);

        Assert.IsFalse(AttachmentSupport.TryResolve([file], caps, out _, out var error));
        StringAssert.Contains(error!, "UTF-8");
    }

    [TestMethod]
    public void OversizedFile_AndTooManyFiles_AreRejected()
    {
        var caps = AttachmentSupport.Resolve(Config(
            ("LLM:Model", "gpt-4o"),
            ("LLM:Attachments:MaxFileSizeMb", "1"),
            ("LLM:Attachments:MaxFilesPerMessage", "2")));

        var big = File("big.png", "image/png", new byte[2 * 1024 * 1024]);
        Assert.IsFalse(AttachmentSupport.TryResolve([big], caps, out _, out var sizeError));
        StringAssert.Contains(sizeError!, "limit");

        var many = new[] { TextFile("a.txt", "a"), TextFile("b.txt", "b"), TextFile("c.txt", "c") };
        Assert.IsFalse(AttachmentSupport.TryResolve(many, caps, out _, out var countError));
        StringAssert.Contains(countError!, "Too many");
    }

    [TestMethod]
    public void MalformedBase64_IsRejected()
    {
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "gpt-4o")));
        var bad = new ChatAttachment { Name = "x.png", MediaType = "image/png", Data = "not-base64!!" };

        Assert.IsFalse(AttachmentSupport.TryResolve([bad], caps, out _, out var error));
        StringAssert.Contains(error!, "base64");
    }

    [TestMethod]
    public void NoAttachments_IsAlwaysValid()
    {
        var caps = AttachmentSupport.Resolve(Config(("LLM:Model", "phi4-mini")));

        Assert.IsTrue(AttachmentSupport.TryResolve(null, caps, out var resolved, out var error));
        Assert.AreEqual(0, resolved.Count);
        Assert.IsNull(error);
    }

    // ── Prompt conversion ───────────────────────────────────────────────────

    [TestMethod]
    public void TextAttachment_IsInlinedAsDelimitedText()
    {
        var (contents, note) = AttachmentSupport.ConvertForPrompt([TextFile("notes.md", "hello world")]);

        Assert.AreEqual(1, contents.Count);
        var text = ((TextContent)contents[0]).Text;
        StringAssert.Contains(text, "name=\"notes.md\"");
        StringAssert.Contains(text, "hello world");
        StringAssert.Contains(note, "notes.md");
    }

    [TestMethod]
    public void ImageAttachment_BecomesLabelledDataContent()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var (contents, note) = AttachmentSupport.ConvertForPrompt([File("shot.png", "image/png", bytes)]);

        Assert.AreEqual(2, contents.Count, "Expected a name label followed by the image data.");
        StringAssert.Contains(((TextContent)contents[0]).Text, "shot.png");

        var data = (DataContent)contents[1];
        Assert.AreEqual("image/png", data.MediaType);
        CollectionAssert.AreEqual(bytes, data.Data.ToArray());
        StringAssert.Contains(note, "image/png");
    }

    [TestMethod]
    public void UndecodableAttachment_IsSkippedRatherThanFailingAnInFlightTurn()
    {
        // Policy already ran at the HTTP edge; by this point the turn is in progress and
        // dropping one bad part beats throwing away the user's whole message.
        var good = TextFile("ok.txt", "fine");
        var bad = new ChatAttachment { Name = "bad.txt", MediaType = "text/plain", Data = "%%%" };

        var (contents, note) = AttachmentSupport.ConvertForPrompt([bad, good]);

        Assert.AreEqual(1, contents.Count);
        StringAssert.Contains(note, "ok.txt");
        Assert.IsFalse(note.Contains("bad.txt"));
    }

    [TestMethod]
    public void NoAttachments_ProduceNoContentAndNoNote()
    {
        var (contents, note) = AttachmentSupport.ConvertForPrompt(null);
        Assert.AreEqual(0, contents.Count);
        Assert.AreEqual(string.Empty, note);
    }
}
