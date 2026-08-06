import { useEffect } from "react";
import { MapContainer, TileLayer, GeoJSON, Marker, useMap } from "react-leaflet";

const NYC = [40.7128, -74.006];

// MapContainer's `center` prop only sets the *initial* view, so once the map is
// mounted we have to imperatively pan it whenever the start location changes.
function RecenterOnStartChange({ latitude, longitude }) {
  const map = useMap();

  useEffect(() => {
    if (latitude != null && longitude != null) {
      map.setView([latitude, longitude], map.getZoom());
    }
  }, [latitude, longitude]);

  return null;
}

export default function RouteMap({ routeGeoJson, start }) {
  const center = start ? [start.latitude, start.longitude] : NYC;

  return (
    <MapContainer center={center} zoom={14} className="map-container">
      <TileLayer
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
      />
      <RecenterOnStartChange latitude={start?.latitude} longitude={start?.longitude} />
      {start && <Marker position={[start.latitude, start.longitude]} />}
      {routeGeoJson && <GeoJSON key={JSON.stringify(routeGeoJson)} data={routeGeoJson} />}
    </MapContainer>
  );
}
