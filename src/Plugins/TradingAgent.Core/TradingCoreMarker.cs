namespace TradingAgent;

/// <summary>
/// Presence marker proving the trading engine has already been composed into a service collection.
/// Registered by <see cref="TradingAgentRuntime.AddCore"/>, which refuses to run twice.
///
/// <para>
/// Why a service registration and not a static flag: two entry plugins mean two
/// <c>PluginLoadContext</c> instances, each with its OWN copy of this assembly and therefore its own
/// copy of every static field — a static flag set by one edition is invisible to the other. The
/// <c>IServiceCollection</c> is the one thing they genuinely share: the host passes the same
/// instance to every module's <c>RegisterServices</c>.
/// </para>
///
/// <para>
/// And why the check compares type NAMES rather than types: across two load contexts these are two
/// distinct <see cref="Type"/> objects that are never equal, so <c>GetService&lt;TradingCoreMarker&gt;</c>
/// or a typed descriptor comparison would not see the other edition's registration. The full name is
/// the only identity that survives the context boundary.
/// </para>
/// </summary>
internal sealed class TradingCoreMarker
{
    /// <summary>
    /// The name the guard matches on. Must stay in sync with this type's namespace and name — the
    /// test asserts they agree, because a rename that missed this constant would silently disable
    /// the guard rather than break the build.
    /// </summary>
    internal const string TypeName = "TradingAgent.TradingCoreMarker";
}
