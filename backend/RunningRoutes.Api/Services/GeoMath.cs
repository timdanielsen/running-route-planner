namespace RunningRoutes.Api.Services;

/// <summary>
/// Small spherical-earth helpers. Good enough for picking a "run this far in this
/// direction" waypoint for out-and-back routes - we don't need ellipsoidal precision here.
/// </summary>
public static class GeoMath
{
    private const double EarthRadiusMeters = 6371000;

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

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;
}
