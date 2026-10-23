// Options, batching, loss, checkpoints, evaluation metrics
using System.Text;
using System.Text.Json;
using TorchSharp;
using static TorchSharp.torch;

namespace ChessModel.Core;

public sealed record TrainingOptions(
    int RandomSeed = 42,
    int MaxEpochs = 100,
    int BatchSizePositions = 32,
    double LearningRate = 0.001,
    double WeightDecay = 0.0001,
    double Dropout = 0.10,
    int EarlyStoppingPatience = 10,
    int HiddenLayer1 = 256,
    int HiddenLayer2 = 128);

public sealed class ModelTrainer
{
    private readonly IChessRulesAdapter _rules;
    private readonly Device _device;

    public ModelTrainer(IChessRulesAdapter rules, Device? device = null)
    {
        _rules = rules;
        _device = device ?? CPU;
    }

    public void Train(
        IReadOnlyList<PositionRecord> trainRecords,
        IReadOnlyList<PositionRecord> valRecords,
        TrainingOptions options,
        string outputDir)
    {
        manual_seed(options.RandomSeed);
        Directory.CreateDirectory(outputDir);

        var model = new ChessMoveScorer(
            hidden1: options.HiddenLayer1,
            hidden2: options.HiddenLayer2,
            dropout: options.Dropout).to(_device);

        var optimizer = optim.AdamW(model.parameters(), lr: options.LearningRate, weight_decay: options.WeightDecay);

        var metricsCsv = new StringBuilder();
        metricsCsv.AppendLine("epoch,train_loss,val_loss,val_top1,val_top5");

        double bestValLoss = double.MaxValue;
        int patienceCount = 0;
        int bestEpoch = 1;
        double bestTop1 = 0, bestTop5 = 0;

        var rng = new Random(options.RandomSeed);
        var shuffledTrain = trainRecords.ToList();

        for (int epoch = 1; epoch <= options.MaxEpochs; epoch++)
        {
            // 1. Train epoch
            model.train();
            shuffledTrain = shuffledTrain.OrderBy(_ => rng.Next()).ToList();
            double epochTrainLoss = RunTrainEpoch(model, optimizer, shuffledTrain, options.BatchSizePositions);

            // 2. Validate
            model.eval();
            var valMetrics = ModelEvaluator.EvaluatePositions(_rules, model, valRecords, _device);

            metricsCsv.AppendLine($"{epoch},{epochTrainLoss:F4},{valMetrics.Loss:F4},{valMetrics.Top1:F4},{valMetrics.Top5:F4}");

            if (valMetrics.Loss < bestValLoss)
            {
                bestValLoss = valMetrics.Loss;
                bestTop1 = valMetrics.Top1;
                bestTop5 = valMetrics.Top5;
                bestEpoch = epoch;
                patienceCount = 0;

                var config = new ModelConfig(
                    FeatureSchema.Version, options.HiddenLayer1, options.HiddenLayer2,
                    options.Dropout, FeatureSchema.TotalFeatures, FeatureSchema.BoardFeatures,
                    FeatureSchema.MoveFeatures, "0.107.0", bestEpoch, bestValLoss, bestTop1, bestTop5,
                    trainRecords.Count);

                ModelArtifacts.Save(outputDir, model, config);
            }
            else
            {
                patienceCount++;
                if (patienceCount >= options.EarlyStoppingPatience) break;
            }
        }

        File.WriteAllText(Path.Combine(outputDir, "training-metrics.csv"), metricsCsv.ToString());
    }

    private double RunTrainEpoch(
        ChessMoveScorer model,
        optim.Optimizer optimizer,
        IReadOnlyList<PositionRecord> data,
        int batchSize)
    {
        double totalLoss = 0;
        int batchCount = 0;

        for (int i = 0; i < data.Count; i += batchSize)
        {
            var chunk = data.Skip(i).Take(batchSize).ToList();
            var (features, offsets, counts, targets) = PrepareBatch(chunk);
            if (counts.Count == 0) continue;

            using (NewDisposeScope())
            {
                using var input = from_array(features).reshape(features.Length / FeatureSchema.TotalFeatures, FeatureSchema.TotalFeatures).to(_device);
                using var logits = model.forward(input).squeeze(-1);

                var losses = new List<Tensor>(counts.Count);
                for (int b = 0; b < counts.Count; b++)
                {
                    var posLogits = logits.narrow(0, offsets[b], counts[b]);
                    var logProbs = nn.functional.log_softmax(posLogits, dim: 0);
                    losses.Add(-logProbs[targets[b]]);
                }

                using var stacked = stack(losses);
                using var meanLoss = stacked.mean();

                optimizer.zero_grad();
                meanLoss.backward();
                optimizer.step();

                totalLoss += meanLoss.ToSingle();
                batchCount++;
            }
        }

        return batchCount > 0 ? totalLoss / batchCount : 0.0;
    }

