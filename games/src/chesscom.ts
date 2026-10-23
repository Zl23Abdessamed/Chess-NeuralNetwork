// src/chesscom.ts — downloads every game of a Chess.com user as monthly PGN files.
//
// How it stays within Chess.com's rules:
//   * Requests are strictly sequential (their API only objects to parallel
//     requests) with a pause between them (CHESSCOM_DELAY_MS).
//   * A descriptive User-Agent is sent on every request.
//   * HTTP 429 / 5xx / network errors retry with backoff (honouring Retry-After).
//
// Output: <outputDir>/chesscom_<username>_<YYYY-MM>.pgn
// Re-running skips months already on disk, except the current and previous
// month, which are always refreshed.

import { existsSync } from "node:fs";
import path from "node:path";
import { request } from "undici";
import {
  DownloadError,
  backoffMs,
  monthIndex,
  parseRetryAfterMs,
  sleep,
  toDownloadError,
  writeFileAtomic,
} from "./utils.js";

const BASE_URL = "https://api.chess.com/pub";
const MAX_ATTEMPTS = 5;
/** How many of the most recent months are always re-fetched (current + previous). */
const REFRESH_RECENT_MONTHS = 2;

export interface ChessComOptions {
  username: string;
  userAgent: string;
  outputDir: string;
  /** Pause between requests, in ms. */
  delayMs: number;
  /** Ignore existing files and download everything again. */
  full: boolean;
  signal: AbortSignal;
}

interface ArchivesResponse {
  archives?: string[];
}

interface MonthResponse {
  games?: Array<{ pgn?: string }>;
}

async function getJson<T>(url: string, userAgent: string, signal: AbortSignal): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try {
      const res = await request(url, {
        method: "GET",
        headers: { "user-agent": userAgent, accept: "application/json" },
        headersTimeout: 15_000,
        bodyTimeout: 60_000,
        signal,
      });

      if (res.statusCode >= 400) {
        await res.body.text().catch(() => undefined);
        throw new DownloadError(`Chess.com responded HTTP ${res.statusCode} for ${url}`, {
          retryable: res.statusCode === 429 || res.statusCode >= 500,
          statusCode: res.statusCode,
          retryAfterMs: parseRetryAfterMs(res.headers["retry-after"]),
        });
      }
      return (await res.body.json()) as T;
    } catch (err) {
      const e = toDownloadError(err, `Chess.com request failed (${url})`, signal);
      if (!e.retryable || attempt >= MAX_ATTEMPTS) throw e;

      const wait = e.retryAfterMs ?? backoffMs(attempt);
      console.log(
        `[chesscom] ${e.message} - retrying in ${Math.round(wait / 1000)}s (attempt ${attempt}/${MAX_ATTEMPTS})`
      );
      await sleep(wait, signal);
    }
  }
}

/** Returns the number of games written during this run. */
export async function downloadChessCom(opts: ChessComOptions): Promise<number> {
  const { username, userAgent, outputDir, delayMs, full, signal } = opts;
  const user = username.toLowerCase(); // Chess.com's API wants lowercase usernames
  const prefix = `chesscom_${user}_`;

  console.log(`[chesscom] Downloading games for "${username}"...`);

  let archives: string[];
  try {
    const data = await getJson<ArchivesResponse>(
      `${BASE_URL}/player/${encodeURIComponent(user)}/games/archives`,
      userAgent,
      signal
    );
    archives = data.archives ?? [];
  } catch (err) {
    if (err instanceof DownloadError && err.statusCode === 404) {
      throw new DownloadError(`Chess.com user "${username}" not found`, { retryable: false, statusCode: 404 });
    }
    throw err;
  }

  const now = new Date();
  const nowIndex = now.getUTCFullYear() * 12 + now.getUTCMonth();

  let games = 0;
  let fetched = 0;
  let skipped = 0;

  for (const url of archives) {
    const match = /\/(\d{4})\/(\d{2})\/?$/.exec(url);
    if (!match) {
      console.warn(`[chesscom] unexpected archive URL, skipped: ${url}`);
      continue;
    }
    const key = `${match[1]}-${match[2]}`;
    const file = path.join(outputDir, `${prefix}${key}.pgn`);

    const isRecent = monthIndex(key) > nowIndex - REFRESH_RECENT_MONTHS;
    if (!full && !isRecent && existsSync(file)) {
      skipped++;
      continue;
    }

    let data: MonthResponse;
    try {
      data = await getJson<MonthResponse>(url, userAgent, signal);
    } catch (err) {
      if (err instanceof DownloadError && err.statusCode === 404) {
        console.warn(`[chesscom] ${key}: archive not found, skipped`);
        continue;
      }
      throw err;
    }

    const pgns = (data.games ?? []).map((g) => g.pgn?.trim()).filter((p): p is string => !!p);
    if (pgns.length > 0) await writeFileAtomic(file, `${pgns.join("\n\n")}\n`);

    games += pgns.length;
    fetched++;
    console.log(`[chesscom] ${key}: ${pgns.length} games saved`);

    await sleep(delayMs, signal); // be polite between requests
  }

  console.log(`[chesscom] ${fetched} months fetched, ${skipped} already on disk.`);
  return games;
}