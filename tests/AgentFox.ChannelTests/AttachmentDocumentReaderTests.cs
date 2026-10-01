using AgentFox.LLM;
using AgentFox.Plugins.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

/// <summary>
/// Covers chat attachments handed to the document-reading MCP server (anymd): when they are
/// accepted, what the model receives in their place, and that the saved copies expire.
/// </summary>
[TestClass]
public sealed class AttachmentDocumentReaderTests
{
    private string _dir = string.Empty;

    [TestInitialize]
    public void SetUp() =>
        _dir = Path.Combine(Path.GetTempPath(), "docreader_" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AttachmentDocumentReader Reader(bool connected = true, bool modelReadsPdf = false) =>
        new("anymd", _dir, 7, () => connected, () => modelReadsPdf);

    private static IConfiguration TextOnlyModel() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new("LLM:Model", "qwen2.5-14b-instruct")])
            .Build();

    private static ChatAttachment Docx(string name = "report.docx") => new()
    {
        Name = name,
        MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        Data = Convert.ToBase64String([0x50, 0x4B, 0x03, 0x04, 1, 2, 3]),
    };

    private static ChatAttachment Pdf() => new()
    {
        Name = "scan.pdf", MediaType = "application/pdf", Data = Convert.ToBase64String("%PDF-1.7"u8.ToArray()),
    };

    // ── Acceptance ───────────────────────────────────────────────────────────

    [TestMethod]
    public void OfficeDocumentsAreOfferedOnlyWhileTheReaderIsConnected()
    {
        var on = AttachmentSupport.Resolve(TextOnlyModel(), Reader(connected: true));
        Assert.IsTrue(on.ConvertedDocuments);
        CollectionAssert.Contains(on.AcceptedMediaTypes.ToList(), ".docx");
        Assert.IsTrue(AttachmentSupport.TryResolve([Docx()], on, out var resolved, out _));
        Assert.AreEqual(Docx().MediaType, resolved.Single().MediaType);

        var off = AttachmentSupport.Resolve(TextOnlyModel(), Reader(connected: false));
        Assert.IsFalse(off.ConvertedDocuments);
        CollectionAssert.DoesNotContain(off.AcceptedMediaTypes.ToList(), ".docx");
        Assert.IsFalse(AttachmentSupport.TryResolve([Docx()], off, out _, out var error));
        StringAssert.Contains(error, "no document reader");
    }

    [TestMethod]
    public void DocxIsRecognisedByExtensionWhenTheBrowserSendsNoType()
    {
        Assert.AreEqual(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            AttachmentSupport.ResolveMediaType("Q3 Report.DOCX", "application/octet-stream"));
    }

    [TestMethod]
    public void PdfIsAcceptedForATextOnlyModelWhenTheReaderCanReadIt()
    {
        var caps = AttachmentSupport.Resolve(TextOnlyModel(), Reader());
        Assert.IsFalse(caps.Documents);
        Assert.IsTrue(AttachmentSupport.TryResolve([Pdf()], caps, out _, out _));
    }

    // ── What the model receives ──────────────────────────────────────────────

    [TestMethod]
    public void DocumentIsSavedAndTheModelIsToldToReadItThroughMcp()
    {
        var (contents, note) = AttachmentSupport.ConvertForPrompt([Docx()], Reader(), "conv-1");

        Assert.IsFalse(contents.OfType<DataContent>().Any(), "The bytes must not be sent to a model that cannot read them.");
        var text = contents.OfType<TextContent>().Single().Text;
        StringAssert.Contains(text, "`read` tool from the `anymd` MCP server");

        var saved = Directory.GetFiles(Path.Combine(_dir, "conv-1")).Single();
        StringAssert.EndsWith(saved, "-report.docx");
        StringAssert.Contains(text, $"path=\"{saved}\"");
        CollectionAssert.AreEqual(Convert.FromBase64String(Docx().Data!), File.ReadAllBytes(saved));
        StringAssert.Contains(note, "report.docx");
    }

    [TestMethod]
    public void PdfStillGoesNativeToAModelThatReadsPdf()
    {
        var (contents, _) = AttachmentSupport.ConvertForPrompt([Pdf()], Reader(modelReadsPdf: true), "conv-1");

        Assert.AreEqual(1, contents.OfType<DataContent>().Count());
        Assert.IsFalse(Directory.Exists(_dir), "Nothing is saved when the model reads the file itself.");
    }

    [TestMethod]
    public void PdfGoesToTheReaderForAModelThatCannotReadPdf()
    {
        var (contents, _) = AttachmentSupport.ConvertForPrompt([Pdf()], Reader(modelReadsPdf: false), "conv-1");

        Assert.IsFalse(contents.OfType<DataContent>().Any());
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_dir, "conv-1")).Length);
    }

    [TestMethod]
    public void WithoutAReaderAttachmentsConvertExactlyAsBefore()
    {
        var (contents, _) = AttachmentSupport.ConvertForPrompt([Pdf()]);
        Assert.AreEqual(1, contents.OfType<DataContent>().Count());
    }

    [TestMethod]
    public void HostileNamesCannotEscapeTheAttachmentFolder()
    {
        var reader = Reader();
        var path = reader.Save("../../etc", "..\\..\\evil.docx", [1], DateTime.UtcNow);

        StringAssert.StartsWith(Path.GetFullPath(path), Path.GetFullPath(_dir));
        StringAssert.EndsWith(path, "evil.docx");
    }

    [TestMethod]
    public void LongNamesKeepTheirExtension()
    {
        var path = Reader().Save("c", new string('x', 300) + ".pptx", [1], DateTime.UtcNow);
        StringAssert.EndsWith(path, ".pptx");
    }

    // ── Retention ────────────────────────────────────────────────────────────

    [TestMethod]
    public void EverySaveSweepsExpiredCopiesAndEmptyFolders()
    {
        var reader = Reader();
        var old = reader.Save("old-conv", "old.docx", [1], DateTime.UtcNow);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));

        reader.Save("new-conv", "new.docx", [1], DateTime.UtcNow);

        Assert.IsFalse(File.Exists(old));
        Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "old-conv")));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(_dir, "new-conv")).Length);
    }
}
