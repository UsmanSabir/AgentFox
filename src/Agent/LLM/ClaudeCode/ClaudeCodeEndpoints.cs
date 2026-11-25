using System.Text.RegularExpressions;
using AgentFox.LLM.Subscription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentFox.LLM.ClaudeCode;

public sealed record ClaudeCodeChoicesRequest(string? Model);
public sealed record ClaudeCodeTokenRequest(string? Token);
public sealed record ClaudeCodeCodeRequest(string? Code);

/// <summary>
/// <c>/api/ai/claude-code/*</c>. Reading status needs the viewer role (the /api group's own rule); everything that
/// changes something, or shows a sign-in link, needs an administrator. No response ever carries the token.
/// </summary>
public static partial class ClaudeCodeEndpoints
{
    private const string Admin = "ManagementAdministrator";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/ai/claude-code");

        group.MapGet("", async (ClaudeCodeStatusService status, bool? refresh, CancellationToken ct) =>
            Results.Ok(await status.GetAsync(refresh == true, ct)));

        group.MapPut("/settings", async (ClaudeCodeStore store, ClaudeCodeStatusService status,
            ClaudeCodeChoicesRequest req, CancellationToken ct) =>
        {
            if (req.Model is { } m && !string.IsNullOrWhiteSpace(m) && !ModelName().IsMatch(m.Trim()))
                return Results.BadRequest(new { error = "Enter a model name such as sonnet, opus or haiku." });
            store.SaveModel(req.Model);
            return Results.Ok(await status.GetAsync(false, ct));
        }).RequireAuthorization(Admin);

        // A pasted token is tried BEFORE it replaces the saved one: a wrong paste must not break a working sign-in.
        group.MapPut("/token", async (ClaudeCodeStore store, ClaudeCodeChatClient claude, ClaudeCodeActivity activity,
            ClaudeCodeStatusService status, AiClientRouter router, ClaudeCodeTokenRequest req, CancellationToken ct) =>
        {
            var token = (req.Token ?? "").Trim();
            if (!ClaudeCodeTerminalText.IsWellFormedToken(token))
                return Results.BadRequest(new { error = "That is not a Claude Code token. It starts with sk-ant-oat01-." });
            if (!(await router.ReadAccessAsync(AiProviders.ClaudeCode, ct)).Allowed)
                return Results.BadRequest(new { error = "Claude Code is not available for this account." });
            var (passed, message) = await claude.TestAsync(token, ct);
            if (!passed)
                return Results.BadRequest(new { error = $"The token was not saved: {message}" });
            store.SaveToken(token, "paste");
            activity.ClearErrors();
            activity.RecordTest(true);
            return Results.Ok(await status.GetAsync(false, ct));
        }).RequireAuthorization(Admin);

        group.MapDelete("/token", async (ClaudeCodeStore store, ClaudeCodeActivity activity, ClaudeCodeStatusService status,
            CancellationToken ct) =>
        {
            store.RemoveToken();
            activity.ClearErrors();
            return Results.Ok(await status.GetAsync(false, ct));
        }).RequireAuthorization(Admin);

        group.MapPost("/test", async (ClaudeCodeChatClient claude, ClaudeCodeStatusService status, AiClientRouter router,
            CancellationToken ct) =>
        {
            if (!(await router.ReadAccessAsync(AiProviders.ClaudeCode, ct)).Allowed)
                return Results.BadRequest(new { error = "Claude Code is not available for this account." });
            var (passed, message) = await claude.TestAsync(null, ct);
            return Results.Ok(new { passed, message, status = await status.GetAsync(false, ct) });
        }).RequireAuthorization(Admin);

        group.MapGet("/signin", (ClaudeCodeSignIn signIn) => Results.Ok(signIn.Current)).RequireAuthorization(Admin);

        group.MapPost("/signin", async (ClaudeCodeSignIn signIn, AiClientRouter router, CancellationToken ct) =>
        {
            if (!(await router.ReadAccessAsync(AiProviders.ClaudeCode, ct)).Allowed)
                return Results.BadRequest(new { error = "Claude Code is not available for this account." });
            return Results.Ok(await signIn.StartAsync(ct));
        }).RequireAuthorization(Admin);

        group.MapPost("/signin/code", async (ClaudeCodeSignIn signIn, ClaudeCodeChatClient claude, ClaudeCodeCodeRequest req,
            CancellationToken ct) =>
        {
            var result = await signIn.SubmitCodeAsync(req.Code ?? "", ct);
            if (result.State == ClaudeCodeSignInState.Completed)
                await claude.TestAsync(null, ct); // a fresh token from Claude itself is kept; the test only fills the card
            return Results.Ok(result);
        }).RequireAuthorization(Admin);

        group.MapDelete("/signin", (ClaudeCodeSignIn signIn) => Results.Ok(signIn.Cancel())).RequireAuthorization(Admin);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\[\]-]{1,100}$")] private static partial Regex ModelName();
}
