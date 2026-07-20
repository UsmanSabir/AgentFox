using System.Reflection;

namespace AgentFox.Helpers;

/// <summary>
/// Exposes the build version baked into the assembly at compile time.
///
/// The CI release workflow (.github/workflows/release.yml) passes
/// <c>-p:Version</c> / <c>-p:InformationalVersion</c> so the value auto-increments
/// per build (e.g. <c>1.0.&lt;run_number&gt;+&lt;sha&gt;</c>). Local builds fall back to the
/// <c>&lt;VersionPrefix&gt;</c> declared in AgentFox.csproj.
/// </summary>
public static class VersionInfo
{
    /// <summary>Semantic version without build metadata, e.g. "1.0.42".</summary>
    public static string Version { get; }

    /// <summary>Full informational version incl. any "+&lt;sha&gt;" suffix, e.g. "1.0.42+ab12cd3".</summary>
    public static string Full { get; }

    /// <summary>Short git commit the build was produced from, or "" when unknown.</summary>
    public static string Commit { get; }

    /// <summary>Ready-to-print form, e.g. "v1.0.42 (ab12cd3)".</summary>
    public static string Display { get; }

    static VersionInfo()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(VersionInfo).Assembly;

        // AssemblyInformationalVersion carries the richest value (may include "+sha");
        // fall back to the numeric AssemblyVersion if the attribute is absent.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";

        Full = info;

        var plus = info.IndexOf('+');
        if (plus >= 0)
        {
            Version = info[..plus];
            Commit  = info[(plus + 1)..];
        }
        else
        {
            Version = info;
            Commit  = string.Empty;
        }

        Display = Commit.Length > 0 ? $"v{Version} ({Commit})" : $"v{Version}";
    }
}
