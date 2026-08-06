using System.Globalization;
using System.Text.Json;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Looks up graveyard/cemetery boundaries from OpenStreetMap via Overpass, so routes can be
/// steered around them with ORS's avoid_polygons option. There's no "avoid graveyards" flag on
/// ORS itself - this is what supplies the actual geometry to avoid.
///
/// Best-effort by design: individual OSM ways occasionally have self-intersecting or otherwise
/// malformed geometry, and Overpass itself can be unreachable. Any failure here just means route
/// generation proceeds without graveyard avoidance - it should never take down route generation.
/// </summary>
public class GraveyardLookupService : IGraveyardLookupService
{
    private readonly IOverpassClient _overpass;

    public GraveyardLookupService(IOverpassClient overpass)
    {
        _overpass = overpass;
    }

    public async Task<IReadOnlyList<double[][]>> FindNearbyGraveyardsAsync(double lat, double lon, double radiusMiles, CancellationToken ct)
    {
        var (south, west, north, east) = GeoMath.BoundingBox(lat, lon, radiusMiles);
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

        var doc = await _overpass.QueryAsync(query, ct);
        return doc is null ? [] : ParseRings(doc.Value);
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

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
