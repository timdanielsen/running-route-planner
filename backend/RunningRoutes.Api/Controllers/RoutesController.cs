using Microsoft.AspNetCore.Mvc;
using RunningRoutes.Api.Models;
using RunningRoutes.Api.Services;

namespace RunningRoutes.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoutesController : ControllerBase
{
    private const int MaxAmenityCount = 5;

    private readonly IOpenRouteServiceClient _orsClient;
    private readonly IAmenityLookupService _amenityLookup;
    private readonly ILogger<RoutesController> _logger;

    public RoutesController(IOpenRouteServiceClient orsClient, IAmenityLookupService amenityLookup, ILogger<RoutesController> logger)
    {
        _orsClient = orsClient;
        _amenityLookup = amenityLookup;
        _logger = logger;
    }

    /// <summary>
    /// Generates a running route starting at the given point.
    /// POST /api/routes
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RouteResult>> Generate([FromBody] RouteRequest request, CancellationToken ct)
    {
        if (request.DistanceMiles <= 0)
        {
            return BadRequest("distanceMiles must be greater than 0.");
        }

        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
        {
            return BadRequest("latitude/longitude are out of range.");
        }

        if (request.RestroomCount is < 0 or > MaxAmenityCount)
        {
            return BadRequest($"restroomCount must be between 0 and {MaxAmenityCount}.");
        }

        if (request.WaterFountainCount is < 0 or > MaxAmenityCount)
        {
            return BadRequest($"waterFountainCount must be between 0 and {MaxAmenityCount}.");
        }

        var searchRadiusMiles = Math.Min(request.DistanceMiles, 15.0);

        // Overpass lookups are the slow part of this request (occasionally 10s+ each on the free
        // public instance), so run the restroom and water fountain searches concurrently rather
        // than back-to-back when both are requested.
        var restroomTask = request.RestroomCount > 0
            ? FindBestStopsAsync(AmenityType.Restroom, request, searchRadiusMiles, request.RestroomCount, ct)
            : Task.FromResult(new List<AmenityStop>());
        var fountainTask = request.WaterFountainCount > 0
            ? FindBestStopsAsync(AmenityType.WaterFountain, request, searchRadiusMiles, request.WaterFountainCount, ct)
            : Task.FromResult(new List<AmenityStop>());
        await Task.WhenAll(restroomTask, fountainTask);

        if (request.RestroomCount > 0 && restroomTask.Result.Count < request.RestroomCount)
        {
            return BadRequest(
                $"Found only {restroomTask.Result.Count} of {request.RestroomCount} requested public restroom(s) " +
                $"within {searchRadiusMiles:0.#} miles of your start location. Try a longer distance, a different " +
                "starting point, or fewer restrooms.");
        }

        if (request.WaterFountainCount > 0 && fountainTask.Result.Count < request.WaterFountainCount)
        {
            return BadRequest(
                $"Found only {fountainTask.Result.Count} of {request.WaterFountainCount} requested water fountain(s) " +
                $"within {searchRadiusMiles:0.#} miles of your start location. Try a longer distance, a different " +
                "starting point, or fewer fountains.");
        }

        // Visit required stops nearest-first, so a route with several restrooms/fountains doesn't
        // zigzag back and forth more than it has to.
        var requiredStops = restroomTask.Result
            .Concat(fountainTask.Result)
            .OrderBy(stop => GeoMath.DistanceMeters(request.Latitude, request.Longitude, stop.Latitude, stop.Longitude))
            .ToList();

        try
        {
            var result = request.Type switch
            {
                RouteType.Loop => await _orsClient.GenerateLoopAsync(
                    request.Latitude, request.Longitude, request.DistanceMiles, request.Seed, requiredStops, ct),
                RouteType.OutAndBack => await _orsClient.GenerateOutAndBackAsync(
                    request.Latitude, request.Longitude, request.DistanceMiles, request.BearingDegrees, requiredStops, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Type), request.Type, "Unknown route type.")
            };

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Routing provider error");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    // Picks up to `count` distinct candidates, spread across the "out" leg of the route instead
    // of clustered around a single point: the first is targeted at roughly 1/(count+1) of the
    // halfway distance, the second at 2/(count+1), and so on, so successive stops read as being
    // encountered one after another as the run progresses rather than all at once. Landing the
    // last one exactly at the halfway/turnaround point would make it the literal end of the route
    // (OpenRouteServiceClient then has to route further out and back for it to read as "on the
    // way" rather than "the destination") - targeting short of that leaves room for that.
    private async Task<List<AmenityStop>> FindBestStopsAsync(AmenityType type, RouteRequest request, double radiusMiles, int count, CancellationToken ct)
    {
        var candidates = await _amenityLookup.FindNearbyAsync(request.Latitude, request.Longitude, radiusMiles, type, ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        var halfDistanceMeters = (request.DistanceMiles * GeoMath.MetersPerMile) / 2.0;
        var used = new HashSet<AmenityStop>();
        var result = new List<AmenityStop>();
        double? anchorBearing = null;

        for (var i = 0; i < count && used.Count < candidates.Count; i++)
        {
            var targetDistanceMeters = halfDistanceMeters * (i + 1) / (count + 1);
            IEnumerable<AmenityStop> remaining = candidates.Where(c => !used.Contains(c));

            // After the first stop, prefer candidates roughly in the same direction as it, so a
            // multi-stop request reads as "several stops along the way out" instead of zigzagging
            // to opposite sides of the start point to hit each one - which is exactly what
            // distance-only selection did in testing (a 4mi request came back as 19+mi).
            if (anchorBearing is { } bearing)
            {
                var sameDirection = remaining
                    .Where(c => AngleDifference(bearing, GeoMath.Bearing(request.Latitude, request.Longitude, c.Latitude, c.Longitude)) <= 60.0)
                    .ToList();
                if (sameDirection.Count > 0)
                {
                    remaining = sameDirection;
                }
            }

            var next = remaining
                .OrderBy(c => Math.Abs(GeoMath.DistanceMeters(request.Latitude, request.Longitude, c.Latitude, c.Longitude) - targetDistanceMeters))
                .First();

            anchorBearing ??= GeoMath.Bearing(request.Latitude, request.Longitude, next.Latitude, next.Longitude);
            used.Add(next);
            result.Add(next);
        }

        return result;
    }

    private static double AngleDifference(double bearingA, double bearingB)
    {
        var diff = Math.Abs(bearingA - bearingB) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }
}
