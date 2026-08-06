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
    private const double MetersPerMile = 1609.344;
    private readonly HttpClient _httpClient;
    private readonly IGraveyardLookupService _graveyardLookup;
    private readonly ILogger<OpenRouteServiceClient> _logger;
    private readonly Random _random = new();

    public OpenRouteServiceClient(
        HttpClient httpClient,
        IGraveyardLookupService graveyardLookup,
        IConfiguration config,
        ILogger<OpenRouteServiceClient> logger)
    {
        _httpClient = httpClient;
        _graveyardLookup = graveyardLookup;
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

    public async Task<RouteResult> GenerateLoopAsync(double lat, double lon, double distanceMiles, int? seed, CancellationToken ct)
    {
        var options = new Dictionary<string, object>
        {
            ["round_trip"] = new
            {
                length = distanceMiles * MetersPerMile,
                // Fewer waypoints means fewer forced legs to route between, which in testing
                // against the live ORS API cut turn counts by ~40-55% versus the previous
                // value of 5, while 3 is still enough to read as a loop rather than
                // collapsing into an out-and-back shape.
                points = 3,
                seed = seed ?? _random.Next()
            },
            ["profile_params"] = QuietWeighting
        };
        await AddAvoidPolygonsAsync(options, lat, lon, distanceMiles, ct);

        var body = new
        {
            coordinates = new[] { new[] { lon, lat } },
            options
        };

        return await PostDirectionsAsync(body, options, ct);
    }

    public async Task<RouteResult> GenerateOutAndBackAsync(double lat, double lon, double distanceMiles, double? bearingDegrees, CancellationToken ct)
    {
        var bearing = bearingDegrees ?? _random.NextDouble() * 360.0;
        var halfDistanceMeters = (distanceMiles * MetersPerMile) / 2.0;
        var turnaround = GeoMath.Destination(lat, lon, bearing, halfDistanceMeters);

        var options = new Dictionary<string, object> { ["profile_params"] = QuietWeighting };
        await AddAvoidPolygonsAsync(options, lat, lon, distanceMiles, ct);

        // Route start -> turnaround -> start. ORS treats this as a single multi-waypoint
        // route rather than the round_trip option, since we're choosing the destination ourselves.
        var body = new
        {
            coordinates = new[]
            {
                new[] { lon, lat },
                new[] { turnaround.Lon, turnaround.Lat },
                new[] { lon, lat }
            },
            options
        };

        return await PostDirectionsAsync(body, options, ct);
    }

    // Looks up nearby graveyards and, if any are found, adds them to the request as an
    // avoid_polygons MultiPolygon. The search radius matches the requested distance since a loop
    // or out-and-back could extend that far from the start point; capped so a very long distance
    // request can't blow up the Overpass query.
    private async Task AddAvoidPolygonsAsync(Dictionary<string, object> options, double lat, double lon, double distanceMiles, CancellationToken ct)
    {
        var radiusMiles = Math.Min(distanceMiles, 15.0);
        var rings = await _graveyardLookup.FindNearbyGraveyardsAsync(lat, lon, radiusMiles, ct);
        if (rings.Count > 0)
        {
            options["avoid_polygons"] = new
            {
                type = "MultiPolygon",
                coordinates = rings.Select(ring => new[] { ring }).ToArray()
            };
        }
    }

    // Biases the foot-walking route away from busy/loud roads (highway classification, speed
    // limits, etc.) instead of just taking the shortest path. Factor 1.0 = strongest preference
    // for quiet ways. This is ORS's documented lever for "avoid major roads" on foot profiles;
    // there's no separate "avoid highways" option for pedestrians since ORS foot routing doesn't
    // treat major roads as impassable, just undesirable.
    private static readonly object QuietWeighting = new
    {
        weightings = new
        {
            quiet = 1.0
        }
    };

    private async Task<RouteResult> PostDirectionsAsync(object requestBody, Dictionary<string, object> options, CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync($"v2/directions/{Profile}/geojson", requestBody, ct);

        // A malformed avoid_polygons geometry (e.g. a self-intersecting OSM way - this happens
        // in real OSM data) makes ORS reject the *entire* request, not just that polygon. Since
        // graveyard avoidance is a nice-to-have, fall back to a normal route rather than failing
        // the whole request over one bad shape.
        if (!response.IsSuccessStatusCode && options.ContainsKey("avoid_polygons"))
        {
            var firstErrorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "ORS rejected the request with avoid_polygons set ({Status}: {Body}); retrying without graveyard avoidance.",
                response.StatusCode, firstErrorBody);

            response.Dispose();
            options.Remove("avoid_polygons");
            response = await _httpClient.PostAsJsonAsync($"v2/directions/{Profile}/geojson", requestBody, ct);
        }

        using var _ = response;

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
