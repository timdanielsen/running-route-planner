using System.Globalization;
using System.Text.Json;
using RunningRoutes.Api.Models;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Flags places where a generated route crosses a busy road with no marked pedestrian crossing
/// nearby. ORS's foot-walking routing has no concept of this at all - per its own maintainers,
/// "no penalty is applied as traffic lights are not taken into account" for crossings, marked or
/// not - so this can't steer route *generation* away from bad crossings, only flag them
/// afterward on whatever route ORS already produced.
///
/// "Busy" means OSM highway=primary/secondary/tertiary/trunk (plus their _link variants) -
/// residential/service/unclassified streets aren't expected to have marked crossings and
/// flagging every one of those would just be noise.
/// </summary>
public class CrossingSafetyService : ICrossingSafetyService
{
    private static readonly string[] BusyHighwayValues =
    [
        "primary", "primary_link",
        "secondary", "secondary_link",
        "tertiary", "tertiary_link",
        "trunk", "trunk_link",
    ];

    // How close a mapped highway=crossing node has to be to a detected road-crossing point to
    // count as "this crossing is marked" - generous enough to cover GPS/geometry slop between
    // the routed path and the crossing node's actual position, tight enough to not credit a
    // crossing three blocks away.
    private const double CrossingNodeToleranceMeters = 20.0;

    // Multiple route segments can clip the same real-world intersection at nearly the same
    // point; collapse detections within this distance into a single warning.
    private const double DedupeToleranceMeters = 15.0;

    private readonly IOverpassClient _overpass;
    private readonly ILogger<CrossingSafetyService> _logger;

    public CrossingSafetyService(IOverpassClient overpass, ILogger<CrossingSafetyService> logger)
    {
        _overpass = overpass;
        _logger = logger;
    }

    public async Task<List<CrossingWarning>> FindUnmarkedCrossingsAsync(JsonElement routeGeoJson, CancellationToken ct)
    {
        try
        {
            var routePoints = ExtractRoutePoints(routeGeoJson);
            if (routePoints.Count < 2)
            {
                return [];
            }

            var (south, west, north, east) = RouteBoundingBox(routePoints);
            var highwayFilter = string.Join("|", BusyHighwayValues);
            var query = $$"""
                [out:json][timeout:20];
                (
                  way["highway"~"^({{highwayFilter}})$"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
                  node["highway"="crossing"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
                );
                out geom;
                """;

            var doc = await _overpass.QueryAsync(query, ct);
            if (doc is null || !doc.Value.TryGetProperty("elements", out var elements))
            {
                return [];
            }

            var busyRoads = new List<(double[][] Points, string? Name)>();
            var crossingNodes = new List<double[]>();

            foreach (var element in elements.EnumerateArray())
            {
                var type = element.GetProperty("type").GetString();
                if (type == "way" && element.TryGetProperty("geometry", out var geom))
                {
                    string? name = null;
                    if (element.TryGetProperty("tags", out var tags) && tags.TryGetProperty("name", out var nameProp))
                    {
                        name = nameProp.GetString();
                    }

                    var points = geom.EnumerateArray()
                        .Select(p => new[] { p.GetProperty("lon").GetDouble(), p.GetProperty("lat").GetDouble() })
                        .ToArray();
                    busyRoads.Add((points, name));
                }
                else if (type == "node")
                {
                    crossingNodes.Add([element.GetProperty("lon").GetDouble(), element.GetProperty("lat").GetDouble()]);
                }
            }

            var detections = new List<CrossingWarning>();
            for (var i = 0; i < routePoints.Count - 1; i++)
            {
                var a = routePoints[i];
                var b = routePoints[i + 1];

                foreach (var (roadPoints, roadName) in busyRoads)
                {
                    for (var j = 0; j < roadPoints.Length - 1; j++)
                    {
                        if (!TrySegmentIntersection(a, b, roadPoints[j], roadPoints[j + 1], out var hit))
                        {
                            continue;
                        }

                        var nearCrossing = crossingNodes.Any(node =>
                            GeoMath.DistanceMeters(hit[1], hit[0], node[1], node[0]) <= CrossingNodeToleranceMeters);

                        if (!nearCrossing)
                        {
                            detections.Add(new CrossingWarning { Latitude = hit[1], Longitude = hit[0], RoadName = roadName });
                        }
                    }
                }
            }

            return Dedupe(detections);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Crossing safety check failed; returning no warnings.");
            return [];
        }
    }

    private static List<double[]> ExtractRoutePoints(JsonElement routeGeoJson)
    {
        var geometry = routeGeoJson.GetProperty("features")[0].GetProperty("geometry");
        return geometry.GetProperty("coordinates").EnumerateArray()
            .Select(c => new[] { c[0].GetDouble(), c[1].GetDouble() })
            .ToList();
    }

    private static (double South, double West, double North, double East) RouteBoundingBox(List<double[]> points)
    {
        var south = points.Min(p => p[1]);
        var north = points.Max(p => p[1]);
        var west = points.Min(p => p[0]);
        var east = points.Max(p => p[0]);

        // Small buffer so a busy road that runs just outside the exact route bbox (but whose
        // crossing point is right at the edge) isn't missed.
        const double bufferDegrees = 0.001;
        return (south - bufferDegrees, west - bufferDegrees, north + bufferDegrees, east + bufferDegrees);
    }

    // Standard 2D segment intersection via parametric line equations. Treats lat/lon as planar
    // (x, y) coordinates, which is a fine approximation at street-crossing scale.
    private static bool TrySegmentIntersection(double[] p1, double[] p2, double[] p3, double[] p4, out double[] intersection)
    {
        intersection = [0, 0];

        var d1X = p2[0] - p1[0];
        var d1Y = p2[1] - p1[1];
        var d2X = p4[0] - p3[0];
        var d2Y = p4[1] - p3[1];

        var denominator = d1X * d2Y - d1Y * d2X;
        if (Math.Abs(denominator) < 1e-15)
        {
            return false; // Parallel or collinear.
        }

        var t = ((p3[0] - p1[0]) * d2Y - (p3[1] - p1[1]) * d2X) / denominator;
        var u = ((p3[0] - p1[0]) * d1Y - (p3[1] - p1[1]) * d1X) / denominator;

        if (t is < 0 or > 1 || u is < 0 or > 1)
        {
            return false;
        }

        intersection = [p1[0] + t * d1X, p1[1] + t * d1Y];
        return true;
    }

    private static List<CrossingWarning> Dedupe(List<CrossingWarning> detections)
    {
        var result = new List<CrossingWarning>();
        foreach (var detection in detections)
        {
            var alreadyCaptured = result.Any(existing =>
                GeoMath.DistanceMeters(existing.Latitude, existing.Longitude, detection.Latitude, detection.Longitude) <= DedupeToleranceMeters);

            if (!alreadyCaptured)
            {
                result.Add(detection);
            }
        }

        return result;
    }

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
