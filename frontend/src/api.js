/**
 * Calls the ASP.NET Core backend, which in turn calls OpenRouteService.
 * Vite's dev proxy (see vite.config.js) forwards /api/* to http://localhost:5080.
 */
export async function generateRoute({
  latitude,
  longitude,
  distanceMiles,
  type,
  bearingDegrees,
  seed,
  restroomCount,
  waterFountainCount,
}) {
  const response = await fetch("/api/routes", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      latitude,
      longitude,
      distanceMiles,
      type, // "Loop" | "OutAndBack" - matches the RouteType enum on the backend
      bearingDegrees,
      seed,
      restroomCount,
      waterFountainCount,
    }),
  });

  if (!response.ok) {
    const body = await response.text();
    throw new Error(`Route request failed (${response.status}): ${body}`);
  }

  return response.json();
}

export async function findNearbyAmenities({ latitude, longitude, radiusMiles, restroom, waterFountain }) {
  const params = new URLSearchParams({
    latitude,
    longitude,
    radiusMiles,
    restroom,
    waterFountain,
  });
  const response = await fetch(`/api/amenities?${params.toString()}`);

  if (!response.ok) {
    const body = await response.text();
    throw new Error(`Nearby amenities request failed (${response.status}): ${body}`);
  }

  return response.json();
}
