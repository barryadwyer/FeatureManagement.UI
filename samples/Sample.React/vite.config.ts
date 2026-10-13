import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Aspire injects the API address (WithReference) and the port to listen on.
const api =
  process.env["services__apiservice__https__0"] ??
  process.env["services__apiservice__http__0"] ??
  "http://localhost:5100";

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@featuremanagement/ui-react": path.resolve(__dirname, "../../src/FeatureManagement.UI.React/src/index.ts"),
    },
    dedupe: ["react", "react-dom", "@fluentui/react-components"],
  },
  server: {
    port: process.env.PORT ? Number(process.env.PORT) : 5173,
    proxy: {
      "/api": { target: api, changeOrigin: true, secure: false },
    },
  },
});
