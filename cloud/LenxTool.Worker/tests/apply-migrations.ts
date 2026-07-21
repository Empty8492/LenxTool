import { applyD1Migrations } from "cloudflare:test";
import { env } from "cloudflare:workers";

// https://developers.cloudflare.com/workers/testing/vitest-integration/test-apis/#d1
const initialMigration = env.TEST_MIGRATIONS.at(0);
if (!initialMigration) throw new Error("At least one D1 migration is required for tests.");

await applyD1Migrations(env.DB, [initialMigration]);
await env.DB.prepare(
  "INSERT INTO auth_attempts(key_hash,bucket,attempts) VALUES('migration-v1-sentinel','1970-01-01T00:00Z',7)"
).run();
await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
