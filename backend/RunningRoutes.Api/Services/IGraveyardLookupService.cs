namespace RunningRoutes.Api.Services;

public interface IGraveyardLookupService
{
    /// <summary>
    /// Finds graveyard/cemetery boundaries within <paramref name="radiusMiles"/> of the given point.
    /// Returns closed polygon rings as [lon, lat] pairs (GeoJSON coordinate order), suitable for
    /// dropping straight into an ORS avoid_polygons MultiPolygon. Returns an empty list rather than
    /// throwing if OSM/Overpass is unreachable or returns nothing - this is a best-effort lookup,
    /// not something that should ever block route generation.
    /// </summary>
    Task<IReadOnlyList<double[][]>> FindNearbyGraveyardsAsync(double lat, double lon, double radiusMiles, CancellationToken ct);
}
