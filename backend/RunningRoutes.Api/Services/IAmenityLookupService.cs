using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

public interface IAmenityLookupService
{
    /// <summary>
    /// Finds restrooms or water fountains (per <paramref name="type"/>) within
    /// <paramref name="radiusMiles"/> of the given point, via OpenStreetMap data. Returns an
    /// empty list rather than throwing if Overpass is unreachable - callers can't distinguish
    /// "none nearby" from "couldn't check," which is the right tradeoff here since this feeds a
    /// user-facing "not found" error either way.
    /// </summary>
    Task<IReadOnlyList<AmenityStop>> FindNearbyAsync(double lat, double lon, double radiusMiles, AmenityType type, CancellationToken ct);
}
