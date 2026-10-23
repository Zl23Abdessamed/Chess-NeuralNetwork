// src/lichess.ts — downloads every game of a Lichess user as monthly PGN files.
//
// How it stays within Lichess's rules:
//   * ONE request at a time, ever. The whole history comes from a single
//     streaming call to /api/games/user/{username}.
//   * On HTTP 429 we wait a full minute before trying again (Lichess's request).
//   * Other failures retry with exponential backoff, resuming from the last
//     game received instead of starting over.
//
// Output: <outputDir>/lichess_<username>_<YYYY-MM>.pgn (month = game start, UTC).
// Re-running only re-downloads from the newest month already on disk.

import { once } from "node:events";
import { createWriteStream, type WriteStream } from "node:fs";
import { rename, rm } from "node:fs/promises";
import path from "node:path";
import { request } from "undici";
import {
  DownloadError,
  backoffMs,
  listMonthKeys,
  monthKey,
  monthStartMs,
  parseRetryAfterMs,
  sleep,
  toDownloadError,
} from "./utils.js";

const BASE_URL = "https://lichess.org";
const USER_AGENT = "chess-game-downloader/1.0";
const MAX_CONSECUTIVE_FAILURES = 5;
const RATE_LIMIT_WAIT_MS = 60_000;
const LOG_EVERY = 500;

export interface LichessOptions {
  username: string;
  token: string | undefined;
  outputDir: string;
  /** Ignore existing files and download everything again. */
  full: boolean;
  signal: AbortSignal;
}

/** The only fields we read from each NDJSON row. */
interface LichessRow {
  id: string;
  createdAt: number; // epoch ms
  pgn?: string;
}

// ---------------------------------------------------------------------------
// Writes games into one file per month. A month's file is written as
// `.pgn.tmp` and renamed to `.pgn` only when that month is complete, so a
// crash never leaves a truncated month that looks finished.
// ---------------------------------------------------------------------------

class MonthWriter {
  private key: string | null = null;
  private stream: WriteStream | null = null;
  private streamError: Error | null = null;
  private count = 0;

  constructor(
    private readonly dir: string,
    private readonly prefix: string
  ) {}

  private finalPath(key: string): string {
    return path.join(this.dir, `${this.prefix}${key}.pgn`);
  }

  private openMonth(key: string): void {
    this.key = key;
    this.count = 0;
    this.streamError = null;
    const stream = createWriteStream(`${this.finalPath(key)}.tmp`, { encoding: "utf8" });
    stream.on("error", (err) => {
      this.streamError = err;
    });
    this.stream = stream;
  }

  async write(key: string, pgn: string): Promise<void> {
    if (key !== this.key) {
      if (this.key !== null && key < this.key) {
        throw new DownloadError(`Games arrived out of order (${key} after ${this.key})`, { retryable: false });
      }
      await this.finishMonth();
      this.openMonth(key);
    }

    const stream = this.stream!;
    try {
      if (this.streamError) throw this.streamError;
      this.count++;
      // Respect backpressure so memory stays flat even for huge histories.
      if (!stream.write(`${pgn.trim()}\n\n`)) await once(stream, "drain");
    } catch (err) {
      throw new DownloadError(`Cannot write to disk: ${(err as Error).message}`, {
        retryable: false,
        cause: err,
      });
    }
  }

  /** Completes the month currently being written (if any). */
  async finishMonth(): Promise<void> {
    if (!this.stream || this.key === null) return;
    const { stream, key, count } = { stream: this.stream, key: this.key, count: this.count };
    this.stream = null;
    this.key = null;
    this.count = 0;

    await new Promise<void>((resolve, reject) => {
      stream.once("error", reject);
      stream.end(resolve);
    });
    await rename(`${this.finalPath(key)}.tmp`, this.finalPath(key));
    console.log(`[lichess] ${key}: ${count} games saved`);
  }

  /** Throws away the unfinished month (used after a failure or Ctrl+C). */
  async abandon(): Promise<void> {
    if (!this.stream || this.key === null) return;
    const stream = this.stream;
    const tmp = `${this.finalPath(this.key)}.tmp`;
    this.stream = null;
    this.key = null;
    await new Promise<void>((resolve) => {
      stream.once("close", () => resolve());
      stream.destroy();
    });
    await rm(tmp, { force: true });
  }
}

// ---------------------------------------------------------------------------
// One HTTP request: stream NDJSON, hand each row to `onRow`.
// ---------------------------------------------------------------------------

interface StreamArgs {
  username: string;
  token: string | undefined;
  since: number | undefined;
  signal: AbortSignal;
  onRow: (row: LichessRow) => Promise<void>;
}

