import { createServer, type IncomingMessage } from "node:http";
import { createHmac, timingSafeEqual } from "node:crypto";
import { createWriteStream, existsSync, mkdirSync, readFileSync, realpathSync, renameSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { Transform } from "node:stream";
import { pipeline } from "node:stream/promises";
import { pathToFileURL } from "node:url";
import portal from "./index.ts";

type StoredValue = { value: string; expiresAt?: number };

export class FileKv {
  private readonly path: string;
  private values: Record<string, StoredValue>;

  constructor(path: string) {
    this.path = path;
    mkdirSync(dirname(path), { recursive: true });
    this.values = existsSync(path)
      ? JSON.parse(readFileSync(path, "utf8")) as Record<string, StoredValue>
      : {};
  }

  async get(key: string): Promise<string | null> {
    const stored = this.values[key];
    if (stored === undefined) return null;
    if (stored.expiresAt !== undefined && stored.expiresAt <= Date.now()) {
      delete this.values[key];
      this.persist();
      return null;
    }
    return stored.value;
  }

  async put(key: string, value: string, options?: { expirationTtl?: number }): Promise<void> {
    this.values[key] = {
      value,
      expiresAt: options?.expirationTtl === undefined
        ? undefined
        : Date.now() + options.expirationTtl * 1_000,
    };
    this.persist();
  }

  private persist(): void {
    const temporary = `${this.path}.tmp`;
    writeFileSync(temporary, JSON.stringify(this.values), { mode: 0o600 });
    renameSync(temporary, this.path);
  }
}

export class SlidingRateLimiter {
  private readonly requests = new Map<string, number[]>();

  async limit(input: { key: string }): Promise<{ success: boolean }> {
    const now = Date.now();
    const recent = (this.requests.get(input.key) ?? []).filter(time => time > now - 60_000);
    if (recent.length >= 3) {
      this.requests.set(input.key, recent);
      return { success: false };
    }
    recent.push(now);
    this.requests.set(input.key, recent);
    return { success: true };
  }
}

async function readBody(request: IncomingMessage, maximumBytes: number): Promise<Buffer | undefined> {
  if (request.method === "GET" || request.method === "HEAD") return undefined;
  const chunks: Buffer[] = [];
  let length = 0;
  for await (const chunk of request) {
    const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    length += bytes.length;
    if (length > maximumBytes) throw new Error("payload_too_large");
    chunks.push(bytes);
  }
  return Buffer.concat(chunks);
}

function sendJson(response: import("node:http").ServerResponse, status: number, value: unknown): void {
  response.statusCode = status;
  response.setHeader("content-type", "application/json; charset=utf-8");
  response.setHeader("cache-control", "no-store");
  response.setHeader("x-content-type-options", "nosniff");
  response.end(JSON.stringify(value));
}

function hasValidUploadToken(reportId: string, supplied: string, secret: string): boolean {
  const [expirationText, signature] = supplied.split(".");
  const expiration = Number(expirationText);
  if (!Number.isSafeInteger(expiration) || expiration <= Date.now() || !/^[0-9a-f]{64}$/i.test(signature ?? ""))
    return false;
  const expected = createHmac("sha256", secret).update(`${reportId}.${expiration}`).digest("hex");
  return timingSafeEqual(Buffer.from(signature!, "hex"), Buffer.from(expected, "hex"));
}

async function handleReportAsset(
  request: IncomingMessage,
  response: import("node:http").ServerResponse,
  attachmentRoot: string,
  publishToken: string,
): Promise<boolean> {
  const pathname = new URL(request.url ?? "/", "http://localhost").pathname;
  const match = /^\/report-assets\/([0-9a-f]{8}-[0-9a-f-]{27})$/i.exec(pathname);
  if (match === null) return false;
  if (request.method !== "POST") {
    sendJson(response, 405, { message: "Method not allowed." });
    return true;
  }

  const reportId = match[1];
  const token = request.headers["x-report-token"];
  const suppliedToken = typeof token === "string" ? token : "";
  if (!hasValidUploadToken(reportId, suppliedToken, publishToken)) {
    sendJson(response, 401, { message: "Upload authorization is invalid or expired." });
    return true;
  }

  const contentType = request.headers["content-type"];
  if (typeof contentType !== "string" || !contentType.toLowerCase().startsWith("application/zip")) {
    sendJson(response, 415, { message: "A ZIP package is required." });
    return true;
  }

  const maximumBytes = 120 * 1024 * 1024;
  const declaredLength = Number(request.headers["content-length"] ?? "0");
  if (declaredLength > maximumBytes) {
    sendJson(response, 413, { message: "The report package is too large." });
    return true;
  }

  const reportDirectory = resolve(attachmentRoot, reportId);
  mkdirSync(reportDirectory, { recursive: true, mode: 0o700 });
  const destination = resolve(reportDirectory, `bundle-${Date.now()}.zip`);
  const temporary = `${destination}.partial`;
  let received = 0;
  const limit = new Transform({
    transform(chunk: Buffer, _encoding, callback) {
      received += chunk.length;
      callback(received > maximumBytes ? new Error("payload_too_large") : null, chunk);
    },
  });

  try {
    await pipeline(request, limit, createWriteStream(temporary, { flags: "wx", mode: 0o600 }));
    renameSync(temporary, destination);
    sendJson(response, 201, { message: "Diagnostic package received." });
  } catch (error) {
    if (existsSync(temporary)) unlinkSync(temporary);
    const tooLarge = error instanceof Error && error.message === "payload_too_large";
    sendJson(response, tooLarge ? 413 : 500, {
      message: tooLarge ? "The report package is too large." : "The diagnostic package could not be saved.",
    });
  }
  return true;
}

export function startServer(): void {
  const githubToken = process.env.GITHUB_TOKEN ?? "";
  const publishToken = process.env.PUBLISH_TOKEN ?? "";
  if (githubToken.length === 0 || publishToken.length === 0)
    throw new Error("GITHUB_TOKEN and PUBLISH_TOKEN must be configured.");

  const port = Number(process.env.PORTAL_PORT ?? "4211");
  const host = process.env.PORTAL_HOST ?? "127.0.0.1";
  const dataPath = resolve(process.env.PORTAL_DATA_PATH ?? "./data/portal-kv.json");
  const attachmentPath = process.env.REPORT_ATTACHMENT_PATH ?? resolve(dirname(dataPath), "report-attachments");
  const env = {
    GITHUB_REPOSITORY: process.env.GITHUB_REPOSITORY ?? "25thplaymaking/BannerlordCoop-FriendEdition",
    GITHUB_TOKEN: githubToken,
    PUBLISH_TOKEN: publishToken,
    PORTAL_KV: new FileKv(dataPath),
    REPORT_RATE_LIMITER: new SlidingRateLimiter(),
  } as unknown as Env;

  const server = createServer(async (incoming, outgoing) => {
    try {
      if (await handleReportAsset(incoming, outgoing, attachmentPath, publishToken)) return;
      const protocol = incoming.headers["x-forwarded-proto"] ?? "http";
      const authority = incoming.headers.host ?? `${host}:${port}`;
      const maximumBytes = (incoming.url ?? "").startsWith("/stats/publish")
        ? 1024 * 1024
        : 64 * 1024;
      const body = await readBody(incoming, maximumBytes);
      const request = new Request(`${protocol}://${authority}${incoming.url ?? "/"}`, {
        method: incoming.method,
        headers: incoming.headers as HeadersInit,
        body: body as BodyInit | undefined,
      });
      const response = await portal.fetch(request, env);
      outgoing.statusCode = response.status;
      response.headers.forEach((value, name) => outgoing.setHeader(name, value));
      outgoing.end(Buffer.from(await response.arrayBuffer()));
    } catch (error) {
      const tooLarge = error instanceof Error && error.message === "payload_too_large";
      outgoing.statusCode = tooLarge ? 413 : 500;
      outgoing.setHeader("content-type", "application/json; charset=utf-8");
      outgoing.end(JSON.stringify({ message: tooLarge ? "Request body is too large." : "Portal request failed." }));
      if (!tooLarge) console.error(JSON.stringify({ event: "server_request_failed", error: String(error) }));
    }
  });

  server.listen(port, host, () => console.log(JSON.stringify({ event: "portal_listening", host, port })));
  const stop = () => server.close(() => process.exit(0));
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(realpathSync(resolve(process.argv[1]))).href)
  startServer();
