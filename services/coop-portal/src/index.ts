import { timingSafeEqual } from "node:crypto";

const MAX_BODY_BYTES = 64 * 1024;
const MAX_TITLE = 120;
const MAX_DESCRIPTION = 4_000;
const MAX_LOGS = 40_000;

type ReportInput = {
  clientId: string;
  kind: "bug" | "feature";
  title: string;
  description: string;
  launcherVersion: string;
  gameVersion: string;
  logs: string;
};

type StatsInput = {
  updatedAt: string;
  gameVersion: string;
  campaignDay: number;
  onlinePlayers: number;
  players: unknown[];
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const url = new URL(request.url);
      if (request.method === "GET" && url.pathname === "/health")
        return json({ status: "ok" });
      if (request.method === "GET" && url.pathname === "/stats")
        return getStats(env);
      if (request.method === "POST" && url.pathname === "/stats/publish")
        return publishStats(request, env);
      if (request.method === "POST" && url.pathname === "/reports")
        return createReport(request, env);
      return json({ message: "Not found." }, 404);
    } catch (error) {
      if (error instanceof PayloadTooLargeError)
        return json({ message: "Request body is too large." }, 413);
      console.error(JSON.stringify({ event: "request_failed", error: String(error) }));
      return json({ message: "The campaign portal could not complete this request." }, 500);
    }
  },
} satisfies ExportedHandler<Env>;

async function getStats(env: Env): Promise<Response> {
  const snapshot = await env.PORTAL_KV.get("campaign-stats");
  if (snapshot === null) return json({ message: "No campaign snapshot has been published yet." }, 404);
  return new Response(snapshot, {
    headers: {
      "content-type": "application/json; charset=utf-8",
      "cache-control": "public, max-age=30",
    },
  });
}

async function publishStats(request: Request, env: Env): Promise<Response> {
  const supplied = request.headers.get("authorization")?.replace(/^Bearer\s+/i, "") ?? "";
  if (!(await secretEquals(supplied, env.PUBLISH_TOKEN)))
    return json({ message: "Unauthorized." }, 401);

  const value = await readJson(request);
  if (!isStats(value)) return json({ message: "Invalid campaign snapshot." }, 400);
  await env.PORTAL_KV.put("campaign-stats", JSON.stringify(value));
  console.log(JSON.stringify({ event: "stats_published", players: value.players.length }));
  return json({ message: "Campaign snapshot published." });
}

async function createReport(request: Request, env: Env): Promise<Response> {
  const value = await readJson(request);
  const report = normalizeReport(value);
  if (report === null) return json({ message: "Title, description, or report metadata is invalid." }, 400);

  const limited = await env.REPORT_RATE_LIMITER.limit({ key: report.clientId });
  if (!limited.success)
    return json({ message: "Too many reports were submitted. Wait a minute and try again." }, 429);

  const duplicateKey = `report:${await sha256(`${report.kind}\n${report.title}\n${report.description}`)}`;
  const existing = await env.PORTAL_KV.get(duplicateKey);
  if (existing !== null)
    return json({ message: "That report was already submitted recently.", issueUrl: existing });

  const prefix = report.kind === "bug" ? "[Launcher Bug]" : "[Launcher Request]";
  const body = buildIssueBody(report);
  const githubResponse = await fetch(`https://api.github.com/repos/${env.GITHUB_REPOSITORY}/issues`, {
    method: "POST",
    headers: {
      accept: "application/vnd.github+json",
      authorization: `Bearer ${env.GITHUB_TOKEN}`,
      "content-type": "application/json",
      "user-agent": "calradia-coop-portal",
      "x-github-api-version": "2022-11-28",
    },
    body: JSON.stringify({ title: `${prefix} ${report.title}`, body }),
  });
  if (!githubResponse.ok) {
    console.error(JSON.stringify({ event: "github_issue_failed", status: githubResponse.status }));
    return json({ message: "GitHub did not accept the issue. The maintainers have been notified." }, 502);
  }

  const issue = await githubResponse.json<{ html_url?: string }>();
  if (typeof issue.html_url !== "string") return json({ message: "GitHub returned an invalid issue response." }, 502);
  await env.PORTAL_KV.put(duplicateKey, issue.html_url, { expirationTtl: 60 * 60 * 24 });
  console.log(JSON.stringify({ event: "github_issue_created", kind: report.kind }));
  return json({ message: "GitHub issue created successfully.", issueUrl: issue.html_url }, 201);
}

