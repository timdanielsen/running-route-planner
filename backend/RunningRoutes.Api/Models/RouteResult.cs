using System.Text.Json;

namespace RunningRoutes.Api.Models;

/// <summary>
/// What we hand back to the frontend: the route as GeoJSON (ready to drop straight into
/// a Leaflet &lt;GeoJSON&gt; layer) plus some summary stats.
/// </summary>
public class RouteResult
{
    public required JsonElement GeoJson { get; set; }
    public double DistanceMeters { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>Restrooms/water fountains the route was routed through, for highlighting on the map.</summary>
    public List<AmenityStop> AmenityStops { get; set; } = [];

    /// <summary>Points where the route crosses a busy road with no marked crossing nearby.</summary>
    public List<CrossingWarning> CrossingWarnings { get; set; } = [];

    /// <summary>
    /// Percent (0-100) of the route's distance spent walking directly on a secondary/tertiary/
    /// primary/trunk road, as opposed to a dedicated footway/path/track or a quiet residential
    /// street. From ORS's own waytype breakdown, not a separate lookup.
    /// </summary>
    public double BusyRoadPercent { get; set; }
}
