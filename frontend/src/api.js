/**
 * Calls the ASP.NET Core backend, which in turn calls OpenRouteService.
 * Vite's dev proxy (see vite.config.js) forwards /api/* to http://localhost:5080.
 */
export async function generateRoute({ latitude, longitude, distanceKm, type, bearingDegrees, seed }) {
  const response = await fetch("/api/routes", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      latitude,
      longitude,
      distanceKm,
      type, // "Loop" | "OutAndBack" - matches the RouteType enum on the backend
      bearingDegrees,
      seed,
    }),
  });

  if (!response.ok) {
    const body = await response.text();
    throw new Error(`Route request failed (${response.status}): ${body}`);
  }

  return response.json();
}
