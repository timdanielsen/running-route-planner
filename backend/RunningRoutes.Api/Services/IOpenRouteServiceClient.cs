using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

public interface IOpenRouteServiceClient
{
    Task<RouteResult> GenerateLoopAsync(double lat, double lon, double distanceMiles, int? seed, IReadOnlyList<AmenityStop> requiredStops, CancellationToken ct);

    Task<RouteResult> GenerateOutAndBackAsync(double lat, double lon, double distanceMiles, double? bearingDegrees, IReadOnlyList<AmenityStop> requiredStops, CancellationToken ct);
}
