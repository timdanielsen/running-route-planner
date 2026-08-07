import { useState } from "react";
import { findNearbyAmenities } from "../api.js";

/**
 * Pure lookup/display: "what's around here", independent of route generation. Reuses whatever
 * start location is already set in RouteForm rather than asking for its own coordinates.
 */
export default function NearbyAmenitiesPanel({ start, onResults }) {
  const [showRestroom, setShowRestroom] = useState(false);
  const [showWaterFountain, setShowWaterFountain] = useState(false);
  const [radiusMiles, setRadiusMiles] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  async function handleShowNearby() {
    if (!start) return;
    setLoading(true);
    setError(null);
    try {
      const results = await findNearbyAmenities({
        latitude: start.latitude,
        longitude: start.longitude,
        radiusMiles: Number(radiusMiles),
        restroom: showRestroom,
        waterFountain: showWaterFountain,
      });
      onResults(results);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="field">
      <label>Explore nearby</label>
      <div className="checkbox-row">
        <input
          type="checkbox"
          id="showRestroom"
          checked={showRestroom}
          onChange={(e) => setShowRestroom(e.target.checked)}
        />
        <label htmlFor="showRestroom">Public restroom</label>
      </div>
      <div className="checkbox-row">
        <input
          type="checkbox"
          id="showWaterFountain"
          checked={showWaterFountain}
          onChange={(e) => setShowWaterFountain(e.target.checked)}
        />
        <label htmlFor="showWaterFountain">Water fountain</label>
      </div>
      <label htmlFor="nearbyRadius" style={{ marginTop: "0.5rem" }}>
        Radius (mi)
      </label>
      <input
        id="nearbyRadius"
        type="number"
        min="0.1"
        step="0.1"
        value={radiusMiles}
        onChange={(e) => setRadiusMiles(e.target.value)}
      />
      <button
        type="button"
        onClick={handleShowNearby}
        disabled={loading || !start || (!showRestroom && !showWaterFountain)}
        style={{ marginTop: "0.5rem" }}
      >
        {loading ? "Searching…" : "Show nearby"}
      </button>
      {error && <div className="error">{error}</div>}
    </div>
  );
}
