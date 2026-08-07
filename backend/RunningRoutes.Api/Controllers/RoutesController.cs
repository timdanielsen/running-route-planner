using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using RunningRoutes.Api.Models;
using RunningRoutes.Api.Services;

namespace RunningRoutes.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoutesController : ControllerBase
{
    private const int MaxAmenityCount = 5;

    // How far off a loop's actual path a restroom/fountain can be and still count as "on the
    // way" for the loop-first placement strategy - about a 4-5 minute walking detour. Widened
    // (doubled) only as far as needed to find `count` candidates, up to this cap - see
    // SelectStopsAlongLoop. Never uncapped: an early version fell back to "any distance goes"
    // when the tight threshold came up short, and arc-length-target matching with no distance
    // limit picked a candidate that was a "perfect" spacing match but kilometers off the actual
    // path over a much closer, only slightly-worse-spaced one - a 5mi request came back as
    // 19.6mi. Capping the widening means we honestly report "not enough found" instead.
    private const double MaxLoopDetourMeters = 400.0;
    private const double MaxLoopDetourWidenLimitMeters = 3200.0;

    // ORS caps directions requests at 70 total waypoints (confirmed via direct testing - a
    // ~260-point request came back with error code 2004). BuildGuidedLoopCoordinates splices the
    // original loop's own path back in as guide waypoints, so that path has to be downsampled to
    // well under the limit to leave room for the actual amenity stops (up to MaxAmenityCount * 2).
    private const int MaxLoopAnchorPoints = 50;

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

        var wantsAmenities = request.RestroomCount > 0 || request.WaterFountainCount > 0;

        // SelectStops/SelectStopsAlongLoop only ever target candidates up to roughly half the
        // route distance out (a loop can't get farther than that from start and back within the
        // requested length) - a stop farther than that could never be selected anyway. Searching
        // the full requested distance was wasteful and, worse, made the Overpass query noticeably
        // heavier for longer routes (more elements to fetch/parse over a much bigger area) with
        // no benefit, which in testing made a 16mi/2-restroom request fail more often than it
        // should have even though restrooms existed well within half that distance.
        var searchRadiusMiles = Math.Min((request.DistanceMiles / 2.0) + 1.0, 15.0);

