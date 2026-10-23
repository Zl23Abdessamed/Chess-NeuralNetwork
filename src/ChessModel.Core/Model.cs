// TorchSharp network, save/load, legal-move predictor
using System.Text.Json;
using TorchSharp;
using static TorchSharp.torch;

namespace ChessModel.Core;

/// <summary>
/// Small TorchSharp MLP scoring legal candidates: 929 -> 256 -> 128 -> 1 logit.
/// </summary>
public sealed class ChessMoveScorer : nn.Module<Tensor, Tensor>
{
    private readonly nn.Module<Tensor, Tensor> _fc1;
    private readonly nn.Module<Tensor, Tensor> _drop;
    private readonly nn.Module<Tensor, Tensor> _fc2;
    private readonly nn.Module<Tensor, Tensor> _fc3;

    public ChessMoveScorer(
        int inputFeatures = FeatureSchema.TotalFeatures,
        int hidden1 = 256,
        int hidden2 = 128,
        double dropout = 0.10) : base(nameof(ChessMoveScorer))
    {
        _fc1 = nn.Linear(inputFeatures, hidden1);
        _drop = nn.Dropout(dropout);
        _fc2 = nn.Linear(hidden1, hidden2);
        _fc3 = nn.Linear(hidden2, 1);

        RegisterComponents();
    }

    public override Tensor forward(Tensor input)
    {
        var x = nn.functional.relu(_fc1.forward(input));
        x = _drop.forward(x);
        x = nn.functional.relu(_fc2.forward(x));
        return _fc3.forward(x);
    }
}

public sealed record ModelConfig(
    int SchemaVersion,
    int HiddenLayer1,
    int HiddenLayer2,
    double Dropout,
    int InputFeatures,
    int BoardFeatures,
    int MoveFeatures,
    string TorchSharpVersion,
    int ChosenEpoch,
    double ValidationLoss,
    double ValidationTop1,
    double ValidationTop5,
    int TrainingPositionsCount);

public static class ModelArtifacts
{
    public const string WeightsFileName = "model-weights.dat";
    public const string ConfigFileName = "model-config.json";

    public static void Save(string directory, ChessMoveScorer model, ModelConfig config)
    {
        Directory.CreateDirectory(directory);
        model.save(Path.Combine(directory, WeightsFileName));

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, ConfigFileName), json);
    }

    public static (ChessMoveScorer Model, ModelConfig Config) Load(string directory, Device? device = null)
    {
        var configPath = Path.Combine(directory, ConfigFileName);
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Model config not found: {configPath}");

        var config = JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(configPath))
            ?? throw new InvalidOperationException("Failed to deserialize model config.");

        if (config.SchemaVersion != FeatureSchema.Version || config.InputFeatures != FeatureSchema.TotalFeatures)
            throw new InvalidOperationException("Model schema or feature dimensions do not match the current build.");

        var model = new ChessMoveScorer(
            inputFeatures: config.InputFeatures,
            hidden1: config.HiddenLayer1,
            hidden2: config.HiddenLayer2,
            dropout: config.Dropout);

        var weightsPath = Path.Combine(directory, WeightsFileName);
        if (!File.Exists(weightsPath))
            throw new FileNotFoundException($"Model weights not found: {weightsPath}");

        model.load(weightsPath);
        if (device != null) model.to(device);
        model.eval();

        return (model, config);
    }
}

/// <summary>
/// Scores and ranks legal moves for a given position. Invariant: only legal moves are returned.
/// </summary>
public sealed class PolicyPredictor
{
    private readonly IChessRulesAdapter _rules;
    private readonly ChessMoveScorer _model;
    private readonly Device _device;

    public PolicyPredictor(IChessRulesAdapter rules, ChessMoveScorer model, Device? device = null)
    {
        _rules = rules;
        _model = model;
        _device = device ?? CPU;
        _model.eval();
    }

    public IReadOnlyList<(CandidateMove Move, float Probability, float Logit)> Predict(string fen)
    {
        var legalMoves = _rules.GetLegalMoves(fen);
        if (legalMoves.Count == 0) return Array.Empty<(CandidateMove, float, float)>();

        bool isWhite = _rules.IsWhiteToMove(fen);
        var board = BoardEncoder.Encode(fen);

        float[] batchData = new float[legalMoves.Count * FeatureSchema.TotalFeatures];
        for (int i = 0; i < legalMoves.Count; i++)
        {
            var moveVec = MoveEncoder.Encode(legalMoves[i], isWhite);
            BoardEncoder.Combine(board, moveVec, batchData.AsSpan(i * FeatureSchema.TotalFeatures, FeatureSchema.TotalFeatures));
        }

        using var _ = NewDisposeScope();
        using var noGrad = no_grad();

        using var input = from_array(batchData).reshape(legalMoves.Count, FeatureSchema.TotalFeatures).to(_device);
        using var logits = _model.forward(input).squeeze(-1);
        using var probs = nn.functional.softmax(logits, dim: 0);

        float[] logitsArr = logits.data<float>().ToArray();
        float[] probsArr = probs.data<float>().ToArray();

        var ranked = new (CandidateMove Move, float Probability, float Logit)[legalMoves.Count];
        for (int i = 0; i < legalMoves.Count; i++)
            ranked[i] = (legalMoves[i], probsArr[i], logitsArr[i]);

        Array.Sort(ranked, (a, b) => b.Probability.CompareTo(a.Probability));
        return ranked;
    }
}