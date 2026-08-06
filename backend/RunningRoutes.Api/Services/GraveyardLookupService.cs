using System.Globalization;
using System.Text.Json;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Looks up graveyard/cemetery boundaries from OpenStreetMap via the public Overpass API, so
/// routes can be steered around them with ORS's avoid_polygons option. There's no "avoid
/// graveyards" flag on ORS itself - this is what supplies the actual geometry to avoid.
///
/// Best-effort by design: Overpass is a free, rate-limited public service, and individual OSM
/// ways occasionally have self-intersecting or otherwise malformed geometry. Any failure here
/// (network, timeout, bad geometry) just means route generation proceeds without graveyard
/// avoidance - it should never take down route generation itself.
/// </summary>
public class GraveyardLookupService : IGraveyardLookupService
{
    private const double MilesPerDegreeLatitude = 69.0;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GraveyardLookupService> _logger;

    public GraveyardLookupService(HttpClient httpClient, ILogger<GraveyardLookupService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _httpClient.BaseAddress ??= new Uri("https://overpass-api.de/");
    }

    public async Task<IReadOnlyList<double[][]>> FindNearbyGraveyardsAsync(double lat, double lon, double radiusMiles, CancellationToken ct)
    {
        try
        {
            var (south, west, north, east) = BoundingBox(lat, lon, radiusMiles);
            var query = $$"""
                [out:json][timeout:15];
                (
                  way["landuse"="cemetery"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
                  way["amenity"="grave_yard"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
                  relation["landuse"="cemetery"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
                  relation["amenity"="grave_yard"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
                );
                out geom;
                """;

            // The free public Overpass instance times out/5xx's fairly often under normal load;
            // one retry noticeably improves the real-world hit rate without adding much latency.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var response = await _httpClient.PostAsync(
                    "api/interpreter",
                    new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query }),
                    ct);

                if (response.IsSuccessStatusCode)
                {
                    var doc = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    return ParseRings(doc);
                }

                _logger.LogWarning(
                    "Overpass returned {Status} on attempt {Attempt}/2{Retrying}",
                    response.StatusCode, attempt, attempt == 1 ? "; retrying" : "; skipping graveyard avoidance for this request.");
            }

            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Graveyard lookup failed; skipping graveyard avoidance for this request.");
            return [];
        }
    }

    private static List<double[][]> ParseRings(JsonElement doc)
    {
        var rings = new List<double[][]>();
        if (!doc.TryGetProperty("elements", out var elements))
        {
            return rings;
        }

        foreach (var element in elements.EnumerateArray())
        {
            var type = element.GetProperty("type").GetString();

            if (type == "way" && element.TryGetProperty("geometry", out var wayGeom))
            {
                AddRingIfValid(rings, wayGeom);
            }
            else if (type == "relation" && element.TryGetProperty("members", out var members))
            {
                foreach (var member in members.EnumerateArray())
                {
                    var role = member.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;
                    if (role == "outer" && member.TryGetProperty("geometry", out var memberGeom))
                    {
                        AddRingIfValid(rings, memberGeom);
                    }
                }
            }
        }

        return rings;
    }

    private static void AddRingIfValid(List<double[][]> rings, JsonElement geometry)
    {
        var points = new List<double[]>();
        foreach (var node in geometry.EnumerateArray())
        {
            var lat = node.GetProperty("lat").GetDouble();
            var lon = node.GetProperty("lon").GetDouble();
            points.Add([lon, lat]);
        }

        // A GeoJSON polygon ring needs at least 4 positions (3 distinct + closing point) and
        // must be closed; Overpass "outer" member ways aren't always closed on their own since
        // they're sometimes assembled from multiple segments in the full relation.
        if (points.Count < 3)
        {
            return;
        }

        if (points[0][0] != points[^1][0] || points[0][1] != points[^1][1])
        {
            points.Add(points[0]);
        }

        if (points.Count >= 4)
        {
            rings.Add(points.ToArray());
        }
    }

    private static (double South, double West, double North, double East) BoundingBox(double lat, double lon, double radiusMiles)
    {
        var latDelta = radiusMiles / MilesPerDegreeLatitude;
        var milesPerDegreeLongitude = MilesPerDegreeLatitude * Math.Cos(lat * Math.PI / 180.0);
        var lonDelta = radiusMiles / Math.Max(milesPerDegreeLongitude, 1.0);

        return (lat - latDelta, lon - lonDelta, lat + latDelta, lon + lonDelta);
    }

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
