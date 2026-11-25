using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentFox.LLM.Subscription;

public sealed record AiRoutingRequest(string? Purpose, string? Provider);

/// <summary><c>/api/ai/routing</c>: which provider each purpose uses. Reading needs the viewer role; changing an
/// administrator.</summary>
public static class AiRoutingEndpoints
{
    private static readonly Dictionary<string, string> PurposeLabels = new()
    {
        [AiPurposes.Chat] = "Chat",
        [AiPurposes.StockAssessment] = "Stock assessments",
        [AiPurposes.RebuyAdvice] = "Rebuy “Ask AI”",
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/ai/routing");
        group.MapGet("", async (AiClientRouter router, AiRoutingStore routing, CancellationToken ct) =>
            Results.Ok(await ReadAsync(router, routing, ct)));

        group.MapPut("", async (AiClientRouter router, AiRoutingStore routing, AiRoutingRequest req, CancellationToken ct) =>
        {
            if (req.Purpose is not { } purpose || !AiRoutingStore.Purposes.Contains(purpose))
                return Results.BadRequest(new { error = "Unknown purpose." });
            if (req.Provider is not { } provider || !AiRoutingStore.Providers.Contains(provider))
                return Results.BadRequest(new { error = "Unknown provider." });
            routing.Set(purpose, provider);
            return Results.Ok(await ReadAsync(router, routing, ct));
        }).RequireAuthorization("ManagementAdministrator");
    }

    private static async Task<object> ReadAsync(AiClientRouter router, AiRoutingStore routing, CancellationToken ct)
    {
        var purposes = new List<object>();
        foreach (var purpose in AiRoutingStore.Purposes)
            purposes.Add(new
            {
                id = purpose,
                label = PurposeLabels.GetValueOrDefault(purpose, purpose),
                provider = routing.ProviderFor(purpose),
                inUse = await router.ProviderInUseAsync(purpose, ct),
            });
        var providers = new List<object> { new { id = AiProviders.Configured, label = "Configured AI", signedIn = true, installed = true, allowed = true } };
        foreach (var p in router.Providers)
            providers.Add(new { id = p.Id, label = p.DisplayName, signedIn = p.IsSignedIn, installed = p.IsInstalled,
                allowed = (await router.ReadAccessAsync(p.Id, ct)).Allowed });
        return new { purposes, providers };
    }
}
