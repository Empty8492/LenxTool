import { describe, expect, it } from "vitest";
import { normalizeUsername } from "../src/index";

describe("security boundary helpers", () => {
  it("normalizes visually equivalent usernames before uniqueness checks", () => {
    expect(normalizeUsername("  ＬＥＮＸ用户  ")).toBe("lenx用户");
  });
});
