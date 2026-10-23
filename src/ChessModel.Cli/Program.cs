using System.Text.Json;
using ChessModel.Core;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();
string configPath = GetArg(args, "--config") ?? "configs/config.json";

if (!File.Exists(configPath))
{
    Console.WriteLine($"Error: Config file not found at {configPath}");
    return 1;
}

var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var root = configDoc.RootElement;
var rules = new ChessLibraryAdapter();

switch (command)
{
    case "build-dataset":
    {
        string pgnDir = GetArg(args, "--pgn-dir") ?? "data/raw";
        string outputDir = GetArg(args, "--output-dir") ?? "data/processed";

        Console.WriteLine($"Parsing PGNs from {pgnDir}...");
        var dataCfg = root.GetProperty("data");
        var playerNames = dataCfg.GetProperty("playerNames").EnumerateArray().Select(x => x.GetString()!).ToList();
        var timeControls = dataCfg.GetProperty("allowedTimeControls").EnumerateArray().Select(x => x.GetString()!).ToList();
        int splitSeed = dataCfg.GetProperty("splitSeed").GetInt32();

        var builder = new PgnDatasetBuilder(rules, new DatasetConfig(playerNames, timeControls, splitSeed));
        var manifest = builder.Build(pgnDir, outputDir);

        Console.WriteLine("\n--- Dataset Build Complete ---");
        Console.WriteLine($"Total Input Games:   {manifest.TotalGamesInput}");
        Console.WriteLine($"Accepted Games:      {manifest.AcceptedGames}");
        Console.WriteLine($"Skipped Games:       {manifest.SkippedGames}");
        Console.WriteLine($"Total Positions:     {manifest.TotalPositions}");
        Console.WriteLine("\nPositions by Split:");
        foreach (var (k, v) in manifest.PositionsPerSplit) Console.WriteLine($"  {k}: {v}");
        Console.WriteLine("\nSkip Reasons Summary:");
        foreach (var (k, v) in manifest.SkipReasons) Console.WriteLine($"  {k}: {v}");
        return 0;
    }

    case "train":
    {
        string dataDir = GetArg(args, "--data") ?? "data/processed";
        string outputDir = GetArg(args, "--output") ?? "models/v1";

        string positionsPath = Path.Combine(dataDir, "positions.jsonl");
        Console.WriteLine("Loading dataset splits...");
        var trainRecords = DatasetReader.ReadJsonl(positionsPath, "train").ToList();
        var valRecords = DatasetReader.ReadJsonl(positionsPath, "validation").ToList();

        if (trainRecords.Count == 0)
        {
            Console.WriteLine("Error: No training records found. Did you run build-dataset first?");
            return 1;
        }

        Console.WriteLine($"Train samples: {trainRecords.Count}, Validation samples: {valRecords.Count}");

        var trnJson = root.GetProperty("training");
        var opts = new TrainingOptions(
            RandomSeed: trnJson.GetProperty("randomSeed").GetInt32(),
            MaxEpochs: trnJson.GetProperty("maxEpochs").GetInt32(),
            BatchSizePositions: trnJson.GetProperty("batchSizePositions").GetInt32(),
            LearningRate: trnJson.GetProperty("learningRate").GetDouble(),
            WeightDecay: trnJson.GetProperty("weightDecay").GetDouble(),
            Dropout: trnJson.GetProperty("dropout").GetDouble(),
            EarlyStoppingPatience: trnJson.GetProperty("earlyStoppingPatience").GetInt32(),
            HiddenLayer1: trnJson.GetProperty("hiddenLayer1").GetInt32(),
            HiddenLayer2: trnJson.GetProperty("hiddenLayer2").GetInt32());

        var trainer = new ModelTrainer(rules);
        Console.WriteLine("Starting training...");
        trainer.Train(trainRecords, valRecords, opts, outputDir);
        Console.WriteLine($"Training completed! Best checkpoint and metrics saved to {outputDir}");
        return 0;
    }

    case "evaluate":
    {
        string modelDir = GetArg(args, "--model") ?? "models/v1";
        string dataDir = GetArg(args, "--data") ?? "data/processed";
        string split = GetArg(args, "--split") ?? "test";

        var (model, _) = ModelArtifacts.Load(modelDir);
        var records = DatasetReader.ReadJsonl(Path.Combine(dataDir, "positions.jsonl"), split).ToList();

        Console.WriteLine($"Evaluating {records.Count} positions from '{split}' split...");
        var eval = ModelEvaluator.EvaluatePositions(rules, model, records, TorchSharp.torch.CPU, split);

        Console.WriteLine($"\n--- Evaluation Metrics ({split}) ---");
        Console.WriteLine($"Cross-Entropy Loss: {eval.Loss:F4}");
        Console.WriteLine($"Top-1 Accuracy:     {eval.Top1 * 100:F2}%");
        Console.WriteLine($"Top-5 Accuracy:     {eval.Top5 * 100:F2}%");

        string evalPath = Path.Combine(modelDir, "evaluation.json");
        ModelEvaluator.SaveEvaluation(eval, evalPath);
        Console.WriteLine($"Saved full evaluation to {evalPath}");
        return 0;
    }

    case "evaluate-careful":
    {
        string modelDir = GetArg(args, "--model") ?? "models/v1";
        string dataDir = GetArg(args, "--data") ?? "data/processed";
        string split = GetArg(args, "--split") ?? "test";
        int limit = int.TryParse(GetArg(args, "--limit"), out int l) ? l : 200; // default evaluate 200 positions

        var (model, _) = ModelArtifacts.Load(modelDir);
        var predictor = new PolicyPredictor(rules, model);

        var carefulJson = root.GetProperty("careful");
        var carefulConfig = new CarefulConfig(
            StockfishPath: carefulJson.GetProperty("stockfishPath").GetString() ?? "stockfish",
            Depth: carefulJson.GetProperty("depth").GetInt32(),
            Threads: carefulJson.GetProperty("threads").GetInt32(),
            HashMb: carefulJson.GetProperty("hashMb").GetInt32(),
            MaxNeuralCandidates: carefulJson.GetProperty("maxNeuralCandidates").GetInt32(),
            MaxCentipawnLoss: carefulJson.GetProperty("maxCentipawnLoss").GetInt32(),
            FallbackToStockfishBestMove: carefulJson.GetProperty("fallbackToStockfishBestMove").GetBoolean(),
            AnalysisTimeoutMilliseconds: carefulJson.GetProperty("analysisTimeoutMilliseconds").GetInt32());

        using var engine = new StockfishService(carefulConfig);
        var records = DatasetReader.ReadJsonl(Path.Combine(dataDir, "positions.jsonl"), split).Take(limit).ToList();

        Console.WriteLine($"Running Careful Evaluation over {records.Count} positions with Stockfish (depth={carefulConfig.Depth})...");

        int changedMoves = 0;
        int evaluated = 0;
        double totalImitateCpLoss = 0;
        double totalCarefulCpLoss = 0;
        int blundersSaved = 0; // where imitate loss > threshold but careful was within threshold

        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var neuralRanked = predictor.Predict(r.Fen);
            if (neuralRanked.Count == 0) continue;

            string imitateMove = neuralRanked[0].Move.Uci;

            try
            {
                var (carefulMove, _, scores) = await CarefulMoveSelector.SelectAsync(
                    r.Fen, neuralRanked, engine, carefulConfig);

                int maxScore = scores.Count > 0 ? scores.Values.Max() : 0;
                int imitateScore = scores.GetValueOrDefault(imitateMove, maxScore - carefulConfig.MaxCentipawnLoss);
                int carefulScore = scores.GetValueOrDefault(carefulMove.Uci, maxScore);

                int imitateLoss = Math.Max(0, maxScore - imitateScore);
                int carefulLoss = Math.Max(0, maxScore - carefulScore);

                totalImitateCpLoss += imitateLoss;
                totalCarefulCpLoss += carefulLoss;

                if (!string.Equals(imitateMove, carefulMove.Uci, StringComparison.OrdinalIgnoreCase))
                {
                    changedMoves++;
                    if (imitateLoss > carefulConfig.MaxCentipawnLoss && carefulLoss <= carefulConfig.MaxCentipawnLoss)
                        blundersSaved++;
                }

                evaluated++;
                if ((i + 1) % 25 == 0 || i + 1 == records.Count)
                    Console.WriteLine($"Analyzed {i + 1}/{records.Count} positions...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Position {i + 1} analysis skipped: {ex.Message}");
            }
        }

        double avgImitateLoss = evaluated > 0 ? totalImitateCpLoss / evaluated : 0;
        double avgCarefulLoss = evaluated > 0 ? totalCarefulCpLoss / evaluated : 0;

        var report = new
        {
            PositionsEvaluated = evaluated,
            AverageImitateCpLoss = avgImitateLoss,
            AverageCarefulCpLoss = avgCarefulLoss,
            FilterVetoPercentage = evaluated > 0 ? (double)changedMoves / evaluated * 100 : 0,
            BlundersSaved = blundersSaved
        };

        string outJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(modelDir, "careful-evaluation.json"), outJson);

        Console.WriteLine("\n--- Careful Evaluation Report ---");
        Console.WriteLine($"Positions Evaluated:       {evaluated}");
        Console.WriteLine($"Imitate Avg Centipawn Loss: {avgImitateLoss:F1} cp");
        Console.WriteLine($"Careful Avg Centipawn Loss: {avgCarefulLoss:F1} cp");
        Console.WriteLine($"Stockfish Veto Rate:       {report.FilterVetoPercentage:F1}% of moves");
        Console.WriteLine($"Blunders Filtered:         {blundersSaved}");
        return 0;
    }

    default:
        PrintUsage();
        return 1;
}

static string? GetArg(string[] args, string flag)
{
    int idx = Array.IndexOf(args, flag);
    return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project src/ChessModel.Cli -- build-dataset [--pgn-dir data/raw] [--output-dir data/processed]");
    Console.WriteLine("  dotnet run --project src/ChessModel.Cli -- train [--data data/processed] [--output models/v1]");
    Console.WriteLine("  dotnet run --project src/ChessModel.Cli -- evaluate [--model models/v1] [--data data/processed] [--split test]");
    Console.WriteLine("  dotnet run --project src/ChessModel.Cli -- evaluate-careful [--model models/v1] [--data data/processed] [--limit 200]");
}