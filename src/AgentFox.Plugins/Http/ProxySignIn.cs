using System.Net;

namespace AgentFox.Http;

/// <summary>
/// Lets every HTTP client in the process sign in to the machine's proxy with the Windows identity the
/// process runs as — what a browser on the same network already does.
///
/// <para>
/// .NET follows the system proxy (Windows settings, or <c>HTTPS_PROXY</c>) on its own but answers the
/// proxy's sign-in challenge with nothing, so on an authenticating corporate proxy every call dies at the
/// tunnel with 407. <see cref="HttpResilienceFactory"/> already fixes that for the clients it builds; this
/// covers the rest — the LLM and embedding SDKs, channel libraries, and any plugin that news up a plain
/// <see cref="HttpClient"/> — which all read <see cref="HttpClient.DefaultProxy"/>. It changes no route:
/// a request goes through a proxy only if the machine already names one, and is ignored by a proxy that
/// asks for no sign-in.
/// </para>
/// </summary>
public static class ProxySignIn
{
    /// <summary>Call once at startup. Credentials something else has already set are kept.</summary>
    public static void UseWindowsIdentityForSystemProxy() =>
        HttpClient.DefaultProxy.Credentials ??= CredentialCache.DefaultCredentials;
}