async function readJson(request: Request): Promise<unknown> {
  const declared = Number(request.headers.get("content-length") ?? "0");
  if (declared > MAX_BODY_BYTES) throw new PayloadTooLargeError();
  if (request.body === null) return null;

  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let length = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    length += value.byteLength;
    if (length > MAX_BODY_BYTES) {
      await reader.cancel();
      throw new PayloadTooLargeError();
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  try { return JSON.parse(new TextDecoder().decode(bytes)); }
  catch { return null; }
}

function normalizeReport(value: unknown): ReportInput | null {
  if (!isObject(value)) return null;
  const kind = value.kind === "bug" || value.kind === "feature" ? value.kind : null;
  const clientId = clean(value.clientId, 64);
  const title = clean(value.title, MAX_TITLE);
  const description = clean(value.description, MAX_DESCRIPTION);
  const launcherVersion = clean(value.launcherVersion, 40);
  const gameVersion = clean(value.gameVersion, 40);
  const logs = redact(clean(value.logs, MAX_LOGS));
  if (kind === null || clientId.length < 16 || title.length < 5 || description.length < 10) return null;
  return { clientId, kind, title, description, launcherVersion, gameVersion, logs };
}

function isStats(value: unknown): value is StatsInput {
  if (!isObject(value) || typeof value.updatedAt !== "string" || typeof value.gameVersion !== "string" ||
      typeof value.campaignDay !== "number" || typeof value.onlinePlayers !== "number" ||
      !Number.isInteger(value.campaignDay) || !Number.isInteger(value.onlinePlayers) || !Array.isArray(value.players))
    return false;
  return value.players.length <= 100 && value.campaignDay >= 0 && value.onlinePlayers >= 0;
}

function buildIssueBody(report: ReportInput): string {
  const diagnostic = report.logs.length === 0
    ? "_No logs attached._"
    : `\`\`\`text\n${report.logs.replaceAll("```", "` ` `")}\n\`\`\``;
  return `${report.description}\n\n` +
    `### Launcher diagnostics\n\n` +
    `- Report type: ${report.kind}\n- Launcher: ${report.launcherVersion || "unknown"}\n` +
    `- Bannerlord: ${report.gameVersion || "unknown"}\n- Submitted: ${new Date().toISOString()}\n\n` +
    `<details><summary>Redacted log excerpt</summary>\n\n${diagnostic}\n\n</details>\n\n` +
    `_Created automatically by the Calradia Co-op launcher._`;
}

function redact(value: string): string {
  return value
    .replace(/(password|passwd|token|secret|authorization)(\s*[:=]\s*|\s+)([^\s,;]+)/gi, "$1=[redacted]")
    .replace(/[A-Z]:\\Users\\[^\\\s]+/gi, "%USERPROFILE%");
}

function clean(value: unknown, maximum: number): string {
  return typeof value === "string" ? value.trim().slice(0, maximum) : "";
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

async function secretEquals(left: string, right: string): Promise<boolean> {
  const [leftHash, rightHash] = await Promise.all([
    crypto.subtle.digest("SHA-256", new TextEncoder().encode(left)),
    crypto.subtle.digest("SHA-256", new TextEncoder().encode(right)),
  ]);
  return timingSafeEqual(new Uint8Array(leftHash), new Uint8Array(rightHash));
}

function json(value: unknown, status = 200): Response {
  return Response.json(value, {
    status,
    headers: { "cache-control": "no-store", "x-content-type-options": "nosniff" },
  });
}

class PayloadTooLargeError extends Error {}

export const testing = { normalizeReport, isStats, buildIssueBody, redact };
