using System.Text.RegularExpressions;
using AgentFox.LLM.Subscription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentFox.LLM.Codex;

public sealed record CodexSettingsRequest(string? Model, string? ReasoningEffort);

/// <summary><c>/api/ai/codex/*</c>. Reading status needs the viewer role; everything that changes something, or
/// shows a sign-in code, needs an administrator. No response carries a credential.</summary>
public static partial class CodexEndpoints
{
    private const string Admin = "ManagementAdministrator";
    private static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max", "ultra"];

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/ai/codex");

        group.MapGet("", async (CodexStatusService status, bool? refresh, CancellationToken ct) =>
            Results.Ok(await status.GetAsync(refresh == true, ct)));

        group.MapPut("/settings", async (CodexStore store, CodexStatusService status, CodexPaths paths, CodexSettingsRequest req, CancellationToken ct) =>
        {
            var model = req.Model?.Trim();
            if (model == CodexStatusService.DefaultModelLabel) model = null;
            if (!string.IsNullOrEmpty(model) && !ModelName().IsMatch(model))
                return Results.BadRequest(new { error = "Enter a model name the Codex CLI accepts." });
            var effort = string.IsNullOrWhiteSpace(req.ReasoningEffort) ? null : req.ReasoningEffort.Trim().ToLowerInvariant();
            if (effort is not null && !EffortAllowed(effort, model, paths))
                return Results.BadRequest(new { error = "That reasoning level is not offered for this model." });
            store.Save(model, effort);
            return Results.Ok(await status.GetAsync(false, ct));
        }).RequireAuthorization(Admin);

        group.MapPost("/test", async (CodexChatClient codex, CodexStatusService status, AiClientRouter router, CancellationToken ct) =>
        {
            if (!(await router.ReadAccessAsync(AiProviders.Codex, ct)).Allowed)
                return Results.BadRequest(new { error = "Codex is not available for this account." });
            var (passed, message) = await codex.TestAsync(ct);
            return Results.Ok(new { passed, message, status = await status.GetAsync(false, ct) });
        }).RequireAuthorization(Admin);

        group.MapGet("/signin", (CodexSignIn signIn) => Results.Ok(signIn.Current)).RequireAuthorization(Admin);

        group.MapPost("/signin", async (CodexSignIn signIn, AiClientRouter router, CancellationToken ct) =>
        {
            if (!(await router.ReadAccessAsync(AiProviders.Codex, ct)).Allowed)
                return Results.BadRequest(new { error = "Codex is not available for this account." });
            return Results.Ok(await signIn.StartAsync(ct));
        }).RequireAuthorization(Admin);

        group.MapDelete("/signin", (CodexSignIn signIn) => Results.Ok(signIn.Cancel())).RequireAuthorization(Admin);

        group.MapPost("/signout", async (CodexStatusService status, CancellationToken ct) =>
        {
            await status.SignOutAsync(ct);
            return Results.Ok(await status.GetAsync(true, ct));
        }).RequireAuthorization(Admin);
    }

    /// <summary>The plan's own catalog decides when it lists the model (its levels differ: some add "ultra"); otherwise
    /// any level the CLI is known to name.</summary>
    internal static bool EffortAllowed(string effort, string? model, CodexPaths paths)
    {
        var listed = model is null ? null : CodexModelCatalog.ReadCache(paths)
            .FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        return listed is { Efforts.Count: > 0 } ? listed.Efforts.Contains(effort) : Efforts.Contains(effort);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\[\]-]{1,100}$")] private static partial Regex ModelName();
}
