import { useEffect } from "react";
import { MapContainer, TileLayer, GeoJSON, Marker, CircleMarker, Tooltip, useMap } from "react-leaflet";

const NYC = [40.7128, -74.006];

const AMENITY_STYLE = {
  Restroom: { color: "#8e44ad", label: "Restroom" },
  WaterFountain: { color: "#16a085", label: "Water fountain" },
};

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

export default function RouteMap({ routeGeoJson, start, amenityStops }) {
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
      {amenityStops?.map((stop, i) => {
        const style = AMENITY_STYLE[stop.type] ?? { color: "#333", label: stop.type };
        return (
          <CircleMarker
            key={i}
            center={[stop.latitude, stop.longitude]}
            radius={9}
            pathOptions={{ color: style.color, fillColor: style.color, fillOpacity: 0.9, weight: 2 }}
          >
            <Tooltip permanent direction="top" offset={[0, -10]} className="amenity-tooltip">
              {style.label}
              {stop.name ? `: ${stop.name}` : ""}
            </Tooltip>
          </CircleMarker>
        );
      })}
    </MapContainer>
  );
}
