import { useState } from "react";
import RouteForm from "./components/RouteForm.jsx";
import RouteMap from "./components/RouteMap.jsx";
import { generateRoute } from "./api.js";

export default function App() {
  const [routeGeoJson, setRouteGeoJson] = useState(null);
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
      setStats({
        distanceKm: (result.distanceMeters / 1000).toFixed(2),
        durationMin: (result.durationSeconds / 60).toFixed(0),
      });
      setStart({ latitude: params.latitude, longitude: params.longitude });
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
        <RouteForm onSubmit={handleSubmit} loading={loading} />
        {error && <div className="error">{error}</div>}
        {stats && (
          <div className="stats">
            <div>Distance: {stats.distanceKm} km</div>
            <div>Est. time: {stats.durationMin} min</div>
          </div>
        )}
      </div>
      <RouteMap routeGeoJson={routeGeoJson} start={start} />
    </div>
  );
}