    private (float[] Features, List<int> Offsets, List<int> Counts, List<int> Targets) PrepareBatch(
        IReadOnlyList<PositionRecord> records)
    {
        var offsets = new List<int>();
        var counts = new List<int>();
        var targets = new List<int>();
        var rowBuffer = new List<float[]>();

        int currentOffset = 0;
        foreach (var r in records)
        {
            var legal = _rules.GetLegalMoves(r.Fen);
            if (legal.Count == 0) continue;

            string wanted = r.TargetUci.Trim().ToLowerInvariant();
            int targetIdx = -1;
            for (int m = 0; m < legal.Count; m++)
            {
                if (legal[m].Uci == wanted) { targetIdx = m; break; }
            }
            if (targetIdx < 0) continue;

            bool isWhite = _rules.IsWhiteToMove(r.Fen);
            var board = BoardEncoder.Encode(r.Fen);

            offsets.Add(currentOffset);
            counts.Add(legal.Count);
            targets.Add(targetIdx);
            currentOffset += legal.Count;

            foreach (var m in legal)
            {
                var moveVec = MoveEncoder.Encode(m, isWhite);
                rowBuffer.Add(BoardEncoder.Combine(board, moveVec));
            }
        }

        float[] flat = new float[rowBuffer.Count * FeatureSchema.TotalFeatures];
        for (int i = 0; i < rowBuffer.Count; i++)
            Array.Copy(rowBuffer[i], 0, flat, i * FeatureSchema.TotalFeatures, FeatureSchema.TotalFeatures);

        return (flat, offsets, counts, targets);
    }
}

public sealed record EvaluationResult(
    string Split,
    int TotalPositions,
    double Loss,
    double Top1,
    double Top5,
    Dictionary<string, double> ByColorTop1,
    Dictionary<string, double> ByPhaseTop1);

public static class ModelEvaluator
{
    public static EvaluationResult EvaluatePositions(
        IChessRulesAdapter rules,
        ChessMoveScorer model,
        IReadOnlyList<PositionRecord> records,
        Device device,
        string splitName = "val")
    {
        var predictor = new PolicyPredictor(rules, model, device);

        double totalLoss = 0;
        int top1Count = 0, top5Count = 0, evaluated = 0;

        int whiteCount = 0, whiteTop1 = 0;
        int blackCount = 0, blackTop1 = 0;
        int openCount = 0, openTop1 = 0;
        int midCount = 0, midTop1 = 0;
        int endCount = 0, endTop1 = 0;

        foreach (var r in records)
        {
            var ranked = predictor.Predict(r.Fen);
            if (ranked.Count == 0) continue;

            string wanted = r.TargetUci.Trim().ToLowerInvariant();
            int targetRank = -1;
            float targetProb = 1e-12f;

            for (int i = 0; i < ranked.Count; i++)
            {
                if (ranked[i].Move.Uci == wanted)
                {
                    targetRank = i;
                    targetProb = Math.Max(ranked[i].Probability, 1e-12f);
                    break;
                }
            }
            if (targetRank < 0) continue;

            evaluated++;
            totalLoss += -Math.Log(targetProb);

            bool isTop1 = targetRank == 0;
            bool isTop5 = targetRank < 5;
            if (isTop1) top1Count++;
            if (isTop5) top5Count++;

            bool isWhite = rules.IsWhiteToMove(r.Fen);
            if (isWhite) { whiteCount++; if (isTop1) whiteTop1++; }
            else { blackCount++; if (isTop1) blackTop1++; }

            if (r.Ply < 20) { openCount++; if (isTop1) openTop1++; }
            else if (r.Ply < 60) { midCount++; if (isTop1) midTop1++; }
            else { endCount++; if (isTop1) endTop1++; }
        }

        return new EvaluationResult(
            splitName,
            evaluated,
            evaluated > 0 ? totalLoss / evaluated : 0.0,
            evaluated > 0 ? (double)top1Count / evaluated : 0.0,
            evaluated > 0 ? (double)top5Count / evaluated : 0.0,
            new()
            {
                ["white"] = whiteCount > 0 ? (double)whiteTop1 / whiteCount : 0.0,
                ["black"] = blackCount > 0 ? (double)blackTop1 / blackCount : 0.0
            },
            new()
            {
                ["opening"] = openCount > 0 ? (double)openTop1 / openCount : 0.0,
                ["middlegame"] = midCount > 0 ? (double)midTop1 / midCount : 0.0,
                ["endgame"] = endCount > 0 ? (double)endTop1 / endCount : 0.0
            });
    }

    public static void SaveEvaluation(EvaluationResult result, string filePath)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
    }
}