        try
        {
            RouteResult result;

            if (request.Type == RouteType.Loop && wantsAmenities)
            {
                // Loop-first placement: generate a real, ORS-validated loop shape first, then
                // only consider amenities already close to *that* path (minimal detour by
                // construction) instead of picking candidates by compass bearing from the start
                // with no guarantee they form a sensible loop at all. Spacing them by position
                // *along* the already-known loop, rather than by bearing from start, also makes
                // the cross-type "restrooms end up opposite the fountains" bug structurally
                // impossible - arc-length along one path is one-directional, there's no "wrong
                // direction" to end up in.
                var initialLoop = await _orsClient.GenerateLoopAsync(
                    request.Latitude, request.Longitude, request.DistanceMiles, request.Seed, [], ct);
                var loopPoints = ExtractRoutePoints(initialLoop.GeoJson);
                var totalArcLengthMeters = TotalArcLength(loopPoints);

                var (restroomCandidates, fountainCandidates, candidateError) =
                    await FetchAmenityCandidatesAsync(request, searchRadiusMiles, ct);
                if (candidateError is not null)
                {
                    return BadRequest(candidateError);
                }

                var restroomStops = SelectStopsAlongLoop(restroomCandidates, loopPoints, totalArcLengthMeters, request.RestroomCount);
                var fountainStops = SelectStopsAlongLoop(fountainCandidates, loopPoints, totalArcLengthMeters, request.WaterFountainCount);

                var countError = CheckStopCounts(request, restroomStops.Count, fountainStops.Count, searchRadiusMiles);
                if (countError is not null)
                {
                    return BadRequest(countError);
                }

                // Visit stops in the order they're actually encountered walking the loop, not
                // nearest-to-start-first - two stops could both be close to start but on
                // opposite sides of the loop.
                var requiredStops = restroomStops
                    .Concat(fountainStops)
                    .OrderBy(stop => ProjectOntoPolyline(stop.Latitude, stop.Longitude, loopPoints).ArcLengthMeters)
                    .ToList();

                // Just handing ORS [start, stop1, stop2, ..., start] and letting it freely
                // shortest-path between them doesn't reliably retrace the loop we picked these
                // stops for being close to - ORS's own shortest path between two points near the
                // loop can diverge from the loop itself, especially since round_trip-generated
                // loops aren't always simple, well-behaved circuits. Tested this: a 3mi loop
                // came back as 6.24mi even with stops individually close to the path. Splicing
                // in a downsampled version of the loop's own points as additional waypoints
                // forces the route to hug the original shape except for the actual detours.
                var guidedCoordinates = BuildGuidedLoopCoordinates(loopPoints, requiredStops);

                result = await _orsClient.GenerateAlongGuidedPathAsync(
                    request.Latitude, request.Longitude, request.DistanceMiles, guidedCoordinates, ct);
                result.AmenityStops = requiredStops;
            }
            else if (wantsAmenities)
            {
                // Out & back has no pre-existing "loop path" to anchor stop placement to (it's
                // a straight-out-and-back shape defined by a bearing, not a generated route) -
                // keeps the original bearing-from-start placement strategy.
                var (restroomCandidates, fountainCandidates, candidateError) =
                    await FetchAmenityCandidatesAsync(request, searchRadiusMiles, ct);
                if (candidateError is not null)
                {
                    return BadRequest(candidateError);
                }

                // Pick ONE shared direction across both amenity types before selecting any
                // individual stop. Without this, restrooms and water fountains each
                // independently pick their own "same direction as each other" bearing (see
                // SelectStops below) but with no coordination between the two - in testing, a
                // 5mi request with 2 restrooms + 3 fountains came back as 17.9mi because the
                // restrooms ended up ~230-240° from start while the fountains ended up
                // ~60-115°, nearly opposite directions, forcing the route to zigzag across the
                // start point repeatedly to hit all five. Anchoring on the single candidate
                // (from either type) closest to where the very first stop should be keeps
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

                var countError = CheckStopCounts(request, restroomStops.Count, fountainStops.Count, searchRadiusMiles);
                if (countError is not null)
                {
                    return BadRequest(countError);
                }

                // Visit required stops nearest-first, so a route with several restrooms/fountains
                // doesn't zigzag back and forth more than it has to.
                var requiredStops = restroomStops
                    .Concat(fountainStops)
                    .OrderBy(stop => GeoMath.DistanceMeters(request.Latitude, request.Longitude, stop.Latitude, stop.Longitude))
                    .ToList();

                result = await _orsClient.GenerateOutAndBackAsync(
                    request.Latitude, request.Longitude, request.DistanceMiles, request.BearingDegrees, requiredStops, ct);
            }
            else
            {
                result = request.Type switch
                {
                    RouteType.Loop => await _orsClient.GenerateLoopAsync(
                        request.Latitude, request.Longitude, request.DistanceMiles, request.Seed, [], ct),
                    RouteType.OutAndBack => await _orsClient.GenerateOutAndBackAsync(
                        request.Latitude, request.Longitude, request.DistanceMiles, request.BearingDegrees, [], ct),
                    _ => throw new ArgumentOutOfRangeException(nameof(request.Type), request.Type, "Unknown route type.")
                };
            }

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

    private async Task<(IReadOnlyList<AmenityStop> Restrooms, IReadOnlyList<AmenityStop> Fountains, string? ErrorMessage)> FetchAmenityCandidatesAsync(
        RouteRequest request, double searchRadiusMiles, CancellationToken ct)
    {
        // Overpass lookups are the slow part of this request, so fetch raw candidates for both
        // types concurrently rather than back-to-back.
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
            return (restroomCandidates, fountainCandidates,
                $"No public restroom found within {searchRadiusMiles:0.#} miles of your start location. " +
                "Try a longer distance or a different starting point.");
        }

        if (request.WaterFountainCount > 0 && fountainCandidates.Count == 0)
        {
            return (restroomCandidates, fountainCandidates,
                $"No water fountain found within {searchRadiusMiles:0.#} miles of your start location. " +
                "Try a longer distance or a different starting point.");
        }

        return (restroomCandidates, fountainCandidates, null);
    }

