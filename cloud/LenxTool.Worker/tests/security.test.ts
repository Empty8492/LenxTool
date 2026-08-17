import { describe, expect, it } from "vitest";
import {
  normalizeUsername,
  PASSWORD_PBKDF2_ITERATIONS
} from "../src/index";

describe("security boundary helpers", () => {
  it("normalizes visually equivalent usernames before uniqueness checks", () => {
    expect(normalizeUsername("  ＬＥＮＸ用户  ")).toBe("lenx用户");
  });

  it("keeps PBKDF2 within the Cloudflare Workers runtime maximum", () => {
    expect(PASSWORD_PBKDF2_ITERATIONS).toBe(100_000);
  });
});
