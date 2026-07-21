import { applyD1Migrations } from "cloudflare:test";
import { env } from "cloudflare:workers";

// https://developers.cloudflare.com/workers/testing/vitest-integration/test-apis/#d1
await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
