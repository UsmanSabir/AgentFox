using AgentFox.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentFox.Channels;

/// <summary>Mapped only inside the host's authenticated /api group.</summary>
public static class ChannelEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/channels/providers", (ChannelProviderCatalog catalog) => Results.Ok(new
        {
            providers = catalog.Providers.Select(p => new
            {
                type = p.ChannelType, name = p.DisplayName,
                fields = p.GetConfigSchema().Where(f => !f.Key.Equals("Name", StringComparison.OrdinalIgnoreCase)).Select(f => new
                {
                    key = f.Key, description = f.Value.Description, required = f.Value.Required,
                    secret = f.Value.Secret || IsSecret(f.Key)
                })
            })
        }));
        endpoints.MapGet("/channels", (ChannelManagerHolder holder, ChannelProviderCatalog catalog, ChannelConfigStore store) =>
        {
            if (holder.Manager == null) return Results.Ok(new { ready = false, channels = Array.Empty<object>() });
            try { return Results.Ok(new ChannelManagement(holder.Manager, catalog, store).Snapshot()); }
            catch { return Results.Json(new { error = "Could not read channel configuration." }, statusCode: 503); }
        });
        endpoints.MapPost("/channels", async (AddChannelRequest body, ChannelManagerHolder holder,
            ChannelProviderCatalog catalog, ChannelConfigStore store) => holder.Manager == null ? NotReady() :
            ToHttp(await new ChannelManagement(holder.Manager, catalog, store).AddAsync(body.Type, body.Name, body.Config, body.Subscribe)))
            .RequireAuthorization("ManagementAdministrator");
        endpoints.MapPut("/channels/{id}/enabled", async (string id, ChannelEnabledRequest body,
            ChannelManagerHolder holder, ChannelProviderCatalog catalog, ChannelConfigStore store) =>
            holder.Manager == null ? NotReady() : body.Enabled == null
                ? Results.BadRequest(new { error = "Enabled must be true or false." })
                : ToHttp(await new ChannelManagement(holder.Manager, catalog, store).SetEnabledAsync(id, body.Enabled.Value)))
            .RequireAuthorization("ManagementAdministrator");
        endpoints.MapDelete("/channels/{id}", async (string id, ChannelManagerHolder holder,
            ChannelProviderCatalog catalog, ChannelConfigStore store) => holder.Manager == null ? NotReady() :
            ToHttp(await new ChannelManagement(holder.Manager, catalog, store).RemoveAsync(id)))
            .RequireAuthorization("ManagementAdministrator");
        endpoints.MapPut("/channels/{id}/subscriptions", async (string id, ChannelSubscriptionUpdate body,
            ChannelManagerHolder holder, ChannelProviderCatalog catalog, ChannelConfigStore store) => holder.Manager == null ? NotReady() :
            ToHttp(await new ChannelManagement(holder.Manager, catalog, store).SubscribeAsync(id, body.Subscribe)))
            .RequireAuthorization("ManagementAdministrator");
    }

    private static bool IsSecret(string key) => key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase) || key.Contains("password", StringComparison.OrdinalIgnoreCase);
    private static IResult NotReady() => Results.Json(new { error = "Channel manager is not ready yet." }, statusCode: 503);
    private static IResult ToHttp(ChannelManagementResult result) => Results.Json(result, statusCode: result.StatusCode);
}

public sealed record AddChannelRequest(string? Type, string? Name, Dictionary<string, string>? Config, string? Subscribe);
public sealed record ChannelEnabledRequest(bool? Enabled);
public sealed record ChannelSubscriptionUpdate(string? Subscribe);
