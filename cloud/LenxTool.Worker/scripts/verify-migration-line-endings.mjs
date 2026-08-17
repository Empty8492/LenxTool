import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const workerRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repositoryRoot = path.resolve(workerRoot, "..", "..");
const migrationsDirectory = path.join(workerRoot, "migrations");
const attributesPath = path.join(repositoryRoot, ".gitattributes");
const expectedRule = "cloud/LenxTool.Worker/migrations/*.sql text eol=lf";

if (!fs.existsSync(attributesPath)) {
  throw new Error("Missing .gitattributes; D1 migration line endings are not protected.");
}

const attributes = fs.readFileSync(attributesPath, "utf8").split(/\r?\n/u);
if (!attributes.includes(expectedRule)) {
  throw new Error(`Missing required Git attribute: ${expectedRule}`);
}

const migrationNames = fs.readdirSync(migrationsDirectory)
  .filter(name => name.endsWith(".sql"))
  .sort((left, right) => left.localeCompare(right, "en"));

if (migrationNames.length === 0) {
  throw new Error("No D1 migration files were found.");
}

const filesWithCarriageReturns = migrationNames.filter(name =>
  fs.readFileSync(path.join(migrationsDirectory, name)).includes(0x0d)
);

if (filesWithCarriageReturns.length > 0) {
  throw new Error(
    `D1 migrations must use LF only; found CR bytes in: ${filesWithCarriageReturns.join(", ")}`
  );
}

console.log(`Verified LF-only D1 migrations: ${migrationNames.length}`);
