// src/utils.ts — small helpers shared by both downloaders.

import { readdir, rename, writeFile } from "node:fs/promises";

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

export interface DownloadErrorOptions {
  /** True if trying again later might succeed (network blip, 429, 5xx). */
  retryable: boolean;
  /** How long the server asked us to wait, if known. */
  retryAfterMs?: number;
  statusCode?: number;
  cause?: unknown;
}

export class DownloadError extends Error {
  readonly retryable: boolean;
  readonly retryAfterMs: number | undefined;
  readonly statusCode: number | undefined;

  constructor(message: string, opts: DownloadErrorOptions) {
    super(message, { cause: opts.cause });
    this.name = "DownloadError";
    this.retryable = opts.retryable;
    this.retryAfterMs = opts.retryAfterMs;
    this.statusCode = opts.statusCode;
  }
}

/** Wraps any thrown value. Network-level failures are retryable; a user abort is not. */
export function toDownloadError(err: unknown, label: string, signal?: AbortSignal): DownloadError {
  if (err instanceof DownloadError) return err;
  if (signal?.aborted) return new DownloadError("Aborted by user", { retryable: false, cause: err });
  const message = err instanceof Error ? err.message : String(err);
  return new DownloadError(`${label}: ${message}`, { retryable: true, cause: err });
}

// ---------------------------------------------------------------------------
// Waiting / retry helpers
// ---------------------------------------------------------------------------

/** Sleep that rejects immediately if the abort signal fires (so Ctrl+C is instant). */
export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DownloadError("Aborted by user", { retryable: false }));
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      reject(new DownloadError("Aborted by user", { retryable: false }));
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/** Exponential backoff (2s, 4s, 8s, ... capped at 60s) plus a little jitter. */
export function backoffMs(attempt: number): number {
  return Math.min(60_000, 2_000 * 2 ** (attempt - 1)) + Math.floor(Math.random() * 500);
}

/** Parses a `Retry-After: <seconds>` header. */
export function parseRetryAfterMs(header: string | string[] | undefined): number | undefined {
  const raw = Array.isArray(header) ? header[0] : header;
  if (!raw) return undefined;
  const seconds = Number(raw);
  return Number.isFinite(seconds) && seconds >= 0 ? seconds * 1000 : undefined;
}

// ---------------------------------------------------------------------------
// Month helpers (everything is UTC, keys look like "2024-05")
// ---------------------------------------------------------------------------

export function monthKey(epochMs: number): string {
  const d = new Date(epochMs);
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}`;
}

export function monthStartMs(key: string): number {
  const [y, m] = key.split("-").map(Number);
  return Date.UTC(y!, m! - 1, 1);
}

/** A sortable integer for a month key, so months can be compared/subtracted. */
export function monthIndex(key: string): number {
  const [y, m] = key.split("-").map(Number);
  return y! * 12 + (m! - 1);
}

// ---------------------------------------------------------------------------
// Files
// ---------------------------------------------------------------------------

/** Writes to `<file>.tmp` and renames, so a crash never leaves a half-written .pgn behind. */
export async function writeFileAtomic(file: string, text: string): Promise<void> {
  const tmp = `${file}.tmp`;
  await writeFile(tmp, text, "utf8");
  await rename(tmp, file);
}

/** Lists the "YYYY-MM" keys of existing `<prefix>YYYY-MM.pgn` files, oldest first. */
export async function listMonthKeys(dir: string, prefix: string): Promise<string[]> {
  const names = await readdir(dir).catch(() => [] as string[]);
  const keys: string[] = [];
  for (const name of names) {
    if (!name.startsWith(prefix) || !name.endsWith(".pgn")) continue;
    const key = name.slice(prefix.length, -".pgn".length);
    if (/^\d{4}-\d{2}$/.test(key)) keys.push(key);
  }
  return keys.sort();
}