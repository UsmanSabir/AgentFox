using AgentFox.Modules.Web;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ManagementAuthenticationTests
{
    [TestMethod]
    public async Task EnabledManagementAuthentication_RejectsMissingAndInvalidKeys()
    {
        using var server = CreateServer(enabled: true);
        using var client = server.CreateClient();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/secure")).StatusCode);
        client.DefaultRequestHeaders.Add("X-AgentFox-Api-Key", "wrong");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/secure")).StatusCode);
    }

    [TestMethod]
    public async Task AdministratorKey_InheritsManagementAndTradingPolicies()
    {
        using var server = CreateServer(enabled: true);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-AgentFox-Api-Key", "correct-secret");

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/secure")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/risk")).StatusCode);
    }

    [TestMethod]
    public async Task DisabledManagementAuthentication_AllowsLocalDevelopmentIdentity()
    {
        using var server = CreateServer(enabled: false);
        using var client = server.CreateClient();
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/risk")).StatusCode);
    }

    private static TestServer CreateServer(bool enabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["Web:ManagementAuth:Enabled"] = enabled.ToString(),
            ["Web:ManagementAuth:ApiKeys:0:Name"] = "test-admin",
            ["Web:ManagementAuth:ApiKeys:0:Key"] = "correct-secret",
            ["Web:ManagementAuth:ApiKeys:0:Roles:0"] = ManagementRoles.Administrator
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddManagementAuthentication(configuration);
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/secure", () => Results.Ok())
                        .RequireAuthorization("ManagementViewer");
                    endpoints.MapGet("/risk", () => Results.Ok())
                        .RequireAuthorization("TradingRiskManager");
                });
            });
        return new TestServer(builder);
    }
}
