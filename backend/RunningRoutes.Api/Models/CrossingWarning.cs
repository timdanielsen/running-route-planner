namespace RunningRoutes.Api.Models;

/// <summary>
/// A point where the generated route crosses a busy road (primary/secondary/tertiary/trunk)
/// with no marked pedestrian crossing nearby. ORS's own routing has no concept of crossing
/// safety (see CrossingSafetyService), so this is detected after the fact, not avoided during
/// generation - it's a heads-up, not a guarantee the route was rerouted around it.
/// </summary>
public class CrossingWarning
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Name of the road being crossed, if OSM has one tagged.</summary>
    public string? RoadName { get; set; }
}
