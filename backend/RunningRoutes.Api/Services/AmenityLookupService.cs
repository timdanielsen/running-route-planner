using System.Globalization;
using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Looks up restrooms (OSM amenity=toilets) and water fountains (amenity=drinking_water) from
/// OpenStreetMap via Overpass. These are almost universally mapped as single nodes, unlike
/// graveyards, so there's no polygon/multipolygon assembly needed here - just points.
/// </summary>
public class AmenityLookupService : IAmenityLookupService
{
    private readonly IOverpassClient _overpass;

    public AmenityLookupService(IOverpassClient overpass)
    {
        _overpass = overpass;
    }

    public async Task<IReadOnlyList<AmenityStop>> FindNearbyAsync(double lat, double lon, double radiusMiles, AmenityType type, CancellationToken ct)
    {
        var osmValue = type == AmenityType.Restroom ? "toilets" : "drinking_water";
        var (south, west, north, east) = GeoMath.BoundingBox(lat, lon, radiusMiles);
        var query = $$"""
            [out:json][timeout:15];
            node["amenity"="{{osmValue}}"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
            out;
            """;

        var doc = await _overpass.QueryAsync(query, ct);
        if (doc is null || !doc.Value.TryGetProperty("elements", out var elements))
        {
            return [];
        }

        var stops = new List<AmenityStop>();
        foreach (var element in elements.EnumerateArray())
        {
            var elLat = element.GetProperty("lat").GetDouble();
            var elLon = element.GetProperty("lon").GetDouble();
            string? name = null;
            if (element.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var nameProp))
            {
                name = nameProp.GetString();
            }

            stops.Add(new AmenityStop { Type = type, Latitude = elLat, Longitude = elLon, Name = name });
        }

        return stops;
    }

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
