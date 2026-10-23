// src/index.ts — entry point.
//   node dist/index.js           download new/updated months only
//   node dist/index.js --full    re-download everything

import { mkdir } from "node:fs/promises";
import { config } from "./config.js";
import { downloadChessCom } from "./chesscom.js";
import { downloadLichess } from "./lichess.js";

function describe(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

async function main(): Promise<number> {
  const full = process.argv.includes("--full");
  const { outputDir, lichess, chesscom } = config;

  if (!lichess.username && !(chesscom.username && chesscom.userAgent)) {
    console.error("Nothing to do: set LICHESS_USERNAME and/or CHESSCOM_USERNAME in your .env file.");
    return 1;
  }

  // Ctrl+C stops cleanly: finished months stay on disk, the unfinished one is discarded.
  const controller = new AbortController();
  process.once("SIGINT", () => {
    console.log("\nStopping... finished months are kept; run again to continue.");
    controller.abort();
  });

  await mkdir(outputDir, { recursive: true });
  console.log(`Output folder: ${outputDir}`);
  if (full) console.log("--full given: re-downloading everything.");

  let failed = false;

  if (lichess.username) {
    try {
      const count = await downloadLichess({
        username: lichess.username,
        token: lichess.token,
        outputDir,
        full,
        signal: controller.signal,
      });
      console.log(`[lichess] Done: ${count} games written this run.`);
    } catch (err) {
      failed = true;
      console.error(`[lichess] FAILED: ${describe(err)}`);
    }
  }

  if (controller.signal.aborted) return 130;

  if (chesscom.username && chesscom.userAgent) {
    try {
      const count = await downloadChessCom({
        username: chesscom.username,
        userAgent: chesscom.userAgent,
        outputDir,
        delayMs: chesscom.delayMs,
        full,
        signal: controller.signal,
      });
      console.log(`[chesscom] Done: ${count} games written this run.`);
    } catch (err) {
      failed = true;
      console.error(`[chesscom] FAILED: ${describe(err)}`);
    }
  }

  if (controller.signal.aborted) return 130;
  return failed ? 1 : 0;
}

main()
  .then((code) => {
    process.exitCode = code;
  })
  .catch((err) => {
    console.error(describe(err));
    process.exitCode = 1;
  });