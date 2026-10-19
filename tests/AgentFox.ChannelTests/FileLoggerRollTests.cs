using AgentFox.Helpers;

namespace AgentFox.ChannelTests;

/// <summary>
/// The file log has a size cap: at the cap it is moved to "agentfox.1.log" (replacing the last one), so the log
/// never takes more than about twice the cap. Before this, nothing ever trimmed the live file — RetentionDays
/// deletes only files untouched for days, and the live file is written every day.
/// </summary>
[TestClass]
public sealed class FileLoggerRollTests
{
    private string _dir = "";

    [TestInitialize]
    public void Setup() => Directory.CreateDirectory(_dir = Path.Combine(Path.GetTempPath(), "agentfox-logroll-" + Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void A_full_log_is_moved_aside_and_replaces_the_previous_one()
    {
        var log = Path.Combine(_dir, "agentfox.log");
        var rolled = FileLogger.RolledPath(log);
        Assert.AreEqual(Path.Combine(_dir, "agentfox.1.log"), rolled, "still a *.log, so the age sweep covers it");

        File.WriteAllText(rolled, "older");
        File.WriteAllText(log, new string('x', 2048));

        FileLogger.RollIfFull(log, maxBytes: 1024);

        Assert.IsFalse(File.Exists(log), "a fresh file starts with the next line");
        Assert.AreEqual(2048, new FileInfo(rolled).Length, "the previous rolled file is replaced, not kept");
    }

    [TestMethod]
    public void A_log_under_the_cap_or_with_no_cap_is_left_alone()
    {
        var log = Path.Combine(_dir, "agentfox.log");
        File.WriteAllText(log, new string('x', 100));

        FileLogger.RollIfFull(log, maxBytes: 1024);
        FileLogger.RollIfFull(log, maxBytes: 0);

        Assert.IsTrue(File.Exists(log));
        Assert.IsFalse(File.Exists(FileLogger.RolledPath(log)));
    }
}
