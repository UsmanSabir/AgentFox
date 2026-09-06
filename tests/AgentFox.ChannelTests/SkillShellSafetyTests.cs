using AgentFox.Skills;

namespace AgentFox.ChannelTests;

/// <summary>
/// The skill tools that shell out, and the two properties they lacked until 2026-09-06: arguments
/// cannot become commands, and <c>Tools:Shell</c> actually reaches them.
///
/// <para>
/// These run a real process rather than asserting on a built string, because the claim being made
/// is about what the OS does with an argument, not about how it was formatted. A test that checked
/// the command line would have passed on the old code too.
/// </para>
/// </summary>
[TestClass]
public sealed class SkillShellSafetyTests
{
    private string _scriptPath = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        SkillShellPolicy.Configure(true);

        // A program that prints its argv and nothing else. Picking one is the whole difficulty of
        // this fixture: `cmd /c echo` and `powershell -Command` both RE-PARSE what follows, so they
        // are interpreters — the very thing under test — and using either would make the test pass
        // or fail for reasons unrelated to the code. PowerShell's -File mode does not re-parse:
        // arguments after the script path arrive as literal argv entries.
        _scriptPath = Path.Combine(
            Path.GetTempPath(), $"agentfox-argv-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(_scriptPath, "foreach ($a in $args) { Write-Output $a }");
    }

    [TestCleanup]
    public void TearDown()
    {
        SkillShellPolicy.Configure(true);
        try { File.Delete(_scriptPath); } catch { /* best effort */ }
    }

    /// <summary>Runs the argv-printing program with <paramref name="payload"/> as ONE argument.</summary>
    private (string File, string[] Args) Echo(string payload) =>
        OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _scriptPath, payload])
            : ("/bin/echo", [payload]);

    [TestMethod]
    public async Task AnArgumentContainingShellMetacharactersIsPassedThroughLiterally()
    {
        // The exact shape that used to be exploitable: DockerBuildTool interpolated a model-supplied
        // tag into "docker build -t {tag} {path}" and handed the result to cmd.exe /c. A tag of
        // "x & whoami" ran whoami. With ArgumentList there is no command line to break out of.
        const string payload = "x & whoami";
        var (file, args) = Echo(payload);

        var result = await SkillShellHelper.RunAsync(file, args);

        Assert.IsTrue(result.Success, $"process failed: {result.Error}");
        StringAssert.Contains(result.Output, payload,
            "The metacharacters must survive as literal text — that is what proves they were not "
            + "interpreted.");
    }

    [TestMethod]
    public async Task ASemicolonSeparatedInjectionDoesNotRunASecondCommand()
    {
        const string payload = "safe; echo INJECTED";
        var (file, args) = Echo(payload);

        var result = await SkillShellHelper.RunAsync(file, args);

        Assert.IsTrue(result.Success, $"process failed: {result.Error}");
        StringAssert.Contains(result.Output, "safe; echo INJECTED");

        // The payload text itself contains "INJECTED", so presence proves nothing. What proves it
        // is that the word appears exactly ONCE — a second occurrence on its own line would be the
        // echoed output of a command that actually ran.
        var occurrences = result.Output.Split("INJECTED").Length - 1;
        Assert.AreEqual(1, occurrences,
            $"'INJECTED' appeared {occurrences} times; a second command executed. Output: {result.Output}");
    }

    [TestMethod]
    public async Task QuotesInAnArgumentDoNotTerminateAnything()
    {
        // db_query used to hand-roll a quote escape (query.Replace("\"", "\\\"")) and paste the SQL
        // inside a quoted shell argument. Escaping is a rule you can get wrong; not building a
        // command line is a property you cannot.
        const string payload = "SELECT \"a\" -- ' comment";
        var (file, args) = Echo(payload);

        var result = await SkillShellHelper.RunAsync(file, args);

        Assert.IsTrue(result.Success, $"process failed: {result.Error}");
        StringAssert.Contains(result.Output, "SELECT");
        StringAssert.Contains(result.Output, "comment");
    }

    [TestMethod]
    public async Task EmptyArgumentsAreSkippedRatherThanPassedAsPositionals()
    {
        // Several callers build a list conditionally — git push with no branch, docker run with no
        // name. An empty string is a real positional argument to the OS, not a no-op, so passing it
        // would give "git push origin ''" instead of "git push origin".
        var (file, args) = Echo("kept");
        var withEmpties = new List<string>(args) { "", "" };

        var result = await SkillShellHelper.RunAsync(file, withEmpties);

        Assert.IsTrue(result.Success, $"process failed: {result.Error}");
        StringAssert.Contains(result.Output, "kept");
    }

    [TestMethod]
    public async Task NoCommandRunsWhenToolsShellIsDisabled()
    {
        // The gap this closes: appsettings documents a sandboxed profile as Shell=false, but
        // Tools:Shell previously gated only ShellCommandTool. Every skill tool reached cmd.exe
        // independently of it, so the documented profile did not mean what it said.
        SkillShellPolicy.Configure(false);

        var (file, args) = Echo("should-not-run");
        var result = await SkillShellHelper.RunAsync(file, args);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Tools:Shell",
            "The refusal must name the setting responsible, or an operator cannot act on it.");
    }

    [TestMethod]
    public async Task TheGateAppliesToEveryShellingTool_NotJustTheOneUnderTest()
    {
        // Asserted through a real tool rather than the helper, because the value of a single gate
        // is precisely that a tool cannot opt out of it by forgetting to check.
        SkillShellPolicy.Configure(false);

        var result = await new GitStatusTool().ExecuteAsync(new Dictionary<string, object?>());

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "disabled by configuration");
    }
}
