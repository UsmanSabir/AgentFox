namespace AgentFox.ChannelTests;

[TestClass]
public sealed class InstallerConfigurationSafetyTests
{
    [TestMethod]
    public void BothInstallersStageMigrateAndPreserveUserConfiguration()
    {
        var root = FindRepositoryRoot();
        var powerShell = File.ReadAllText(Path.Combine(root, "install.ps1"));
        var bash = File.ReadAllText(Path.Combine(root, "install.sh"));

        AssertInstallerContract(powerShell, "appsettings.user.json", "appsettings.defaults.json");
        AssertInstallerContract(bash, "appsettings.user.json", "appsettings.defaults.json");

        StringAssert.Contains(powerShell, "Config migrate --config", StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(bash, "config migrate --config");
        StringAssert.Contains(powerShell, "install-state.json");
        StringAssert.Contains(bash, "install-state.json");
        StringAssert.Contains(powerShell, "AGENTFOX_NO_TRADING");
        StringAssert.Contains(bash, "AGENTFOX_NO_TRADING");
    }

    private static void AssertInstallerContract(string script, string userFile, string defaultsFile)
    {
        StringAssert.Contains(script, userFile);
        StringAssert.Contains(script, defaultsFile);
        StringAssert.Contains(script, "candidate");
        StringAssert.Contains(script, "backups");
        StringAssert.Contains(script, "stage", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "install.ps1")) &&
                File.Exists(Path.Combine(current.FullName, "install.sh")))
                return current.FullName;
            current = current.Parent;
        }

        Assert.Fail("Could not locate the AgentFox repository root.");
        return string.Empty;
    }
}
