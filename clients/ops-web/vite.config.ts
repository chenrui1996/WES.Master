import { defineConfig } from "vitest/config";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      "/api": { target: process.env.WES_API_ORIGIN ?? "https://localhost:7243", secure: false },
    },
  },
  test: { environment: "jsdom", include: ["src/**/*.spec.ts"], restoreMocks: true },
});
