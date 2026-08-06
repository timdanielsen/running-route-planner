using Microsoft.AspNetCore.Mvc;
using RunningRoutes.Api.Models;
using RunningRoutes.Api.Services;

namespace RunningRoutes.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoutesController : ControllerBase
{
    private readonly IOpenRouteServiceClient _orsClient;
    private readonly ILogger<RoutesController> _logger;

    public RoutesController(IOpenRouteServiceClient orsClient, ILogger<RoutesController> logger)
    {
        _orsClient = orsClient;
        _logger = logger;
    }

    /// <summary>
    /// Generates a running route starting at the given point.
    /// POST /api/routes
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RouteResult>> Generate([FromBody] RouteRequest request, CancellationToken ct)
    {
        if (request.DistanceKm <= 0)
        {
            return BadRequest("distanceKm must be greater than 0.");
        }

        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
        {
            return BadRequest("latitude/longitude are out of range.");
        }

        try
        {
            var result = request.Type switch
            {
                RouteType.Loop => await _orsClient.GenerateLoopAsync(
                    request.Latitude, request.Longitude, request.DistanceKm, request.Seed, ct),
                RouteType.OutAndBack => await _orsClient.GenerateOutAndBackAsync(
                    request.Latitude, request.Longitude, request.DistanceKm, request.BearingDegrees, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Type), request.Type, "Unknown route type.")
            };

            return Ok(result);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Routing provider error");
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }
}
