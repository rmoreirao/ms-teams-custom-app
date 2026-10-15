import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: { outDir: "../Teams.Host/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://localhost:5255", "/auth": "http://localhost:5255" } },
});
