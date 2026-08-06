using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

public interface IOpenRouteServiceClient
{
    Task<RouteResult> GenerateLoopAsync(double lat, double lon, double distanceKm, int? seed, CancellationToken ct);

    Task<RouteResult> GenerateOutAndBackAsync(double lat, double lon, double distanceKm, double? bearingDegrees, CancellationToken ct);
}
