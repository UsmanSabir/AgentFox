using AgentFox.LLM;
using AgentFox.Memory;
using AgentFox.Plugins.Models;
using Microsoft.Extensions.AI;
using System.Text;

namespace AgentFox.ChannelTests;

/// <summary>
/// A user turn carrying attachments becomes a multi-part <see cref="ChatMessage"/>. These cover
/// how that message lands in the markdown transcript, which is also what session export and
/// reload read back.
/// </summary>
[TestClass]
public sealed class AttachmentTranscriptTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "attachtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ChatMessage UserTurnWith(string text, params ChatAttachment[] attachments)
    {
        var (contents, _) = AttachmentSupport.ConvertForPrompt(attachments);
        return new ChatMessage(ChatRole.User, [new TextContent(text), .. contents]);
    }

    [TestMethod]
    public void ImageBytes_StayOutOfTheTranscript_ButTheFileNameIsRecorded()
    {
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";

        // Distinctive bytes so a base64 leak into the transcript is unmistakable.
        var image = new ChatAttachment
        {
            Name = "screenshot.png",
            MediaType = "image/png",
            Data = Convert.ToBase64String(Enumerable.Repeat((byte)0xAB, 4096).ToArray())
        };

        store.AppendForTest(conv, UserTurnWith("what is wrong here?", image));
        store.AppendForTest(conv, new ChatMessage(ChatRole.Assistant, "Looks fine."));

        var transcript = File.ReadAllText(Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories).Single());

        StringAssert.Contains(transcript, "what is wrong here?");
        StringAssert.Contains(transcript, "screenshot.png");
        Assert.IsFalse(transcript.Contains("q6urq6ur"),
            "Base64 image bytes must never be written into the transcript.");
    }

    [TestMethod]
    public void UserText_IsNotDuplicated_WhenTheTurnCarriesBinaryParts()
    {
        // Regression guard: a DataContent part previously fell through to the transcript
        // writer's default branch, which re-emitted the whole message's text.
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";

        var pdf = new ChatAttachment
        {
            Name = "report.pdf",
            MediaType = "application/pdf",
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7 fake"))
        };

        store.AppendForTest(conv, UserTurnWith("summarise this", pdf));

        var transcript = File.ReadAllText(Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories).Single());
        var occurrences = transcript.Split("summarise this").Length - 1;

        Assert.AreEqual(1, occurrences, $"Expected the question once, found it {occurrences} times.");
    }

    [TestMethod]
    public void TextAttachment_ContentIsPreservedInTheTranscript()
    {
        // Text files are inlined as prompt text, so unlike binary parts they should survive
        // a reload — the model saw them as text and so should the transcript.
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";

        var file = new ChatAttachment
        {
            Name = "config.json",
            MediaType = "application/json",
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"retries\":3}"))
        };

        store.AppendForTest(conv, UserTurnWith("is this right?", file));

        var transcript = File.ReadAllText(Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories).Single());

        StringAssert.Contains(transcript, "config.json");
        StringAssert.Contains(transcript, "{\"retries\":3}");
    }
}
