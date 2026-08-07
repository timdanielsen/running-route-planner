namespace RunningRoutes.Api.Services;

public interface IGatedCommunityLookupService
{
    /// <summary>
    /// Finds gated-community boundaries and private-access streets within <paramref name="radiusMiles"/>
    /// of the given point. Returns closed polygon rings as [lon, lat] pairs (GeoJSON coordinate
    /// order), suitable for dropping into an ORS avoid_polygons MultiPolygon alongside
    /// IGraveyardLookupService's rings. Returns an empty list rather than throwing if OSM/Overpass
    /// is unreachable or returns nothing - this is a best-effort lookup, not something that should
    /// ever block route generation.
    /// </summary>
    Task<IReadOnlyList<double[][]>> FindNearbyGatedAreasAsync(double lat, double lon, double radiusMiles, CancellationToken ct);
}
