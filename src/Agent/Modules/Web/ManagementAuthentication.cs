using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentFox.Modules.Web;

public static class ManagementRoles
{
    public const string Viewer = "Viewer";
    public const string Analyst = "Analyst";
    public const string Trader = "Trader";
    public const string RiskManager = "RiskManager";
    public const string Administrator = "Administrator";

    public static readonly string[] All =
        [Viewer, Analyst, Trader, RiskManager, Administrator];
}

public sealed class ManagementAuthOptions
{
    public const string SectionName = "Web:ManagementAuth";
    public bool Enabled { get; set; }
    public string HeaderName { get; set; } = "X-AgentFox-Api-Key";
    public List<ManagementApiKey> ApiKeys { get; set; } = [];
}

public sealed class ManagementApiKey
{
    public string Name { get; set; } = "operator";
    public string Key { get; set; } = "";
    public List<string> Roles { get; set; } = [ManagementRoles.Viewer];
}

public sealed class ManagementApiKeyHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AgentFoxManagementApiKey";
    private readonly IOptionsMonitor<ManagementAuthOptions> _managementOptions;

    public ManagementApiKeyHandler(
        IOptionsMonitor<ManagementAuthOptions> managementOptions,
        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(schemeOptions, logger, encoder)
    {
        _managementOptions = managementOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = _managementOptions.CurrentValue;
        if (!options.Enabled)
            return Task.FromResult(Success("local-development", ManagementRoles.All));

        if (!Request.Headers.TryGetValue(options.HeaderName, out var suppliedValues)
            || string.IsNullOrWhiteSpace(suppliedValues.FirstOrDefault()))
            return Task.FromResult(AuthenticateResult.NoResult());

        var supplied = suppliedValues.First()!;
        foreach (var configured in options.ApiKeys.Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            if (!FixedTimeEquals(supplied, configured.Key)) continue;
            var roles = ExpandRoles(configured.Roles);
            return Task.FromResult(Success(
                string.IsNullOrWhiteSpace(configured.Name) ? "operator" : configured.Name,
                roles));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid management API key."));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Response.WriteAsJsonAsync(new
        {
            error = "management_authentication_required",
            message = "A valid management API key is required."
        });
    }

    private AuthenticateResult Success(string actor, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, actor),
            new(ClaimTypes.Name, actor)
        };
        claims.AddRange(roles.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static IReadOnlyList<string> ExpandRoles(IEnumerable<string> configured)
    {
        var roles = new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);
        if (roles.Contains(ManagementRoles.Administrator))
            roles.UnionWith(ManagementRoles.All);
        if (roles.Contains(ManagementRoles.RiskManager))
            roles.UnionWith([ManagementRoles.Viewer, ManagementRoles.Analyst]);
        if (roles.Contains(ManagementRoles.Trader))
            roles.UnionWith([ManagementRoles.Viewer, ManagementRoles.Analyst]);
        if (roles.Contains(ManagementRoles.Analyst))
            roles.Add(ManagementRoles.Viewer);
        return roles.ToList();
    }

    private static bool FixedTimeEquals(string supplied, string configured)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public static class ManagementAuthenticationExtensions
{
    public static IServiceCollection AddManagementAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ManagementAuthOptions>(
            configuration.GetSection(ManagementAuthOptions.SectionName));
        services.AddAuthentication(ManagementApiKeyHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ManagementApiKeyHandler>(
                ManagementApiKeyHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy("ManagementViewer", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ManagementRoles.All))
            .AddPolicy("TradingAnalyst", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ManagementRoles.Analyst, ManagementRoles.Trader,
                    ManagementRoles.RiskManager, ManagementRoles.Administrator))
            .AddPolicy("TradingTrader", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ManagementRoles.Trader, ManagementRoles.Administrator))
            .AddPolicy("TradingRiskManager", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ManagementRoles.RiskManager, ManagementRoles.Administrator))
            .AddPolicy("ManagementAdministrator", policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ManagementRoles.Administrator));
        return services;
    }
}