    private static string? CheckStopCounts(RouteRequest request, int restroomStopCount, int fountainStopCount, double searchRadiusMiles)
    {
        if (request.RestroomCount > 0 && restroomStopCount < request.RestroomCount)
        {
            return $"Found only {restroomStopCount} of {request.RestroomCount} requested public restroom(s) " +
                $"within {searchRadiusMiles:0.#} miles of your start location. Try a longer distance, a different " +
                "starting point, or fewer restrooms.";
        }

        if (request.WaterFountainCount > 0 && fountainStopCount < request.WaterFountainCount)
        {
            return $"Found only {fountainStopCount} of {request.WaterFountainCount} requested water fountain(s) " +
                $"within {searchRadiusMiles:0.#} miles of your start location. Try a longer distance, a different " +
                "starting point, or fewer fountains.";
        }

        return null;
    }

    // Picks up to `count` distinct candidates, spread across the "out" leg of the route instead
    // of clustered around a single point: the first is targeted at roughly 1/(count+1) of the
    // halfway distance, the second at 2/(count+1), and so on, so successive stops read as being
    // encountered one after another as the run progresses rather than all at once. Landing the
    // last one exactly at the halfway/turnaround point would make it the literal end of the route
    // (OpenRouteServiceClient then has to route further out and back for it to read as "on the
    // way" rather than "the destination") - targeting short of that leaves room for that.
    //
    // Used for Out & back only - see SelectStopsAlongLoop for the loop-path-based equivalent.
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

    // Picks up to `count` distinct candidates for a loop route, preferring ones already close to
    // the loop's actual generated path (minimal detour) and spread across its length instead of
    // clustered at one point: the first is targeted at roughly 1/(count+1) of the total loop
    // distance in, the second at 2/(count+1), and so on.
    //
    // The candidate pool is capped to MaxLoopDetourMeters (widened only as far as strictly
    // needed to find `count` candidates, up to MaxLoopDetourWidenLimitMeters) *before* ranking
    // by arc-length target. Ranking by arc-length match first with no distance cap would let a
    // "perfectly spaced" candidate that's kilometers off the actual path beat one that's a much
    // smaller, more sensible detour but slightly worse-spaced - distance to the loop has to be
    // the primary filter, not a tie-breaker.
    private static List<AmenityStop> SelectStopsAlongLoop(
        IReadOnlyList<AmenityStop> candidates, List<double[]> loopPoints, double totalArcLengthMeters, int count)
    {
        if (candidates.Count == 0 || count == 0)
        {
            return [];
        }

        var projections = candidates
            .Select(c => (Stop: c, Projection: ProjectOntoPolyline(c.Latitude, c.Longitude, loopPoints)))
            .ToList();

        var threshold = MaxLoopDetourMeters;
        var pool = projections.Where(p => p.Projection.DistanceMeters <= threshold).ToList();
        while (pool.Count < count && threshold < MaxLoopDetourWidenLimitMeters)
        {
            threshold = Math.Min(threshold * 2, MaxLoopDetourWidenLimitMeters);
            pool = projections.Where(p => p.Projection.DistanceMeters <= threshold).ToList();
        }

        var used = new HashSet<AmenityStop>();
        var result = new List<AmenityStop>();

        for (var i = 0; i < count && used.Count < pool.Count; i++)
        {
            var targetArcLengthMeters = totalArcLengthMeters * (i + 1) / (count + 1);
            var next = pool
                .Where(p => !used.Contains(p.Stop))
                .OrderBy(p => Math.Abs(p.Projection.ArcLengthMeters - targetArcLengthMeters))
                .ThenBy(p => p.Projection.DistanceMeters)
                .First();

            used.Add(next.Stop);
            result.Add(next.Stop);
        }

        return result;
    }

