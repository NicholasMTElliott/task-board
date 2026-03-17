import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    testTimeout: 60_000, // container startup can take time
    hookTimeout: 120_000,
  },
});
