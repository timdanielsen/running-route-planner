using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

public interface IOpenRouteServiceClient
{
    Task<RouteResult> GenerateLoopAsync(double lat, double lon, double distanceMiles, int? seed, IReadOnlyList<AmenityStop> requiredStops, CancellationToken ct);

    Task<RouteResult> GenerateOutAndBackAsync(double lat, double lon, double distanceMiles, double? bearingDegrees, IReadOnlyList<AmenityStop> requiredStops, CancellationToken ct);

    /// <summary>
    /// Routes through an already-built ordered list of [lon, lat] waypoints, applying the same
    /// avoid_polygons/extra_info handling as the other Generate*Async methods. Used for the
    /// loop-first amenity placement strategy, where the caller has already interleaved a
    /// downsampled version of a previously-generated loop's own path with the amenity stops to
    /// visit, so the resulting route hugs the original loop instead of ORS finding its own
    /// possibly-quite-different shortest path between the stops.
    /// </summary>
    Task<RouteResult> GenerateAlongGuidedPathAsync(double lat, double lon, double distanceMiles, IReadOnlyList<double[]> coordinates, CancellationToken ct);
}
