using System.Text.Json;
using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Talks to OpenRouteService's foot-walking directions endpoint to build:
///   - Loop routes, using ORS's built-in `options.round_trip` (it does the hard work of
///     finding a real, walkable circuit of roughly the requested length).
///   - Out-and-back routes, by picking a point ~half the target distance away along a
///     bearing and asking ORS for a route through it and back to the start.
///
/// Docs: https://openrouteservice.org/dev/#/api-docs/v2/directions/{profile}/geojson/post
/// </summary>
public class OpenRouteServiceClient : IOpenRouteServiceClient
{
    private const string Profile = "foot-walking";
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenRouteServiceClient> _logger;
    private readonly Random _random = new();

    public OpenRouteServiceClient(HttpClient httpClient, IConfiguration config, ILogger<OpenRouteServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = config["OpenRouteService:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "OpenRouteService:ApiKey is not set. Requests to ORS will fail with 401/403 until " +
                "you set it via appsettings, user-secrets, or the ORS_API_KEY environment variable.");
        }
        else
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", apiKey);
        }

        _httpClient.BaseAddress ??= new Uri("https://api.openrouteservice.org/");
    }

    public async Task<RouteResult> GenerateLoopAsync(double lat, double lon, double distanceKm, int? seed, CancellationToken ct)
    {
        var body = new
        {
            coordinates = new[] { new[] { lon, lat } },
            options = new
            {
                round_trip = new
                {
                    length = distanceKm * 1000,
                    points = 5,
                    seed = seed ?? _random.Next()
                }
            }
        };

        return await PostDirectionsAsync(body, ct);
    }

    public async Task<RouteResult> GenerateOutAndBackAsync(double lat, double lon, double distanceKm, double? bearingDegrees, CancellationToken ct)
    {
        var bearing = bearingDegrees ?? _random.NextDouble() * 360.0;
        var halfDistanceMeters = (distanceKm * 1000) / 2.0;
        var turnaround = GeoMath.Destination(lat, lon, bearing, halfDistanceMeters);

        // Route start -> turnaround -> start. ORS treats this as a single multi-waypoint
        // route rather than the round_trip option, since we're choosing the destination ourselves.
        var body = new
        {
            coordinates = new[]
            {
                new[] { lon, lat },
                new[] { turnaround.Lon, turnaround.Lat },
                new[] { lon, lat }
            }
        };

        return await PostDirectionsAsync(body, ct);
    }

    private async Task<RouteResult> PostDirectionsAsync(object requestBody, CancellationToken ct)
    {
        using var response = await _httpClient.PostAsJsonAsync($"v2/directions/{Profile}/geojson", requestBody, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("ORS request failed ({Status}): {Body}", response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenRouteService returned {(int)response.StatusCode}: {errorBody}");
        }

        var geoJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        // ORS puts aggregate distance/duration under features[0].properties.summary
        var summary = geoJson
            .GetProperty("features")[0]
            .GetProperty("properties")
            .GetProperty("summary");

        return new RouteResult
        {
            GeoJson = geoJson,
            DistanceMeters = summary.GetProperty("distance").GetDouble(),
            DurationSeconds = summary.GetProperty("duration").GetDouble()
        };
    }
}
