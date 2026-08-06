namespace RunningRoutes.Api.Models;

public enum AmenityType
{
    Restroom,
    WaterFountain
}

/// <summary>A restroom or water fountain a route is routed through, for highlighting on the map.</summary>
public class AmenityStop
{
    public AmenityType Type { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Name { get; set; }
}
