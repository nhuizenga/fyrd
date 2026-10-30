import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: { outDir: "../runtime/src/Fyrd.Server/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://127.0.0.1:4870" } },
  test: { environment: "jsdom", globals: true, setupFiles: "src/test/setup.ts" },
});
