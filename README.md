# Chess-NeuralNetwork: Personal Chess Style Clone

A deep-learning chess pipeline built with C# (.NET 10), TorchSharp (libtorch), ASP.NET Core, and SolidJS.

Instead of trying to calculate the objectively best engine move, this model trains on your personal rapid games (from Lichess and Chess.com) using supervised imitation learning. It learns your favorite openings, strategic habits, aggressive piece pushes, and your endgame tendencies.

---

## Playing Modes

The application supports two distinct selection strategies backed by the same neural network:

* **Imitator Mode (`imitate`):**
  Pure neural network policy. The model plays with human intuition, mirroring your favorite openings and tactical choices with no engine interference.
* **Careful Imitator Mode (`careful`):**
  Human style filtered by tactical safety. The network selects its top candidate moves, requests Stockfish (via a persistent UCI process with `MultiPV` search) to score each candidate at a fixed depth, rejects any move that loses more than a configured centipawn threshold, and plays your preferred move among the survivors.

---

## Machine Learning and Architecture

* **Feature Vector (929 values):**
  * **781 Board Features:** 12 binary piece planes (12 x 64 = 768), 4 castling rights, 8 en-passant file flags, and 1 normalized halfmove clock.
  * **148 Candidate Move Features:** 64 source square flags, 64 destination square flags, 6 moving piece types, 6 captured piece types, 5 promotion choices, and 3 special move flags.
* **Perspective Mirroring:**
  Boards are encoded relative to the player to move. When Black is to move, ranks are vertically mirrored (`sq ^ 56`) and piece colors are swapped. Playing White and Black shares the exact same weights.
* **Model Architecture:**
  A small, fast Multi-Layer Perceptron (MLP) built in TorchSharp:
  `Linear(929 -> 256) -> ReLU -> Dropout(0.10) -> Linear(256 -> 128) -> ReLU -> Linear(128 -> 1)`
* **Loss Function:**
  Dynamic per-position Cross-Entropy. The network scores all legal candidate moves for the current position, applies Softmax strictly across those legal moves, and penalizes deviation from your actual choice. It is mathematically constrained from selecting illegal moves.
* **Optimization and Regularization:**
  Trained with AdamW, deterministic game-level 80/10/10 splits, and early stopping to prevent the model from memorizing specific games (overfitting).

---

## Repository Structure

```text
Chess-NeuralNetwork/
├── configs/
│   └── config.json            # Data filter, training hyperparameters, and careful mode settings
├── data/
│   ├── raw/                   # Raw exported PGN files
│   └── processed/             # Generated positions.jsonl and manifest.json
├── games/                     # TypeScript downloader for Chess.com & Lichess APIs
├── NN-SolidJS-Test/           # Interactive SolidJS frontend using solid-chessboard
├── src/
│   ├── ChessModel.Core/       # PGN replay, feature encoders, TorchSharp model, Stockfish UCI service
│   ├── ChessModel.Cli/        # CLI commands: build-dataset, train, evaluate, evaluate-careful
│   └── ChessModel.Api/        # ASP.NET Core Web API serving POST /api/v1/move
├── tests/
│   └── ChessModel.Tests/      # xUnit unit and integration test suite
└── tools/
    └── stockfish              # Local Stockfish engine binary
```

---

## Step-by-Step Guide: Train Your Own Clone

### Prerequisites

* .NET 10 SDK
* Node.js (v18+) and npm / pnpm
* A 64-bit Linux environment (Fedora, Ubuntu, Debian, Arch, or WSL2)

### Step 1: Download Your Games

The `games/` directory contains a TypeScript scraper that downloads your public game archives from Chess.com and Lichess.

1. Navigate to `games/`:

   ```bash
   cd games
   npm install
   ```

2. Create a `.env` file in `games/` with your usernames:

   ```env
   LICHESS_USERNAME=YourLichessUsername
   CHESSCOM_USERNAME=YourChesscomUsername
   ```

3. Build and run the scraper:

   ```bash
   npm run build
   npm run start
   ```

