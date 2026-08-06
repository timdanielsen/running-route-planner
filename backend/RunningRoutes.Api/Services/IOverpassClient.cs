using System.Text.Json;

namespace RunningRoutes.Api.Services;

public interface IOverpassClient
{
    /// <summary>
    /// Runs an Overpass QL query against the public Overpass API and returns the parsed
    /// response, or null if it's unreachable/times out/errors after retrying once. Overpass
    /// lookups are always best-effort in this app - callers should treat null the same as
    /// "found nothing", never let it fail the caller's own operation.
    /// </summary>
    Task<JsonElement?> QueryAsync(string query, CancellationToken ct);
}
