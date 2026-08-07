using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Looks up restrooms (OSM amenity=toilets) and water fountains (amenity=drinking_water) from
/// OpenStreetMap via Overpass. These are almost universally mapped as single nodes, unlike
/// graveyards, so there's no polygon/multipolygon assembly needed here - just points.
///
/// Results are cached per (type, location, radius) for a week, since this data changes rarely
/// and the free Overpass instance is slow/flaky enough that avoiding repeat calls matters.
/// </summary>
public class AmenityLookupService : IAmenityLookupService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(7);

    // Overpass returning nothing is indistinguishable from Overpass being unreachable (see
    // IAmenityLookupService's docs) - caching that for a full week would mean one transient
    // outage makes a location look permanently amenity-free until the cache expires. Cache empty
    // results only briefly: long enough to avoid hammering Overpass on rapid repeat requests
    // (e.g. a user immediately retrying after a failure), short enough that once Overpass
    // recovers, the next real request tries again instead of trusting a stale failure.
    private static readonly TimeSpan EmptyResultCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IOverpassClient _overpass;
    private readonly IMemoryCache _cache;

    public AmenityLookupService(IOverpassClient overpass, IMemoryCache cache)
    {
        _overpass = overpass;
        _cache = cache;
    }

    public async Task<IReadOnlyList<AmenityStop>> FindNearbyAsync(double lat, double lon, double radiusMiles, AmenityType type, CancellationToken ct)
    {
        // Rounded to ~100m so two requests near the same spot (re-rolling a route, another user
        // starting from the same landmark) share a cache entry instead of each hitting Overpass.
        var cacheKey = $"amenities:{type}:{Math.Round(lat, 3).ToString(CultureInfo.InvariantCulture)}:{Math.Round(lon, 3).ToString(CultureInfo.InvariantCulture)}:{radiusMiles:F1}";

        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<AmenityStop>? cached) && cached is not null)
        {
            return cached;
        }

        var result = await FindNearbyUncachedAsync(lat, lon, radiusMiles, type, ct);
        _cache.Set(cacheKey, result, result.Count > 0 ? CacheDuration : EmptyResultCacheDuration);
        return result;
    }

    private async Task<IReadOnlyList<AmenityStop>> FindNearbyUncachedAsync(double lat, double lon, double radiusMiles, AmenityType type, CancellationToken ct)
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
