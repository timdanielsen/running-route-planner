namespace RunningRoutes.Api.Services;

/// <summary>
/// Small spherical-earth helpers. Good enough for picking a "run this far in this
/// direction" waypoint for out-and-back routes - we don't need ellipsoidal precision here.
/// </summary>
public static class GeoMath
{
    private const double EarthRadiusMeters = 6371000;
    public const double MetersPerMile = 1609.344;
    private const double MilesPerDegreeLatitude = 69.0;

    /// <summary>
    /// Returns the lat/lon reached by travelling <paramref name="distanceMeters"/> from
    /// (<paramref name="lat"/>, <paramref name="lon"/>) along <paramref name="bearingDegrees"/>
    /// (0 = north, 90 = east, clockwise).
    /// </summary>
    public static (double Lat, double Lon) Destination(double lat, double lon, double bearingDegrees, double distanceMeters)
    {
        var angularDistance = distanceMeters / EarthRadiusMeters;
        var bearing = DegreesToRadians(bearingDegrees);
        var lat1 = DegreesToRadians(lat);
        var lon1 = DegreesToRadians(lon);

        var lat2 = Math.Asin(
            Math.Sin(lat1) * Math.Cos(angularDistance) +
            Math.Cos(lat1) * Math.Sin(angularDistance) * Math.Cos(bearing));

        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(lat1),
            Math.Cos(angularDistance) - Math.Sin(lat1) * Math.Sin(lat2));

        return (RadiansToDegrees(lat2), RadiansToDegrees(lon2));
    }

    /// <summary>Initial compass bearing (0 = north, 90 = east) from point 1 to point 2.</summary>
    public static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var deltaLon = DegreesToRadians(lon2 - lon1);

        var y = Math.Sin(deltaLon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLon);

        return (RadiansToDegrees(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    /// <summary>Great-circle distance between two points, in meters.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    /// <summary>A lat/lon bounding box roughly <paramref name="radiusMiles"/> around a point.</summary>
    public static (double South, double West, double North, double East) BoundingBox(double lat, double lon, double radiusMiles)
    {
        var latDelta = radiusMiles / MilesPerDegreeLatitude;
        var milesPerDegreeLongitude = MilesPerDegreeLatitude * Math.Cos(lat * Math.PI / 180.0);
        var lonDelta = radiusMiles / Math.Max(milesPerDegreeLongitude, 1.0);

        return (lat - latDelta, lon - lonDelta, lat + latDelta, lon + lonDelta);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
}
