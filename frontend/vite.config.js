import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Proxies /api requests to the ASP.NET Core backend during dev, so the
// frontend can just call fetch("/api/routes") without worrying about CORS/ports.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: "http://localhost:5080",
        changeOrigin: true,
      },
    },
  },
});
