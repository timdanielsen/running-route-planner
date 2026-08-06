import { useEffect, useState } from "react";

const DEFAULT_START = { latitude: 40.7128, longitude: -74.006 }; // NYC, just as a placeholder

export default function RouteForm({ onSubmit, onLocationChange, loading }) {
  const [latitude, setLatitude] = useState(DEFAULT_START.latitude);
  const [longitude, setLongitude] = useState(DEFAULT_START.longitude);
  const [distanceMiles, setDistanceMiles] = useState(3);
  const [type, setType] = useState("Loop");
  const [locating, setLocating] = useState(false);

  useEffect(() => {
    const lat = Number(latitude);
    const lon = Number(longitude);
    if (Number.isFinite(lat) && Number.isFinite(lon)) {
      onLocationChange({ latitude: lat, longitude: lon });
    }
  }, [latitude, longitude, onLocationChange]);

  function useMyLocation() {
    if (!navigator.geolocation) return;
    setLocating(true);
    navigator.geolocation.getCurrentPosition(
      (position) => {
        setLatitude(position.coords.latitude);
        setLongitude(position.coords.longitude);
        setLocating(false);
      },
      () => setLocating(false)
    );
  }

  function handleSubmit(e) {
    e.preventDefault();
    onSubmit({
      latitude: Number(latitude),
      longitude: Number(longitude),
      distanceMiles: Number(distanceMiles),
      type,
    });
  }

  return (
    <form onSubmit={handleSubmit}>
      <div className="field">
        <label>Start location</label>
        <input
          type="number"
          step="any"
          placeholder="Latitude"
          value={latitude}
          onChange={(e) => setLatitude(e.target.value)}
        />
        <input
          type="number"
          step="any"
          placeholder="Longitude"
          value={longitude}
          onChange={(e) => setLongitude(e.target.value)}
          style={{ marginTop: "0.4rem" }}
        />
        <button type="button" onClick={useMyLocation} style={{ marginTop: "0.4rem" }} disabled={locating}>
          {locating ? "Locating…" : "Use my location"}
        </button>
      </div>

      <div className="field">
        <label>Distance (mi)</label>
        <input
          type="number"
          min="0.5"
          step="0.5"
          value={distanceMiles}
          onChange={(e) => setDistanceMiles(e.target.value)}
        />
      </div>

      <div className="field">
        <label>Route shape</label>
        <div className="radio-row">
          <label>
            <input
              type="radio"
              name="type"
              value="Loop"
              checked={type === "Loop"}
              onChange={() => setType("Loop")}
            />
            {" "}Loop
          </label>
          <label>
            <input
              type="radio"
              name="type"
              value="OutAndBack"
              checked={type === "OutAndBack"}
              onChange={() => setType("OutAndBack")}
            />
            {" "}Out &amp; back
          </label>
        </div>
      </div>

      <button type="submit" disabled={loading}>
        {loading ? "Generating…" : "Generate route"}
      </button>
    </form>
  );
}
