namespace RunningRoutes.Api.Models;

public enum RouteType
{
    Loop,
    OutAndBack
}

/// <summary>
/// What the frontend sends us: where to start, how far to run, and the shape of the route.
/// </summary>
public class RouteRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Target distance in kilometers.</summary>
    public double DistanceKm { get; set; }

    public RouteType Type { get; set; } = RouteType.Loop;

    /// <summary>
    /// Optional compass bearing in degrees (0 = north, 90 = east) used for out-and-back routes
    /// to pick a direction. If null, a random bearing is chosen.
    /// </summary>
    public double? BearingDegrees { get; set; }

    /// <summary>
    /// Optional seed so a loop request is reproducible/re-rollable. If null, ORS picks randomly.
    /// </summary>
    public int? Seed { get; set; }
}