    private static List<double[]> ExtractRoutePoints(JsonElement routeGeoJson)
    {
        var geometry = routeGeoJson.GetProperty("features")[0].GetProperty("geometry");
        return geometry.GetProperty("coordinates").EnumerateArray()
            .Select(c => new[] { c[0].GetDouble(), c[1].GetDouble() })
            .ToList();
    }

    private static double TotalArcLength(List<double[]> polyline)
    {
        double total = 0;
        for (var i = 0; i < polyline.Count - 1; i++)
        {
            total += GeoMath.DistanceMeters(polyline[i][1], polyline[i][0], polyline[i + 1][1], polyline[i + 1][0]);
        }

        return total;
    }

    // Projects a point onto the closest point of a polyline (planar approximation for the
    // projection math, real haversine distance for the result - fine at street-crossing/detour
    // scale). Returns both the perpendicular distance to the path and how far along the path
    // (from its start) that closest point is, so callers can both filter by "how big a detour"
    // and place stops in path order.
    private static PolylineProjection ProjectOntoPolyline(double lat, double lon, List<double[]> polyline)
    {
        var bestDistance = double.MaxValue;
        var bestArcLength = 0.0;
        var cumulativeArcLength = 0.0;

        for (var i = 0; i < polyline.Count - 1; i++)
        {
            var a = polyline[i];
            var b = polyline[i + 1];
            var segmentLength = GeoMath.DistanceMeters(a[1], a[0], b[1], b[0]);

            var dx = b[0] - a[0];
            var dy = b[1] - a[1];
            var lengthSquared = dx * dx + dy * dy;

            var t = lengthSquared < 1e-15
                ? 0.0
                : Math.Clamp(((lon - a[0]) * dx + (lat - a[1]) * dy) / lengthSquared, 0.0, 1.0);

            var closestLon = a[0] + t * dx;
            var closestLat = a[1] + t * dy;
            var distance = GeoMath.DistanceMeters(lat, lon, closestLat, closestLon);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestArcLength = cumulativeArcLength + t * segmentLength;
            }

            cumulativeArcLength += segmentLength;
        }

        return new PolylineProjection(bestDistance, bestArcLength);
    }

    private readonly record struct PolylineProjection(double DistanceMeters, double ArcLengthMeters);

    // Builds the waypoint list for GenerateAlongGuidedPathAsync: the original loop's own path,
    // downsampled to MaxLoopAnchorPoints points, with the selected amenity stops merged in by
    // arc-length position. Passing ORS the loop's own points as intermediate waypoints (not just
    // the stops) forces its shortest-pathing to hug the original shape between detours, instead
    // of independently shortest-pathing between stops and potentially diverging from the loop
    // entirely - see the comment at the call site for the 6.24mi-for-a-3mi-loop result that
    // motivated this.
    private static List<double[]> BuildGuidedLoopCoordinates(List<double[]> loopPoints, List<AmenityStop> sortedStops)
    {
        var anchors = new List<(double[] Point, double ArcLengthMeters)>();
        var step = Math.Max(1, (int)Math.Ceiling((loopPoints.Count - 1) / (double)(MaxLoopAnchorPoints - 1)));
        var cumulativeArcLength = 0.0;

        for (var i = 0; i < loopPoints.Count; i++)
        {
            if (i > 0)
            {
                cumulativeArcLength += GeoMath.DistanceMeters(
                    loopPoints[i - 1][1], loopPoints[i - 1][0], loopPoints[i][1], loopPoints[i][0]);
            }

            if (i % step == 0 || i == loopPoints.Count - 1)
            {
                anchors.Add((loopPoints[i], cumulativeArcLength));
            }
        }

        var combined = anchors
            .Select(a => (a.Point, a.ArcLengthMeters))
            .Concat(sortedStops.Select(s => (
                Point: new[] { s.Longitude, s.Latitude },
                ArcLengthMeters: ProjectOntoPolyline(s.Latitude, s.Longitude, loopPoints).ArcLengthMeters)))
            .OrderBy(c => c.ArcLengthMeters)
            .Select(c => c.Point)
            .ToList();

        return combined;
    }
}
