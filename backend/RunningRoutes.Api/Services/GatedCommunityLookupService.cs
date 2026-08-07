using System.Globalization;
using System.Text.Json;

namespace RunningRoutes.Api.Services;

/// <summary>
/// Looks up gated-community boundaries and private-access streets from OpenStreetMap via
/// Overpass, so routes can be steered around them with ORS's avoid_polygons option - the same
/// approach GraveyardLookupService uses. Two distinct OSM signals, combined into one set of
/// avoid rings:
///   - landuse=residential + residential=gated: an actual gated-community boundary polygon.
///     Rarely tagged - most gated communities in OSM simply aren't marked this way - so this
///     alone catches only a minority of them.
///   - highway=* with access=private|no: individual private streets. Tagged far more often than
///     the community boundary itself (mappers tend to tag the street they can see, not the
///     informal community boundary around it), so this is the signal that catches most real
///     cases. These come back from Overpass as lines, not areas, so each way is buffered into a
///     thin corridor polygon before being added.
///
/// The two signals are queried over very different radii: access=private matches nearly every
/// driveway, parking-lot access road, alley, and HOA street in an area, not just gated
/// communities - a real test over a 15mi-radius bbox near Naperville, IL came back with 12,505
/// matching ways. Turning that many into polygons and handing them to ORS in one request made
/// the connection fail outright while writing the (enormous) body, not just respond slowly. See
/// MaxPrivateStreetSearchRadiusMiles/MaxPrivateStreetRings below for the two independent caps
/// that bound this regardless of how dense an area's private-street tagging is.
///
/// Best-effort by design, same as GraveyardLookupService: malformed geometry or an unreachable
/// Overpass should never block route generation, only skip avoidance.
/// </summary>
public class GatedCommunityLookupService : IGatedCommunityLookupService
{
    // Half-width of the polygon built around each private street. Wide enough to reliably
    // intersect the road's own OSM geometry (which has no width of its own) even with a few
    // meters of mapping slop, narrow enough not to swallow a parallel public street in a
    // tightly-platted subdivision (typical residential block spacing is well over 60m).
    private const double PrivateStreetBufferMeters = 15.0;

    // Unlike graveyards (a metro area might have a few dozen), private-access ways are dense
    // enough that searching the full route radius isn't viable - gated communities are a local
    // feature near the start point, not something that needs full route-radius coverage the way
    // avoid_polygons for a graveyard does. Independent of, and much smaller than, the radius used
    // for the gated-boundary polygon query.
    private const double MaxPrivateStreetSearchRadiusMiles = 3.0;

    // Second, independent safety net on top of the radius cap: even within 3 miles, a dense
    // urban core can still return more ways than are worth sending to ORS in one request. Keep
    // only the closest ones to the search origin.
    private const int MaxPrivateStreetRings = 150;

    private readonly IOverpassClient _overpass;

    public GatedCommunityLookupService(IOverpassClient overpass)
    {
        _overpass = overpass;
    }

    public async Task<IReadOnlyList<double[][]>> FindNearbyGatedAreasAsync(double lat, double lon, double radiusMiles, CancellationToken ct)
    {
        var (south, west, north, east) = GeoMath.BoundingBox(lat, lon, radiusMiles);

        var privateStreetRadiusMiles = Math.Min(radiusMiles, MaxPrivateStreetSearchRadiusMiles);
        var (pSouth, pWest, pNorth, pEast) = GeoMath.BoundingBox(lat, lon, privateStreetRadiusMiles);

        var query = $$"""
            [out:json][timeout:20];
            (
              way["landuse"="residential"]["residential"="gated"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
              relation["landuse"="residential"]["residential"="gated"]({{Fmt(south)}},{{Fmt(west)}},{{Fmt(north)}},{{Fmt(east)}});
              way["highway"]["access"~"^(private|no)$"]["service"!="driveway"]({{Fmt(pSouth)}},{{Fmt(pWest)}},{{Fmt(pNorth)}},{{Fmt(pEast)}});
            );
            out geom;
            """;

        var doc = await _overpass.QueryAsync(query, ct);
        return doc is null ? [] : ParseRings(doc.Value, lat, lon);
    }

