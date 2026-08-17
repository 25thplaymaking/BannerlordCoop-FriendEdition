import { describe, expect, it } from "vitest";
import { testing } from "../src/index";
import { clientAddressHeaders, SlidingRateLimiter } from "../src/server";

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

  it("keys the report throttle on the connecting address, not the caller's clientId", () => {
    const edge = new Request("https://portal/reports", { headers: { "cf-connecting-ip": "203.0.113.9" } });
    expect(testing.rateLimitKey(edge)).toBe("origin:203.0.113.9");

    // A caller varying clientId must land in the same bucket.
    const spoofedBody = new Request("https://portal/reports", {
      headers: { "cf-connecting-ip": "203.0.113.9, 70.0.0.1" },
    });
    expect(testing.rateLimitKey(spoofedBody)).toBe("origin:203.0.113.9");

    const host = new Request("https://portal/reports", { headers: { "x-portal-client-ip": "127.0.0.1" } });
    expect(testing.rateLimitKey(host)).toBe("origin:127.0.0.1");

    // Unattributable origins share one bucket instead of getting a private one.
    expect(testing.rateLimitKey(new Request("https://portal/reports"))).toBe("origin:unattributed");
  });

  it("stamps the socket address over any supplied client-ip header", () => {
    const headers = clientAddressHeaders({
      headers: { "x-portal-client-ip": "1.2.3.4", "content-type": "application/json" },
      socket: { remoteAddress: "127.0.0.1" },
    } as never) as Record<string, string>;

    expect(headers["x-portal-client-ip"]).toBe("127.0.0.1");
    expect(headers["content-type"]).toBe("application/json");
  });

  it("keeps cf-connecting-ip only when the local tunnel is the peer", () => {
    // Behind cloudflared the edge has already overwritten it, and it is the only real client
    // address available — dropping it would collapse every user into one bucket.
    const tunnelled = clientAddressHeaders({
      headers: { "cf-connecting-ip": "203.0.113.9", authorization: "Bearer x" },
      socket: { remoteAddress: "127.0.0.1" },
    } as never) as Record<string, string>;
    expect(tunnelled["cf-connecting-ip"]).toBe("203.0.113.9");
    expect(tunnelled["authorization"]).toBe("Bearer x");

    // Reached directly it is just caller input, and honouring it would restore the bypass.
    const direct = clientAddressHeaders({
      headers: { "cf-connecting-ip": "203.0.113.9" },
      socket: { remoteAddress: "198.51.100.4" },
    } as never) as Record<string, string>;
    expect(direct["cf-connecting-ip"]).toBeUndefined();
    expect(direct["x-portal-client-ip"]).toBe("198.51.100.4");
  });

  it("throttles a single origin regardless of how many client ids it claims", async () => {
    const limiter = new SlidingRateLimiter();
    const results: boolean[] = [];
    for (let attempt = 0; attempt < 4; attempt++)
      results.push((await limiter.limit({ key: "origin:203.0.113.9" })).success);

    expect(results).toEqual([true, true, true, false]);
  });
});
