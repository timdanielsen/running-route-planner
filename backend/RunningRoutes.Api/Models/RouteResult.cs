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
}