    private static List<double[][]> ParseRings(JsonElement doc, double centerLat, double centerLon)
    {
        var rings = new List<double[][]>();
        var streetCandidates = new List<(double[][] Ring, double DistanceMeters)>();

        if (!doc.TryGetProperty("elements", out var elements))
        {
            return rings;
        }

        foreach (var element in elements.EnumerateArray())
        {
            var type = element.GetProperty("type").GetString();

            if (type == "way" && element.TryGetProperty("geometry", out var wayGeom))
            {
                var points = ReadPoints(wayGeom);
                var isPrivateStreet = element.TryGetProperty("tags", out var tags) &&
                    tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("highway", out _);

                if (isPrivateStreet)
                {
                    var ring = BuildBufferedRing(points);
                    if (ring is not null)
                    {
                        var distanceMeters = GeoMath.DistanceMeters(centerLat, centerLon, points[0][1], points[0][0]);
                        streetCandidates.Add((ring, distanceMeters));
                    }
                }
                else
                {
                    AddClosedRingIfValid(rings, points);
                }
            }
            else if (type == "relation" && element.TryGetProperty("members", out var members))
            {
                foreach (var member in members.EnumerateArray())
                {
                    var role = member.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : null;
                    if (role == "outer" && member.TryGetProperty("geometry", out var memberGeom))
                    {
                        AddClosedRingIfValid(rings, ReadPoints(memberGeom));
                    }
                }
            }
        }

        rings.AddRange(streetCandidates
            .OrderBy(c => c.DistanceMeters)
            .Take(MaxPrivateStreetRings)
            .Select(c => c.Ring));

        return rings;
    }

    private static List<double[]> ReadPoints(JsonElement geometry)
    {
        var points = new List<double[]>();
        foreach (var node in geometry.EnumerateArray())
        {
            var lat = node.GetProperty("lat").GetDouble();
            var lon = node.GetProperty("lon").GetDouble();
            points.Add([lon, lat]);
        }

        return points;
    }

    // A GeoJSON polygon ring needs at least 4 positions (3 distinct + closing point) and must be
    // closed; Overpass "outer" relation members aren't always closed on their own since they're
    // sometimes assembled from multiple segments in the full relation.
    private static void AddClosedRingIfValid(List<double[][]> rings, List<double[]> points)
    {
        if (points.Count < 3)
        {
            return;
        }

        if (points[0][0] != points[^1][0] || points[0][1] != points[^1][1])
        {
            points.Add(points[0]);
        }

        if (points.Count >= 4)
        {
            rings.Add(points.ToArray());
        }
    }

    // Private streets come back from Overpass as a line, not an area - avoid_polygons needs a
    // closed ring, so this traces a corridor PrivateStreetBufferMeters wide down each side of
    // the street and closes it into a rectangle-ish ring around the whole way.
    private static double[][]? BuildBufferedRing(List<double[]> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var left = new double[points.Count][];
        var right = new double[points.Count][];

        for (var i = 0; i < points.Count; i++)
        {
            var bearing = i switch
            {
                0 => GeoMath.Bearing(points[0][1], points[0][0], points[1][1], points[1][0]),
                _ when i == points.Count - 1 => GeoMath.Bearing(points[i - 1][1], points[i - 1][0], points[i][1], points[i][0]),
                _ => AverageBearing(
                    GeoMath.Bearing(points[i - 1][1], points[i - 1][0], points[i][1], points[i][0]),
                    GeoMath.Bearing(points[i][1], points[i][0], points[i + 1][1], points[i + 1][0]))
            };

            var leftPoint = GeoMath.Destination(points[i][1], points[i][0], bearing - 90.0, PrivateStreetBufferMeters);
            var rightPoint = GeoMath.Destination(points[i][1], points[i][0], bearing + 90.0, PrivateStreetBufferMeters);
            left[i] = [leftPoint.Lon, leftPoint.Lat];
            right[i] = [rightPoint.Lon, rightPoint.Lat];
        }

        var ring = new List<double[]>(points.Count * 2 + 1);
        ring.AddRange(left);
        ring.AddRange(right.Reverse());
        ring.Add(ring[0]);

        return ring.ToArray();
    }

    // Averages two compass bearings correctly across the 0/360 wraparound (a naive (b1+b2)/2
    // breaks e.g. averaging 350 and 10, which should land on 0/360, not 180) by averaging their
    // unit vectors instead.
    private static double AverageBearing(double bearingA, double bearingB)
    {
        var radiansA = bearingA * Math.PI / 180.0;
        var radiansB = bearingB * Math.PI / 180.0;
        var x = Math.Cos(radiansA) + Math.Cos(radiansB);
        var y = Math.Sin(radiansA) + Math.Sin(radiansB);
        var average = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (average + 360.0) % 360.0;
    }

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
