using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TradingAgent.Research;

/// <summary>
/// The PSX data portal's request key: without it, every data endpoint answers 404.
///
/// <para>
/// <b>What changed, measured 2026-09-25.</b> The portal now renders
/// <c>window.__ps = {"_k":"…"}</c> into every page, and its own <c>script.js</c> sends that value as
/// <c>X-Req-Id</c> on every AJAX call. The data endpoints this client reads —
/// <c>/timeseries/*</c>, <c>/market-watch</c> and <c>POST /historical</c> — refuse a request without
/// it. With no headers they answer <b>404</b>. With <c>X-Requested-With</c> alone they answer
/// <b>403</b>. With both headers they answer <b>200</b>. A wrong key answers 404 again. No cookie is
/// involved. The HTML pages (<c>/company/X</c>, <c>/indices/X</c>) still serve without a key. This
/// looked like the portal being down: the home page loads, and a browser works because the page's
/// own script carries the key.
/// </para>
///
/// <para>
/// The key read twice a few seconds apart was the same value, but nothing says it is fixed. So it is
/// re-read after <see cref="RequestKeyTtl"/>, and on a 403 or 404 from a key older than
/// <see cref="RequestKeyMinAgeForRefresh"/>. The age floor stops a genuine 404 (an unknown symbol)
/// from costing a page fetch every time. Memory only, one value replaced whole, so there is nothing to
/// sweep.
/// </para>
/// </summary>
public sealed partial class PsxDataClient
{
    private static readonly TimeSpan RequestKeyTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestKeyMinAgeForRefresh = TimeSpan.FromSeconds(60);

    private static readonly Regex RequestKeyRegex = new(
        """window\.__ps\s*=\s*\{[^<]*?["']_k["']\s*:\s*["'](?<key>[^"']+)["']""",
        RegexOptions.Compiled);

    private readonly SemaphoreSlim _requestKeyGate = new(1, 1);
    private string? _requestKey;
    private DateTime _requestKeyAtUtc;

    /// <summary>The portal's request key from a page's HTML, or null when the page carries none.</summary>
    public static string? ParseRequestKey(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var match = RequestKeyRegex.Match(html);
        return match.Success ? match.Groups["key"].Value : null;
    }

    /// <summary>
    /// Sends one request to a keyed data endpoint, with the key and the AJAX marker the portal checks.
    /// A refusal that a stale key would explain gets a fresh key and ONE retry.
    /// </summary>
    /// <param name="build">Builds the request, called once per attempt: a request cannot be sent twice.</param>
    private async Task<HttpResponseMessage> SendKeyedAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var (key, keyAt) = await GetRequestKeyAsync(forceRefresh: false, ct);
        var response = await _http.SendAsync(WithKey(build(), key), ct);

        var refusedForKey = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden;
        if (!refusedForKey || DateTime.UtcNow - keyAt < RequestKeyMinAgeForRefresh)
            return response;

        response.Dispose();
        var (fresh, _) = await GetRequestKeyAsync(forceRefresh: true, ct);
        return await _http.SendAsync(WithKey(build(), fresh), ct);
    }

    private static HttpRequestMessage WithKey(HttpRequestMessage request, string? key)
    {
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        if (key is not null) request.Headers.TryAddWithoutValidation("X-Req-Id", key);
        return request;
    }

    private async Task<(string? Key, DateTime AtUtc)> GetRequestKeyAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && IsHeldKeyUsable(RequestKeyTtl))
            return (_requestKey, _requestKeyAtUtc);

        await _requestKeyGate.WaitAsync(ct);
        try
        {
            // A concurrent caller may have refreshed it while this one waited. A forced refresh
            // accepts that one too, as long as it is newer than the age floor.
            if (IsHeldKeyUsable(forceRefresh ? RequestKeyMinAgeForRefresh : RequestKeyTtl))
                return (_requestKey, _requestKeyAtUtc);

            var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
            string? key = null;
            try
            {
                key = ParseRequestKey(await _http.GetStringAsync($"{baseUrl}/", ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PsxData] Could not read the data portal's request key.");
            }

            if (key is null)
            {
                // Send without one rather than fail here: the endpoint's own refusal is the more
                // useful error, and a portal that drops the key again will simply work. The attempt is
                // still timestamped, so a page with no key costs one fetch a minute, not one per request.
                _logger.LogWarning("[PsxData] The data portal's home page carried no request key (window.__ps._k); " +
                                   "data requests will be sent without one and are likely to be refused.");
                _requestKey = null;
            }
            else
            {
                _requestKey = key;
            }

            _requestKeyAtUtc = DateTime.UtcNow;
            return (_requestKey, _requestKeyAtUtc);
        }
        finally
        {
            _requestKeyGate.Release();
        }
    }

    /// <summary>
    /// Whether the last read is recent enough to reuse. A read that found NO key is reused only for the
    /// age floor, so the page is checked again a minute later rather than an hour later.
    /// </summary>
    private bool IsHeldKeyUsable(TimeSpan maxAge) =>
        DateTime.UtcNow - _requestKeyAtUtc < (_requestKey is null ? RequestKeyMinAgeForRefresh : maxAge);
}
