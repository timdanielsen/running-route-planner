using System.Text.Json;
using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

public interface ICrossingSafetyService
{
    /// <summary>
    /// Finds points where the given route (ORS GeoJSON FeatureCollection) crosses a busy road
    /// with no marked pedestrian crossing nearby. Best-effort like the other Overpass-backed
    /// lookups in this app - returns an empty list rather than throwing if the data can't be
    /// fetched, since this is a safety heads-up, not something that should block a route from
    /// being returned.
    /// </summary>
    Task<List<CrossingWarning>> FindUnmarkedCrossingsAsync(JsonElement routeGeoJson, CancellationToken ct);
}
