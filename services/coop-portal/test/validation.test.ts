import { describe, expect, it } from "vitest";
import { testing } from "../src/index";

describe("portal validation", () => {
  it("redacts credentials and user profile paths", () => {
    expect(testing.redact("password=hunter2 C:\\Users\\Bryce\\game token: abc"))
      .toBe("password=[redacted] %USERPROFILE%\\game token=[redacted]");
  });

  it("rejects underspecified reports", () => {
    expect(testing.normalizeReport({ kind: "bug", clientId: "short", title: "no", description: "no" }))
      .toBeNull();
  });

  it("accepts a bounded launcher report", () => {
    expect(testing.normalizeReport({
      kind: "feature",
      clientId: "4dfbe4a2-cda9-4aaf-bc80-c89fa21828bb",
      title: "Show party speed",
      description: "Please show the current map party speed in the roster.",
      launcherVersion: "1.2.3",
      gameVersion: "1.4.8",
      logs: "",
    })?.kind).toBe("feature");
  });

  it("accepts a bounded schema-two lord snapshot", () => {
    expect(testing.isStats({
      schemaVersion: 2,
      updatedAt: "2026-08-15T22:00:00Z",
      gameVersion: "1.4.8",
      campaignDay: 42,
      onlinePlayers: 1,
      lords: [{ id: "lord_1", name: "Aldric", controller: "ai" }],
    })).toBe(true);
  });

  it("rejects an unbounded lord snapshot", () => {
    expect(testing.isStats({
      updatedAt: "2026-08-15T22:00:00Z",
      gameVersion: "1.4.8",
      campaignDay: 42,
      onlinePlayers: 1,
      lords: Array.from({ length: 2_001 }),
    })).toBe(false);
  });
});
