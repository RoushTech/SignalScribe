import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

// CORS is never needed: dev proxies to the API; production is same-origin (API serves the build).
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    proxy: {
      "/api": "http://localhost:5020",
      "/swagger": "http://localhost:5020",
      "/hubs": { target: "http://localhost:5020", ws: true },
    },
  },
});
