using TradingAgent;

namespace AgentFox.ChannelTests;

/// <summary>
/// The dashboard is embedded in TradingAgent.Core, not in an entry plugin, so every edition serves
/// the same bundle and a premium edition extends this UI through data rather than by shipping a
/// second page.
///
/// <para>
/// This is worth a test because both of its failure modes are SILENT. If the embedded-files manifest
/// or the wwwroot glob is lost, the plugin still builds and still runs — it just contributes no
/// Trading page, and nobody notices until someone opens the dashboard. And if the assembly the file
/// provider reads from drifts back to the entry assembly, the community edition keeps working while
/// premium ships with no UI at all.
/// </para>
/// </summary>
[TestClass]
public class TradingCoreUiTests
{
    [TestMethod]
    public void GetCorePages_ServesTheDashboardFromTheCoreAssembly()
    {
        var pages = TradingAgentRuntime.GetCorePages().ToList();

        // A backend-only build (no npm build run) legitimately contributes no page, so an empty
        // result is not a failure — but this suite runs after a full build, so assert the real case.
        Assert.AreEqual(1, pages.Count, "Core contributed no UI page. Either the UI was not built "
            + "(cd src/Plugins/TradingAgent/ui && npm run build) or the EmbeddedResource glob / "
            + "GenerateEmbeddedFilesManifest was lost from TradingAgent.Core.csproj.");

        var page = pages[0];
        Assert.AreEqual("trading", page.Slug);
        Assert.IsTrue(page.Assets!.GetFileInfo("index.html").Exists,
            "The page was contributed but its entry document is missing — that is a dead link in the "
            + "host navigation, which is exactly what GetCorePages is supposed to avoid.");
    }

    [TestMethod]
    public void TheDashboardIsEmbeddedInCore_NotInTheEntryPlugin()
    {
        // Reading the resources off the runtime's own assembly is what makes one bundle serve every
        // edition. If this moves to the entry assembly, premium ships without a dashboard.
        var core = typeof(TradingAgentRuntime).Assembly;
        Assert.AreEqual("TradingAgent.Core", core.GetName().Name);

        var wwwroot = core.GetManifestResourceNames()
            .Where(n => n.Contains("wwwroot", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.IsTrue(wwwroot.Count > 0,
            $"TradingAgent.Core embeds no wwwroot resources. Found: {string.Join(", ", core.GetManifestResourceNames())}");

        Assert.AreNotEqual("TradingAgent.Core", typeof(TradingAgentModule).Assembly.GetName().Name,
            "The entry module should live in the entry assembly, separate from the engine.");
    }
}
