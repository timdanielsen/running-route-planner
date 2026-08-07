using System.Text.Json;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Thin wrapper around the public Overpass API (OSM data queries), shared by everything that
/// needs to look up real-world features near a route: graveyards to avoid, restrooms/water
/// fountains to route through, etc.
///
/// Handles the quirks that bit us in practice: Overpass rejects requests with no/generic
/// User-Agent (406 - configured on this HttpClient in Program.cs), and the free public instance
/// is unreliable enough - timeouts, 5xx's, and even extended full outages we've hit repeatedly
/// while building this - that falling back across multiple independent mirrors matters more than
/// just retrying the same one.
/// </summary>
public class OverpassClient : IOverpassClient
{
    // Tried in order. overpass-api.de is the canonical/primary instance, so it goes first even
    // though it's been unreliable while building this - it's normally the best-provisioned
    // instance and this is presumed to be a transient bad patch. kumi.systems and maps.mail.ru
    // are well-known community-run global mirrors (see
    // https://wiki.openstreetmap.org/wiki/Overpass_API) used as fallbacks, which in practice
    // building this feature has been necessary often enough to matter. maps.mail.ru is ordered
    // before kumi.systems because, across every test run while diagnosing this (from two
    // independent networks), kumi.systems never once returned a response - it accepts the
    // connection then hangs for the full timeout - while maps.mail.ru consistently came through
    // with live, current data in a few seconds. No point spending 30s on an endpoint that's 0
    // for several straight attempts before trying the one that's actually been working.
    //
    // overpass.osm.ch was tried and deliberately excluded: it responds quickly but its dataset
    // is stale/empty (a broad Manhattan restaurant count query returned 0, and its
    // timestamp_osm_base wasn't even a valid date) - a confident wrong answer is worse than a
    // slow or failed one, since it'd get treated as "genuinely nothing here" instead of a
    // failure. Other public instances from the wiki are either region-locked (UK/Ireland,
    // Virginia, Ethiopia - not useful for a routing app that isn't scoped to one region) or
    // require their own API keys (Geofabrik, FairwayMapper).
    private static readonly string[] EndpointUrls =
    [
        "https://overpass-api.de/api/interpreter",
        "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<OverpassClient> _logger;

    public OverpassClient(HttpClient httpClient, ILogger<OverpassClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<JsonElement?> QueryAsync(string query, CancellationToken ct)
    {
        for (var i = 0; i < EndpointUrls.Length; i++)
        {
            var url = EndpointUrls[i];
            var isLastEndpoint = i == EndpointUrls.Length - 1;
            var fallingBack = isLastEndpoint ? "; no more fallbacks, giving up." : "; falling back to next endpoint.";

            try
            {
                // GET with the query as a URL parameter, not POST with a form body. Overpass
                // supports both, but POST specifically got blocked somewhere on at least one
                // real network this app was tested from (worked fine in Postman as GET, failed
                // in-app as POST against the same host) - likely a firewall/security product
                // that's stricter about POST bodies to external hosts than plain GETs. Our
                // queries are short bbox+tag filters, well within normal URL length limits.
                using var response = await _httpClient.GetAsync(
                    $"{url}?data={Uri.EscapeDataString(query)}",
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                }

                _logger.LogWarning("Overpass endpoint {Url} returned {Status}{FallingBack}", url, response.StatusCode, fallingBack);
            }
            // A connection-level failure (DNS hiccup, timeout, refused connection, etc.) throws
            // before we ever get a response. HttpClient's own Timeout expiring throws
            // TaskCanceledException, which *is* an OperationCanceledException, so checking the
            // exception type alone can't tell that apart from the caller's own ct being
            // cancelled - checking ct.IsCancellationRequested can: only let it propagate if the
            // caller actually asked for cancellation.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Overpass endpoint {Url} failed{FallingBack}", url, fallingBack);
            }
        }

        return null;
    }
}
