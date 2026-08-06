# Running Route Planner

Generates running routes (loop or out-and-back) from a start point and target distance,
using OpenRouteService's foot-walking directions API on top of OpenStreetMap data.

## Structure

```
running-route-planner/
  backend/RunningRoutes.Api/   ASP.NET Core (.NET 8) Web API
  frontend/                    React + Vite + Leaflet
```

## How route generation works

- **Loop**: sends the start point to ORS's `options.round_trip` directions parameter,
  which finds a real, walkable circuit close to the requested length.
- **Out-and-back**: picks a point roughly half the target distance away along a bearing
  (random unless you pass one), then asks ORS for a route through that point and back
  to the start.

Both come back from the backend as GeoJSON, which the frontend drops straight into a
Leaflet `<GeoJSON>` layer.

## 1. Get an OpenRouteService API key

1. Sign up free at https://openrouteservice.org/dev/#/signup
2. Create a token — the free tier gives 2,500 requests/day, which is plenty for local dev.
3. Round-trip routes are capped at 100 km by ORS, so distant/ultra requests will need a
   different provider or a self-hosted ORS/GraphHopper instance later.

## 2. Run the backend

```bash
cd backend/RunningRoutes.Api
dotnet user-secrets init
dotnet user-secrets set "OpenRouteService:ApiKey" "YOUR_ORS_KEY_HERE"
dotnet run
```

This starts the API at `http://localhost:5080`. Swagger UI is available at
`http://localhost:5080/swagger` in Development.

(You can also set the key via the `OpenRouteService__ApiKey` environment variable instead
of user-secrets — useful for CI/containers. Don't commit a real key into `appsettings.json`.)

## 3. Run the frontend

```bash
cd frontend
npm install
npm run dev
```

Opens at `http://localhost:5173`. Its dev server proxies `/api/*` to the backend on port
5080 (see `vite.config.js`), so there's no CORS setup needed beyond what's already in
`Program.cs`.

## 4. Try it

Enter a latitude/longitude (or click "Use my location"), a distance in miles, pick Loop or
Out & back, and hit Generate. The route draws on the map with distance/time stats in the
sidebar.

## Where to go from here

- **Address search instead of raw lat/lng** — ORS has a free geocoding endpoint
  (`/geocode/search`); add a text input that calls it and feeds the result into the
  existing form.
- **Elevation** — ORS directions responses can include elevation per point
  (`"elevation": true` in the request body) if you want hill stats.
- **Re-roll button** — loops accept a `seed`; regenerating with a new random seed but the
  same distance gives the user a different loop without changing any other input.
- **Persisting favorite routes** — add a database (even SQLite to start) and a
  `SavedRoutes` table/endpoint once the core generation flow feels right.
- **Self-hosted routing** — if you outgrow ORS's free tier or the 100 km round-trip cap,
  both ORS and GraphHopper are open source and can be run against a local OpenStreetMap
  extract for your region, removing per-call limits entirely.
