// src/config.ts — reads settings from .env (see .env.example).

import "dotenv/config";
import path from "node:path";

function optional(key: string): string | undefined {
  const value = process.env[key]?.trim();
  return value ? value : undefined;
}

function optionalInt(key: string, fallback: number): number {
  const raw = optional(key);
  if (raw === undefined) return fallback;
  const n = Number.parseInt(raw, 10);
  if (Number.isNaN(n) || n < 0) throw new Error(`${key} must be a non-negative integer`);
  return n;
}

const chesscomUsername = optional("CHESSCOM_USERNAME");
const chesscomUserAgent = optional("CHESSCOM_USER_AGENT");

if (chesscomUsername && !chesscomUserAgent) {
  throw new Error(
    "CHESSCOM_USER_AGENT is required when CHESSCOM_USERNAME is set " +
      "(Chess.com asks for a User-Agent that identifies you, e.g. 'MyApp/1.0 (contact: me@example.com)')."
  );
}

export const config = {
  outputDir: path.resolve(optional("OUTPUT_DIR") ?? "./output"),
  lichess: {
    username: optional("LICHESS_USERNAME"),
    token: optional("LICHESS_TOKEN"),
  },
  chesscom: {
    username: chesscomUsername,
    userAgent: chesscomUserAgent,
    delayMs: optionalInt("CHESSCOM_DELAY_MS", 500),
  },
} as const;