async function streamGames({ username, token, since, signal, onRow }: StreamArgs): Promise<void> {
  const url = new URL(`${BASE_URL}/api/games/user/${encodeURIComponent(username)}`);
  url.searchParams.set("sort", "dateAsc"); // oldest first, so progress only moves forward
  url.searchParams.set("pgnInJson", "true"); // full PGN text inside each JSON row
  url.searchParams.set("clocks", "true"); // keep [%clk ...] comments
  url.searchParams.set("opening", "true");
  if (since !== undefined) url.searchParams.set("since", String(since));

  const res = await request(url, {
    method: "GET",
    headers: {
      accept: "application/x-ndjson",
      "user-agent": USER_AGENT,
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    headersTimeout: 30_000,
    bodyTimeout: 120_000,
    signal,
  });

  if (res.statusCode !== 200) {
    await res.body.text().catch(() => undefined);
    const code = res.statusCode;
    if (code === 429) {
      throw new DownloadError("Lichess rate limit hit (HTTP 429)", {
        retryable: true,
        statusCode: code,
        retryAfterMs: Math.max(RATE_LIMIT_WAIT_MS, parseRetryAfterMs(res.headers["retry-after"]) ?? 0),
      });
    }
    if (code === 401 || code === 403) {
      throw new DownloadError("Lichess rejected the token (check LICHESS_TOKEN in .env)", {
        retryable: false,
        statusCode: code,
      });
    }
    if (code === 404) {
      throw new DownloadError(`Lichess user "${username}" not found`, { retryable: false, statusCode: code });
    }
    throw new DownloadError(`Lichess responded HTTP ${code}`, {
      retryable: code >= 500,
      statusCode: code,
    });
  }

  const handleLine = async (line: string): Promise<void> => {
    const trimmed = line.trim();
    if (!trimmed) return; // Lichess sends empty keep-alive lines
    let row: LichessRow;
    try {
      row = JSON.parse(trimmed) as LichessRow;
    } catch (err) {
      throw new DownloadError("Lichess stream was cut mid-game", { retryable: true, cause: err });
    }
    if (typeof row.id !== "string" || typeof row.createdAt !== "number") return;
    await onRow(row);
  };

  const decoder = new TextDecoder();
  let buffer = "";
  for await (const chunk of res.body) {
    buffer += decoder.decode(chunk as Uint8Array, { stream: true });
    let newline: number;
    while ((newline = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, newline);
      buffer = buffer.slice(newline + 1);
      await handleLine(line);
    }
  }
  buffer += decoder.decode();
  await handleLine(buffer);
}

// ---------------------------------------------------------------------------
// Public entry point
// ---------------------------------------------------------------------------

/** Returns the number of games written during this run. */
export async function downloadLichess(opts: LichessOptions): Promise<number> {
  const { username, token, outputDir, full, signal } = opts;
  const prefix = `lichess_${username.toLowerCase()}_`;
  const writer = new MonthWriter(outputDir, prefix);

  // Incremental mode: restart from the first day of the newest month on disk.
  // Older months are already complete and are left untouched.
  let since: number | undefined;
  if (!full) {
    const newest = (await listMonthKeys(outputDir, prefix)).at(-1);
    if (newest) {
      since = monthStartMs(newest);
      console.log(`[lichess] Found existing files up to ${newest}; re-fetching from ${newest}-01.`);
    }
  }
  console.log(`[lichess] Downloading games for "${username}"...`);

  let total = 0;
  let lastCreatedAt = 0;
  let idsAtLast = new Set<string>(); // ids seen with createdAt === lastCreatedAt (resume de-duplication)
  let failures = 0;

  const onRow = async (row: LichessRow): Promise<void> => {
    if (row.createdAt === lastCreatedAt) {
      if (idsAtLast.has(row.id)) return; // already written before a retry
    } else {
      lastCreatedAt = row.createdAt;
      idsAtLast = new Set();
    }
    idsAtLast.add(row.id);

    if (!row.pgn) {
      console.warn(`[lichess] game ${row.id} has no PGN, skipped`);
      return;
    }
    await writer.write(monthKey(row.createdAt), row.pgn);
    total++;
    if (total % LOG_EVERY === 0) console.log(`[lichess] ${total} games so far...`);
  };

  try {
    while (true) {
      const totalBefore = total;
      try {
        await streamGames({ username, token, since, signal, onRow });
        break; // the stream ended normally: we have everything
      } catch (err) {
        const e = toDownloadError(err, "Lichess request failed", signal);
        if (!e.retryable) throw e;

        if (total > totalBefore) failures = 0; // we made progress, so this isn't a stuck loop
        failures++;
        if (failures > MAX_CONSECUTIVE_FAILURES) throw e;

        const wait = e.retryAfterMs ?? backoffMs(failures);
        console.log(
          `[lichess] ${e.message} - waiting ${Math.round(wait / 1000)}s, then resuming ` +
            `(retry ${failures}/${MAX_CONSECUTIVE_FAILURES})`
        );
        await sleep(wait, signal);
        if (lastCreatedAt > 0) since = lastCreatedAt; // resume where we stopped
      }
    }
    await writer.finishMonth();
  } catch (err) {
    await writer.abandon();
    throw err;
  }

  return total;
}