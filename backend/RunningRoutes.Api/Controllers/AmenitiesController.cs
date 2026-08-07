using Microsoft.AspNetCore.Mvc;
using RunningRoutes.Api.Models;
using RunningRoutes.Api.Services;

namespace RunningRoutes.Api.Controllers;

/// <summary>
/// Pure lookup/display endpoint: "what's around here", with no route involved. Unlike
/// RoutesController (which picks a handful of amenities to route through), this returns every
/// match found within the radius.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AmenitiesController : ControllerBase
{
    private const double MaxRadiusMiles = 15.0;

    private readonly IAmenityLookupService _amenityLookup;

    public AmenitiesController(IAmenityLookupService amenityLookup)
    {
        _amenityLookup = amenityLookup;
    }

    /// <summary>
    /// GET /api/amenities?latitude=..&amp;longitude=..&amp;radiusMiles=..&amp;restroom=true&amp;waterFountain=true
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<AmenityStop>>> Find(
        [FromQuery] double latitude,
        [FromQuery] double longitude,
        [FromQuery] double radiusMiles,
        [FromQuery] bool restroom,
        [FromQuery] bool waterFountain,
        CancellationToken ct)
    {
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            return BadRequest("latitude/longitude are out of range.");
        }

        if (radiusMiles <= 0)
        {
            return BadRequest("radiusMiles must be greater than 0.");
        }

        if (!restroom && !waterFountain)
        {
            return Ok(new List<AmenityStop>());
        }

        var cappedRadiusMiles = Math.Min(radiusMiles, MaxRadiusMiles);

        var restroomTask = restroom
            ? _amenityLookup.FindNearbyAsync(latitude, longitude, cappedRadiusMiles, AmenityType.Restroom, ct)
            : Task.FromResult<IReadOnlyList<AmenityStop>>([]);
        var fountainTask = waterFountain
            ? _amenityLookup.FindNearbyAsync(latitude, longitude, cappedRadiusMiles, AmenityType.WaterFountain, ct)
            : Task.FromResult<IReadOnlyList<AmenityStop>>([]);
        await Task.WhenAll(restroomTask, fountainTask);

        return Ok(restroomTask.Result.Concat(fountainTask.Result).ToList());
    }
}
