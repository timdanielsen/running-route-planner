using System.Text.Json;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Thin wrapper around the public Overpass API (OSM data queries), shared by everything that
/// needs to look up real-world features near a route: graveyards to avoid, restrooms/water
/// fountains to route through, etc.
///
/// Handles the two quirks that bit us in practice: Overpass rejects requests with no/generic
/// User-Agent (406 - configured on this HttpClient in Program.cs), and the free public instance
/// times out/5xx's often enough under normal load that a single retry meaningfully helps.
/// </summary>
public class OverpassClient : IOverpassClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OverpassClient> _logger;

    public OverpassClient(HttpClient httpClient, ILogger<OverpassClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress ??= new Uri("https://overpass-api.de/");
    }

    public async Task<JsonElement?> QueryAsync(string query, CancellationToken ct)
    {
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var response = await _httpClient.PostAsync(
                    "api/interpreter",
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query }),
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                }

                _logger.LogWarning(
                    "Overpass returned {Status} on attempt {Attempt}/2{Retrying}",
                    response.StatusCode, attempt, attempt == 1 ? "; retrying" : "; giving up for this request.");
            }

            return null;
        }
        // HttpClient's own Timeout expiring throws TaskCanceledException, which *is* an
        // OperationCanceledException - so checking the exception type alone can't tell that
        // apart from the caller's own ct being cancelled. Checking ct.IsCancellationRequested
        // can: only let it propagate if the caller actually asked for cancellation; anything
        // else (including our own timeout) is just a failed lookup, not a request abort.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Overpass query failed.");
            return null;
        }
    }
}
