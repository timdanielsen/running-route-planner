import { useState } from "react";
import RouteForm from "./components/RouteForm.jsx";
import RouteMap from "./components/RouteMap.jsx";
import NearbyAmenitiesPanel from "./components/NearbyAmenitiesPanel.jsx";
import { generateRoute } from "./api.js";

const METERS_PER_MILE = 1609.344;

export default function App() {
  const [routeGeoJson, setRouteGeoJson] = useState(null);
  const [amenityStops, setAmenityStops] = useState([]);
  const [nearbyAmenities, setNearbyAmenities] = useState([]);
  const [crossingWarnings, setCrossingWarnings] = useState([]);
  const [busyRoadPercent, setBusyRoadPercent] = useState(0);
  const [stats, setStats] = useState(null);
  const [start, setStart] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  async function handleSubmit(params) {
    setLoading(true);
    setError(null);
    try {
      const result = await generateRoute(params);
      setRouteGeoJson(result.geoJson);
      setAmenityStops(result.amenityStops ?? []);
      setCrossingWarnings(result.crossingWarnings ?? []);
      setBusyRoadPercent(result.busyRoadPercent ?? 0);
      setStats({
        distanceMiles: (result.distanceMeters / METERS_PER_MILE).toFixed(2),
        durationMin: (result.durationSeconds / 60).toFixed(0),
      });
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="app">
      <div className="sidebar">
        <h2>Running Route Planner</h2>
        <RouteForm onSubmit={handleSubmit} onLocationChange={setStart} loading={loading} />
        {error && <div className="error">{error}</div>}
        {stats && (
          <div className="stats">
            <div>Distance: {stats.distanceMiles} mi</div>
          </div>
        )}
        {crossingWarnings.length > 0 && (
          <div className="warning">
            This route crosses {crossingWarnings.length === 1 ? "a busy road" : `${crossingWarnings.length} busy roads`} with
            no marked crossing nearby (see the map). Watch for traffic there.
          </div>
        )}
        {busyRoadPercent >= 0.5 && (
          <div className="warning">
            About {Math.round(busyRoadPercent)}% of this route runs directly on a road (no sidewalk/path
            distinction in the map data) rather than a dedicated footway or trail.
          </div>
        )}
        <NearbyAmenitiesPanel start={start} onResults={setNearbyAmenities} />
      </div>
      <RouteMap
        routeGeoJson={routeGeoJson}
        start={start}
        amenityStops={amenityStops}
        nearbyAmenities={nearbyAmenities}
        crossingWarnings={crossingWarnings}
      />
    </div>
  );
}
