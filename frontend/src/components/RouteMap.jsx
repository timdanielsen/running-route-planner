import { MapContainer, TileLayer, GeoJSON, Marker } from "react-leaflet";

const NYC = [40.7128, -74.006];

export default function RouteMap({ routeGeoJson, start }) {
  const center = start ? [start.latitude, start.longitude] : NYC;

  return (
    <MapContainer center={center} zoom={14} className="map-container">
      <TileLayer
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
        url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
      />
      {start && <Marker position={[start.latitude, start.longitude]} />}
      {routeGeoJson && <GeoJSON key={JSON.stringify(routeGeoJson)} data={routeGeoJson} />}
    </MapContainer>
  );
}
