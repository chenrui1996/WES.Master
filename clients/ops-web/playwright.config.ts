import { defineConfig } from "@playwright/test";
if (!process.env.WES_E2E_URL) throw new Error("Run through the controlled IdentityHttpTests browser fixture; no production target is permitted.");
export default defineConfig({
  testDir: "./tests", workers: 1, retries: 0, timeout: 60000, reporter: "list",
  outputDir: "test-results",
  use: { baseURL: process.env.WES_E2E_URL, ignoreHTTPSErrors: true, testIdAttribute: "data-test", trace: "off", video: "off", screenshot: "off" },
});
