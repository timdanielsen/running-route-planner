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
    private readonly ICrossingSafetyService _crossingSafety;
    private readonly ILogger<RoutesController> _logger;

    public RoutesController(
        IOpenRouteServiceClient orsClient,
        IAmenityLookupService amenityLookup,
        ICrossingSafetyService crossingSafety,
        ILogger<RoutesController> logger)
    {
        _orsClient = orsClient;
        _amenityLookup = amenityLookup;
        _crossingSafety = crossingSafety;
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

        // SelectStops only ever targets candidates up to roughly half the route distance out -
        // a stop farther than that could never be selected anyway. Searching the full requested
        // distance was wasteful and, worse, made the Overpass query noticeably heavier for
        // longer routes (more elements to fetch/parse over a much bigger area) with no benefit,
        // which in testing made a 16mi/2-restroom request fail more often than it should have
        // even though restrooms existed well within half that distance.
        var searchRadiusMiles = Math.Min((request.DistanceMiles / 2.0) + 1.0, 15.0);

        // Overpass lookups are the slow part of this request, so fetch raw candidates for both
        // types concurrently rather than back-to-back. Selection (which candidates to actually
        // use) happens afterward, synchronously, once both lists are in hand - see below for why.
        var restroomCandidatesTask = request.RestroomCount > 0
            ? _amenityLookup.FindNearbyAsync(request.Latitude, request.Longitude, searchRadiusMiles, AmenityType.Restroom, ct)
            : Task.FromResult<IReadOnlyList<AmenityStop>>([]);
        var fountainCandidatesTask = request.WaterFountainCount > 0
            ? _amenityLookup.FindNearbyAsync(request.Latitude, request.Longitude, searchRadiusMiles, AmenityType.WaterFountain, ct)
            : Task.FromResult<IReadOnlyList<AmenityStop>>([]);
        await Task.WhenAll(restroomCandidatesTask, fountainCandidatesTask);

        var restroomCandidates = restroomCandidatesTask.Result;
        var fountainCandidates = fountainCandidatesTask.Result;

        if (request.RestroomCount > 0 && restroomCandidates.Count == 0)
        {
            return BadRequest(
                $"No public restroom found within {searchRadiusMiles:0.#} miles of your start location. " +
                "Try a longer distance or a different starting point.");
        }

        if (request.WaterFountainCount > 0 && fountainCandidates.Count == 0)
        {
            return BadRequest(
                $"No water fountain found within {searchRadiusMiles:0.#} miles of your start location. " +
                "Try a longer distance or a different starting point.");
        }

        // Pick ONE shared direction across both amenity types before selecting any individual
        // stop. Without this, restrooms and water fountains each independently pick their own
        // "same direction as each other" bearing (see SelectStops below) but with no
        // coordination between the two - in testing, a 5mi request with 2 restrooms + 3
        // fountains came back as 17.9mi because the restrooms ended up ~230-240° from start
        // while the fountains ended up ~60-115°, nearly opposite directions, forcing the route
        // to zigzag across the start point repeatedly to hit all five. Anchoring on the single
        // candidate (from either type) closest to where the very first stop should be keeps
        // everything heading the same way.
        var allCandidates = restroomCandidates.Concat(fountainCandidates).ToList();
        double? sharedBearing = null;
        if (allCandidates.Count > 0)
        {
            var halfDistanceMeters = (request.DistanceMiles * GeoMath.MetersPerMile) / 2.0;
            var totalStopsRequested = request.RestroomCount + request.WaterFountainCount;
            var firstStopTargetMeters = halfDistanceMeters / (totalStopsRequested + 1);
            var anchor = allCandidates
                .OrderBy(c => Math.Abs(GeoMath.DistanceMeters(request.Latitude, request.Longitude, c.Latitude, c.Longitude) - firstStopTargetMeters))
                .First();
            sharedBearing = GeoMath.Bearing(request.Latitude, request.Longitude, anchor.Latitude, anchor.Longitude);
        }

        var restroomStops = SelectStops(restroomCandidates, request, request.RestroomCount, sharedBearing);
        var fountainStops = SelectStops(fountainCandidates, request, request.WaterFountainCount, sharedBearing);

        if (request.RestroomCount > 0 && restroomStops.Count < request.RestroomCount)
        {
            return BadRequest(
                $"Found only {restroomStops.Count} of {request.RestroomCount} requested public restroom(s) " +
                $"within {searchRadiusMiles:0.#} miles of your start location. Try a longer distance, a different " +
                "starting point, or fewer restrooms.");
        }

        if (request.WaterFountainCount > 0 && fountainStops.Count < request.WaterFountainCount)
        {
            return BadRequest(
                $"Found only {fountainStops.Count} of {request.WaterFountainCount} requested water fountain(s) " +
                $"within {searchRadiusMiles:0.#} miles of your start location. Try a longer distance, a different " +
                "starting point, or fewer fountains.");
        }

        // Visit required stops nearest-first, so a route with several restrooms/fountains doesn't
        // zigzag back and forth more than it has to.
        var requiredStops = restroomStops
            .Concat(fountainStops)
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

            // ORS's own routing can't tell a marked crossing from an unmarked one (its
            // maintainers confirm crossing type isn't factored in at all), so this can only
            // flag risky crossings on the route it already produced, not steer generation away
            // from them.
            result.CrossingWarnings = await _crossingSafety.FindUnmarkedCrossingsAsync(result.GeoJson, ct);

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
    private static List<AmenityStop> SelectStops(IReadOnlyList<AmenityStop> candidates, RouteRequest request, int count, double? sharedBearing)
    {
        if (candidates.Count == 0 || count == 0)
        {
            return [];
        }

        var halfDistanceMeters = (request.DistanceMiles * GeoMath.MetersPerMile) / 2.0;
        var used = new HashSet<AmenityStop>();
        var result = new List<AmenityStop>();

        for (var i = 0; i < count && used.Count < candidates.Count; i++)
        {
            var targetDistanceMeters = halfDistanceMeters * (i + 1) / (count + 1);
            IEnumerable<AmenityStop> remaining = candidates.Where(c => !used.Contains(c));

            // Prefer candidates roughly in the shared direction, so a multi-stop request reads
            // as "several stops along the way out" instead of zigzagging to opposite sides of
            // the start point to hit each one.
            if (sharedBearing is { } bearing)
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