4. Copy the downloaded `.pgn` files into the training data folder:

   ```bash
   cd ..
   mkdir -p data/raw
   cp games/output/*.pgn data/raw/
   ```

### Step 2: Set Up Stockfish

Stockfish is used by Careful Mode to prevent your model from making tactical blunders.

1. Download the official Linux x86-64 binary from [stockfishchess.org](https://stockfishchess.org).

2. Place the executable into the `tools/` folder:

   ```bash
   mkdir -p tools
   cp /path/to/downloaded/stockfish tools/stockfish
   chmod +x tools/stockfish
   ```

(Alternatively, install it via your system package manager: `sudo apt install stockfish` or `sudo dnf install stockfish`.)

### Step 3: Configure `configs/config.json`

Open `configs/config.json` and adjust the settings to match your accounts:

```json
{
  "schemaVersion": 1,
  "data": {
    "playerNames": ["YourLichessUsername", "YourChesscomUsername"],
    "allowedTimeControls": ["600+0", "600+5", "900+10", "1800", "1800+0"],
    "splitSeed": 42,
    "split": { "train": 0.8, "validation": 0.1, "test": 0.1 }
  },
  "training": {
    "randomSeed": 42,
    "maxEpochs": 100,
    "batchSizePositions": 32,
    "learningRate": 0.001,
    "weightDecay": 0.0001,
    "dropout": 0.1,
    "earlyStoppingPatience": 10,
    "hiddenLayer1": 256,
    "hiddenLayer2": 128
  },
  "careful": {
    "stockfishPath": "./tools/stockfish",
    "depth": 12,
    "threads": 1,
    "hashMb": 64,
    "maxNeuralCandidates": 5,
    "maxCentipawnLoss": 150,
    "fallbackToStockfishBestMove": true,
    "analysisTimeoutMilliseconds": 3000
  }
}
```

* `playerNames`: Case-insensitive usernames to identify which turns were yours.
* `allowedTimeControls`: Keeps rapid games and automatically filters out bullet and blitz.
* `maxCentipawnLoss`: The blunder threshold. Stockfish vetoes any candidate move that drops more than 150 centipawns (1.5 pawns) compared to the best engine move.

### Step 4: Build the Dataset and Train the Model

#### 1. Process Raw PGNs

Filters games, removes clocks and evaluation comments, verifies move legality, and generates deterministic train/validation/test splits:

```bash
dotnet run --project src/ChessModel.Cli -- build-dataset
```

Inspect `data/processed/manifest.json` to confirm how many rapid games were accepted.

#### 2. Train the Neural Network

Trains the MLP candidate scorer, logs validation metrics per epoch, and saves the best model checkpoint to `models/v1/`:

```bash
dotnet run --project src/ChessModel.Cli -- train
```

#### 3. Evaluate the Model

Evaluate imitation accuracy on held-out test positions:

```bash
dotnet run --project src/ChessModel.Cli -- evaluate --split test
```

Run offline careful evaluation to test how effectively Stockfish filters blunders:

```bash
dotnet run --project src/ChessModel.Cli -- evaluate-careful --limit 100
```

#### 4. Run Unit and Integration Tests

Verify chess rules, feature dimensions, perspective symmetry, fake-engine selection, and API endpoints:

```bash
dotnet test
```

### Step 5: Start the ASP.NET Core API

Start the backend move prediction API:

```bash
dotnet run --project src/ChessModel.Api
```

The API will listen on `http://localhost:5246`. You can test it using `curl`:

```bash
curl -X POST http://localhost:5246/api/v1/move \
  -H "Content-Type: application/json" \
  -d '{"fen": "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1", "mode": "careful"}'
```

### Step 6: Launch the Web UI and Play

The `NN-SolidJS-Test/` directory contains a SolidJS web application powered by `solid-chessboard` and `chess.js`.

1. Open a new terminal and navigate to the web project:

   ```bash
   cd NN-SolidJS-Test
   npm install
   ```

2. Build and start the development server:

   ```bash
   npm run build
   npm run dev
   ```

3. Open your browser at `http://localhost:5173`.

4. Choose your playing side (White or Black), select your mode (Careful or Imitate), and play directly against your neural network clone.