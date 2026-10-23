import { createSignal, onCleanup, onMount, For, Show } from "solid-js";
import { Motion } from "solid-motionone";
import {
  FiAlertTriangle,
  FiCheckCircle,
  FiChevronRight,
  FiClock,
  FiCpu,
  FiLoader,
  FiPlay,
  FiRefreshCw,
  FiRepeat,
  FiSettings,
  FiShield,
  FiStar,
  FiTarget,
} from "solid-icons/fi";
import { TbFillChessKnight } from "solid-icons/tb";
import { Chessboard } from "solid-chessboard";
import type { PieceDropHandlerArgs } from "solid-chessboard";
import { Chess } from "chess.js";

interface CandidateDto {
  move: string;
  probability: number;
  engineScoreCp: number | null;
}

interface MoveApiResponse {
  move: string;
  mode: "imitate" | "careful";
  fallback: string | null;
  candidates: CandidateDto[];
}

export default function App() {
  // chess.js is mutable, so keep one instance and mirror its position in a signal.
  const chess = new Chess();
  const [fen, setFen] = createSignal(chess.fen());
  const [playerColor, setPlayerColor] = createSignal<"white" | "black">("white");
  const [mode, setMode] = createSignal<"imitate" | "careful">("careful");
  const [apiUrl, setApiUrl] = createSignal("http://localhost:5246");
  const [isThinking, setIsThinking] = createSignal(false);
  const [statusMessage, setStatusMessage] = createSignal("Your move. Make it count.");
  const [candidates, setCandidates] = createSignal<CandidateDto[]>([]);
  const [lastMoveInfo, setLastMoveInfo] = createSignal<string | null>(null);
  const [moveHistory, setMoveHistory] = createSignal<string[]>([]);
  const [errorMessage, setErrorMessage] = createSignal<string | null>(null);
  const [reduceMotion, setReduceMotion] = createSignal(false);

  // A late response from an old game must never modify the newly reset board.
  let gameGeneration = 0;

  const isPlayerTurn = () => {
    const turn = chess.turn() === "w" ? "white" : "black";
    return turn === playerColor();
  };

  const updateGameStatus = () => {
    if (chess.isCheckmate()) {
      setStatusMessage(`Checkmate — ${chess.turn() === "w" ? "Black" : "White"} wins.`);
    } else if (chess.isDraw()) {
      setStatusMessage("Game drawn. A well-fought battle.");
    } else if (chess.inCheck()) {
      setStatusMessage(`Check — ${chess.turn() === "w" ? "White" : "Black"} to move.`);
    } else {
      setStatusMessage(isPlayerTurn() ? "Your move. Make it count." : "Abdessamed AI is preparing a move…");
    }
  };

  const requestAiMove = async (currentFen: string, generation = gameGeneration) => {
    if (generation !== gameGeneration || chess.isGameOver()) return;

    const baseUrl = apiUrl().trim().replace(/\/+$/, "");
    if (!baseUrl) {
      setErrorMessage("Enter your engine API URL in the connection panel.");
      setStatusMessage("The engine endpoint is missing.");
      return;
    }

    setErrorMessage(null);
    setIsThinking(true);
    setStatusMessage("Abdessamed AI is thinking…");

    try {
      const response = await fetch(`${baseUrl}/api/v1/move`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ fen: currentFen, mode: mode() }),
      });

      if (!response.ok) {
        throw new Error(`The engine returned HTTP ${response.status}.`);
      }

      const data: MoveApiResponse = await response.json();
      if (generation !== gameGeneration) return;
      if (!data.move || data.move.length < 4) {
        throw new Error("The engine response did not contain a valid move.");
      }

      const from = data.move.slice(0, 2);
      const to = data.move.slice(2, 4);
      const promotion = data.move.length > 4 ? data.move[4] : undefined;
      const result = chess.move({ from, to, promotion });

      if (!result) {
        throw new Error(`The engine returned an illegal move: ${data.move}.`);
      }

      setFen(chess.fen());
      setMoveHistory(chess.history());
      setCandidates(Array.isArray(data.candidates) ? data.candidates : []);
      setLastMoveInfo(
        data.fallback
          ? `Fallback used: ${data.fallback}`
          : `AI played ${result.san}`,
      );
    } catch (error) {
      if (generation !== gameGeneration) return;
      const message = error instanceof Error ? error.message : "An unknown error occurred.";
      console.error("AI move error:", error);
      setErrorMessage(message);
      setStatusMessage("The engine couldn't complete its move.");
    } finally {
      if (generation === gameGeneration) {
        setIsThinking(false);
        if (!errorMessage()) updateGameStatus();
      }
    }
  };

  const onPieceDrop = ({ sourceSquare, targetSquare }: PieceDropHandlerArgs): boolean => {
    if (!targetSquare || !isPlayerTurn() || isThinking() || chess.isGameOver()) {
      return false;
    }

    try {
      const move = chess.move({
        from: sourceSquare,
        to: targetSquare,
        promotion: "q",
      });

      if (!move) return false;

      setErrorMessage(null);
      setFen(chess.fen());
      setMoveHistory(chess.history());
      setLastMoveInfo(null);
      updateGameStatus();

      const generation = gameGeneration;
      window.setTimeout(() => void requestAiMove(chess.fen(), generation), 160);
      return true;
    } catch {
      return false;
    }
  };

  const resetGame = (color: "white" | "black" = playerColor()) => {
    const generation = ++gameGeneration;
    chess.reset();
    setFen(chess.fen());
    setPlayerColor(color);
    setCandidates([]);
    setLastMoveInfo(null);
    setMoveHistory([]);
    setErrorMessage(null);
    setIsThinking(false);
    setStatusMessage(color === "black" ? "The AI will open as White." : "Your move. Make it count.");

    if (color === "black") {
      window.setTimeout(() => {
        if (generation === gameGeneration) void requestAiMove(chess.fen(), generation);
      }, 180);
    }
  };

  const retryAiMove = () => {
    if (!isThinking() && !isPlayerTurn() && !chess.isGameOver()) {
      void requestAiMove(chess.fen(), gameGeneration);
    }
  };

  const movePairs = () => {
    const history = moveHistory();
    return Array.from({ length: Math.ceil(history.length / 2) }, (_, index) => ({
      number: index + 1,
      white: history[index * 2],
      black: history[index * 2 + 1],
    }));
  };

  const motionTransition = (delay = 0) => ({
    duration: reduceMotion() ? 0.01 : 0.48,
    easing: "ease-out" as const,
    delay: reduceMotion() ? 0 : delay,
  });

  onMount(() => {
    const media = window.matchMedia("(prefers-reduced-motion: reduce)");
    setReduceMotion(media.matches);
    const updateMotionPreference = (event: MediaQueryListEvent) => setReduceMotion(event.matches);
    media.addEventListener?.("change", updateMotionPreference);
    onCleanup(() => media.removeEventListener?.("change", updateMotionPreference));
  });

  return (
    <div class="chess-studio">
      <div class="studio-grain" aria-hidden="true" />

      <div class="studio-container">
        <Motion.header
          class="topbar"
          initial={{ opacity: 0, y: -12 }}
          animate={{ opacity: 1, y: 0 }}
          transition={motionTransition(0)}
        >
          

          <div class="topbar-actions">
            <button
              type="button"
              class="button button-quiet"
              onClick={() => resetGame(playerColor() === "white" ? "black" : "white")}
              title="Start a new game with the opposite color"
            >
              <FiRepeat size={17} />
              <span>Play as {playerColor() === "white" ? "Black" : "White"}</span>
            </button>
            <button type="button" class="button button-primary" onClick={() => resetGame()}>
              <FiRefreshCw size={17} />
              <span>New game</span>
            </button>
          </div>
        </Motion.header>

        <Motion.section
          id="top"
          class="hero"
          initial={{ opacity: 0, y: 18 }}
          animate={{ opacity: 1, y: 0 }}
          transition={motionTransition(0.06)}
        >
          <div class="hero-copy">
            <div class="eyebrow"><span class="eyebrow-dot" /> YOUR OWN NEURAL OPPONENT</div>
            <h1>Good games begin<br /><em>with a bold move.</em></h1>
            <p>Play against the personality you trained. Make your move, then see what your model considered next.</p>
          </div>
        </Motion.section>

        <main class="workspace">
          <Motion.section
            class="board-panel panel"
            initial={{ opacity: 0, y: 22 }}
            animate={{ opacity: 1, y: 0 }}
            transition={motionTransition(0.12)}
            aria-label="Chessboard"
          >
            <div class="panel-heading board-heading">
              <div class="opponent-profile">
                <div class="opponent-avatar"><FiCpu size={22} /></div>
                <div>
                  <div class="micro-label">YOUR OPPONENT</div>
                  <h2>Abdessamed <span>AI</span></h2>
                </div>
              </div>
              <div class={`thinking-pill ${isThinking() ? "is-thinking" : ""}`}>
                <span class="thinking-dot" />
                {isThinking() ? "Thinking" : chess.isGameOver() ? "Game complete" : "Ready to play"}
              </div>
            </div>

            <div class={`game-status ${errorMessage() ? "status-error" : ""}`} role="status" aria-live="polite">
              <span class="status-symbol">
                <Show when={errorMessage()} fallback={isThinking() ? <FiLoader class="spin-icon" size={18} /> : <FiCheckCircle size={18} />}>
                  <FiAlertTriangle size={18} />
                </Show>
              </span>
              <span class="status-copy">{errorMessage() || statusMessage()}</span>
              <Show when={errorMessage() && !isThinking() && !isPlayerTurn()}>
                <button class="retry-button" type="button" onClick={retryAiMove}>Try again <FiChevronRight size={15} /></button>
              </Show>
            </div>

            <div class="board-wrap">
              <Chessboard
                options={{
                  position: fen(),
                  boardOrientation: playerColor(),
                  onPieceDrop,
                }}
              />
            </div>

            <div class="board-footer">
              <div class="board-meta">
                <span class="meta-icon"><FiTarget size={16} /></span>
                <span>Playing as <strong>{playerColor()}</strong></span>
              </div>
              <div class="board-meta">
                <span class="turn-marker" />
                <span>Turn <strong>{chess.turn() === "w" ? "White" : "Black"}</strong></span>
              </div>
              <Show when={chess.inCheck() && !chess.isGameOver()}>
                <span class="check-tag">CHECK</span>
              </Show>
            </div>

            <div class="board-divider" />
            <div class="mode-row">
              <div class="mode-description">
                <span class="mode-icon"><FiShield size={18} /></span>
                <div>
                  <strong>Playing style</strong>
                  <span>Choose how your model approaches the board.</span>
                </div>
              </div>
              <div class="mode-switch" role="group" aria-label="AI playing style">
                <button
                  type="button"
                  class={mode() === "careful" ? "mode-option active" : "mode-option"}
                  aria-pressed={mode() === "careful"}
                  onClick={() => setMode("careful")}
                >
                  <FiShield size={15} /> Careful
                </button>
                <button
                  type="button"
                  class={mode() === "imitate" ? "mode-option active" : "mode-option"}
                  aria-pressed={mode() === "imitate"}
                  onClick={() => setMode("imitate")}
                >
                  <FiStar size={15} /> Imitate
                </button>
              </div>
            </div>
            <div class="mode-footnote">
              <Show when={mode() === "careful"} fallback={<>Plays in your model's natural style, without the Stockfish veto.</>}>
                <>Uses the Stockfish veto to filter out risky moves.</>
              </Show>
            </div>
          </Motion.section>

          <aside class="inspector-column" aria-label="Game analysis and controls">
            <Motion.section
              class="analysis-panel panel"
              initial={{ opacity: 0, y: 22 }}
              animate={{ opacity: 1, y: 0 }}
              transition={motionTransition(0.2)}
            >
              <div class="section-heading">
                <div>
                  <div class="micro-label">MODEL READOUT</div>
                  <h2>Move candidates<span class="heading-period">.</span></h2>
                </div>
                <span class="section-icon"><FiCpu size={20} /></span>
              </div>

              <Show when={lastMoveInfo()}>
                <div class="last-move-note">
                  <FiCheckCircle size={16} />
                  <span>{lastMoveInfo()}</span>
                </div>
              </Show>

              <Show
                when={candidates().length > 0}
                fallback={
                  <div class="analysis-empty">
                    <div class="empty-orbit"><FiStar size={24} /></div>
                    <strong>Your model's thinking, visualized.</strong>
                    <p>Play a move to reveal the candidate moves and confidence scores returned by your model.</p>
                    <div class="empty-hint"><FiPlay size={15} /> Make your first move on the board</div>
                  </div>
                }
              >
                <div class="candidate-list">
                  <For each={candidates().slice(0, 6)}>
                    {(candidate, index) => {
                      const percentage = () => Math.min(100, Math.max(0, candidate.probability * 100));
                      return (
                        <Motion.div
                          class={`candidate-row ${index() === 0 ? "candidate-best" : ""}`}
                          initial={{ opacity: 0, y: 8 }}
                          animate={{ opacity: 1, y: 0 }}
                          transition={motionTransition(index() * 0.045)}
                        >
                          <div class="candidate-topline">
                            <div class="candidate-move">
                              <span class="candidate-rank">{String(index() + 1).padStart(2, "0")}</span>
                              <strong>{candidate.move}</strong>
                              <Show when={index() === 0}><span class="best-label">TOP PICK</span></Show>
                            </div>
                            <div class="candidate-metrics">
                              <Show when={candidate.engineScoreCp !== null}>
                                <span class={`cp-score ${(candidate.engineScoreCp ?? 0) >= 0 ? "score-positive" : "score-negative"}`}>
                                  {(candidate.engineScoreCp ?? 0) > 0 ? "+" : ""}{candidate.engineScoreCp} cp
                                </span>
                              </Show>
                              <strong class="candidate-percentage">{percentage().toFixed(1)}<small>%</small></strong>
                            </div>
                          </div>
                          <div class="probability-track" aria-label={`${percentage().toFixed(1)} percent probability`}>
                            <Motion.div
                              class="probability-fill"
                              initial={{ scaleX: 0 }}
                              animate={{ scaleX: percentage() / 100 }}
                              transition={motionTransition(0.08 + index() * 0.04)}
                            />
                          </div>
                        </Motion.div>
                      );
                    }}
                  </For>
                </div>
              </Show>

              <div class="analysis-footnote">
                <span class="footnote-dot" /> Probabilities are supplied by your model.
              </div>
            </Motion.section>

            <Motion.section
              class="history-panel panel"
              initial={{ opacity: 0, y: 22 }}
              animate={{ opacity: 1, y: 0 }}
              transition={motionTransition(0.27)}
            >
              <div class="section-heading history-heading">
                <div>
                  <div class="micro-label">THE GAME SO FAR</div>
                  <h2>Move history<span class="heading-period">.</span></h2>
                </div>
                <span class="history-count">{moveHistory().length} <small>plies</small></span>
              </div>

              <Show
                when={moveHistory().length > 0}
                fallback={
                  <div class="history-empty">
                    <FiClock size={20} />
                    <span>The opening is yours to write.</span>
                  </div>
                }
              >
                <div class="move-table" aria-label="Moves played">
                  <div class="move-table-head"><span>MOVE</span><span>WHITE</span><span>BLACK</span></div>
                  <div class="move-table-body">
                    <For each={movePairs()}>
                      {(pair) => (
                        <div class={`move-table-row ${pair.number === Math.ceil(moveHistory().length / 2) ? "latest-move" : ""}`}>
                          <span class="move-number">{String(pair.number).padStart(2, "0")}</span>
                          <span>{pair.white}</span>
                          <span>{pair.black || <span class="pending-move">—</span>}</span>
                        </div>
                      )}
                    </For>
                  </div>
                </div>
              </Show>
            </Motion.section>
          </aside>
        </main>
      </div>
    </div>
  );
}
