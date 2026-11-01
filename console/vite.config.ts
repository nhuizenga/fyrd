import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import svgr from "vite-plugin-svgr";

export default defineConfig({
  // Anvil2 icons are SVGR components: `import X from "@servicetitan/anvil2/assets/icons/.../x.svg?react"`.
  plugins: [react(), svgr()],
  build: { outDir: "../runtime/src/Fyrd.Server/wwwroot", emptyOutDir: true },
  server: { proxy: { "/api": "http://127.0.0.1:4870" } },
  test: {
    environment: "jsdom", globals: true, setupFiles: "src/test/setup.ts", include: ["src/**/*.test.{ts,tsx}"],
    server: { deps: { inline: ["@servicetitan/anvil2"] } },
    css: false,
  },
});
