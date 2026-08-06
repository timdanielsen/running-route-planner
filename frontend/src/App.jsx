import { useState } from "react";
import RouteForm from "./components/RouteForm.jsx";
import RouteMap from "./components/RouteMap.jsx";
import { generateRoute } from "./api.js";

const METERS_PER_MILE = 1609.344;

export default function App() {
  const [routeGeoJson, setRouteGeoJson] = useState(null);
  const [amenityStops, setAmenityStops] = useState([]);
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
      </div>
      <RouteMap routeGeoJson={routeGeoJson} start={start} amenityStops={amenityStops} />
    </div>
  );
}
