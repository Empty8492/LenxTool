import path from "node:path";
import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

export default defineConfig(async () => {
  // Cloudflare's documented Workers Vitest + D1 migration setup:
  // https://developers.cloudflare.com/workers/testing/vitest-integration/configuration/
  const migrations = await readD1Migrations(path.join(__dirname, "migrations"));

  return {
    plugins: [
      cloudflareTest({
        wrangler: { configPath: "./wrangler.toml" },
        miniflare: {
          bindings: {
            TOKEN_SECRET: "test-token-secret-with-at-least-32-bytes",
            BOOTSTRAP_TOKEN: "test-bootstrap-secret-with-at-least-32-bytes",
            GROQ_API_KEY: "",
            DEEPSEEK_API_KEY: "",
            TEST_MIGRATIONS: migrations
          }
        }
      })
    ],
    test: {
      setupFiles: ["./tests/apply-migrations.ts"]
    }
  };
});
