// Stockfish UCI service, careful selector, MoveService
using System.Diagnostics;

namespace ChessModel.Core;

// =====================================================================================
// 1. Interfaces, records, and configuration
// =====================================================================================

public sealed record CarefulConfig(
    string StockfishPath = "stockfish",
    int Depth = 12,
    int Threads = 1,
    int HashMb = 64,
    int MaxNeuralCandidates = 5,
    int MaxCentipawnLoss = 150,
    bool FallbackToStockfishBestMove = true,
    int AnalysisTimeoutMilliseconds = 3000);

public interface IEngineAnalyzer : IDisposable
{
    Task<string?> GetBestMoveAsync(string fen, int depth, int timeoutMs, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> ScoreMovesAsync(
        string fen, IReadOnlyList<string> uciMoves, int depth, int timeoutMs, CancellationToken ct = default);
}

// =====================================================================================
// 2. StockfishService (Persistent UCI Process)
// =====================================================================================

public sealed class StockfishService : IEngineAnalyzer
{
    private readonly CarefulConfig _config;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;

    public StockfishService(CarefulConfig config) => _config = config;

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return;

        KillProcess();

        var psi = new ProcessStartInfo
        {
            FileName = _config.StockfishPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {_config.StockfishPath}");
        _input = _process.StandardInput;
        _output = _process.StandardOutput;

        await SendCommandAsync("uci", ct);
        await ReadUntilAsync(line => line == "uciok", ct);

        await SendCommandAsync($"setoption name Threads value {_config.Threads}", ct);
        await SendCommandAsync($"setoption name Hash value {_config.HashMb}", ct);
        await SendCommandAsync("isready", ct);
        await ReadUntilAsync(line => line == "readyok", ct);
    }

    public async Task<string?> GetBestMoveAsync(string fen, int depth, int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        await _lock.WaitAsync(cts.Token);
        try
        {
            await EnsureStartedAsync(cts.Token);
            await SendCommandAsync($"position fen {fen}", cts.Token);
            await SendCommandAsync($"go depth {depth}", cts.Token);

            string? bestMove = null;
            await ReadUntilAsync(line =>
            {
                if (!line.StartsWith("bestmove ")) return false;
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 1 && tokens[1] != "(none)") bestMove = tokens[1];
                return true;
            }, cts.Token);

            return bestMove;
        }
        catch
        {
            KillProcess();
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, int>> ScoreMovesAsync(
        string fen, IReadOnlyList<string> uciMoves, int depth, int timeoutMs, CancellationToken ct = default)
    {
        if (uciMoves.Count == 0) return new Dictionary<string, int>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        await _lock.WaitAsync(cts.Token);
        try
        {
            await EnsureStartedAsync(cts.Token);

            await SendCommandAsync($"setoption name MultiPV value {uciMoves.Count}", cts.Token);
            await SendCommandAsync("isready", cts.Token);
            await ReadUntilAsync(line => line == "readyok", cts.Token);

            await SendCommandAsync($"position fen {fen}", cts.Token);
            var searchCmd = $"go depth {depth} searchmoves {string.Join(' ', uciMoves)}";
            await SendCommandAsync(searchCmd, cts.Token);

            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            await ReadUntilAsync(line =>
            {
                if (line.StartsWith("info ") && line.Contains(" score ") && line.Contains(" pv "))
                    ParseInfoLine(line, scores);
                return line.StartsWith("bestmove ");
            }, cts.Token);

            return scores;
        }
        catch
        {
            KillProcess();
            throw;
        }
        finally { _lock.Release(); }
    }

    private static void ParseInfoLine(string line, Dictionary<string, int> scores)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int scoreCp = 0;
        string? move = null;

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "score" && i + 2 < tokens.Length)
            {
                if (tokens[i + 1] == "cp" && int.TryParse(tokens[i + 2], out int cp))
                    scoreCp = cp;
                else if (tokens[i + 1] == "mate" && int.TryParse(tokens[i + 2], out int mate))
                    scoreCp = mate > 0 ? (30000 - mate) : (-30000 - mate);
            }
            if (tokens[i] == "pv" && i + 1 < tokens.Length)
            {
                move = tokens[i + 1].ToLowerInvariant();
                break;
            }
        }

        if (move != null) scores[move] = scoreCp;
    }

    private async Task SendCommandAsync(string cmd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _input!.WriteLineAsync(cmd.AsMemory(), ct);
        await _input.FlushAsync(ct);
    }

    private async Task ReadUntilAsync(Func<string, bool> predicate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await _output!.ReadLineAsync(ct);
            if (line == null || predicate(line)) return;
        }
    }

    private void KillProcess()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); }
        catch { /* ignored */ }
        finally
        {
            _process?.Dispose();
            _process = null;
            _input = null;
            _output = null;
        }
    }

    public void Dispose()
    {
        KillProcess();
        _lock.Dispose();
    }
}

// =====================================================================================
// 3. CarefulMoveSelector
// =====================================================================================

public static class CarefulMoveSelector
{
    public static async Task<(CandidateMove Move, string? Fallback, IReadOnlyDictionary<string, int> Scores)> SelectAsync(
        string fen,
        IReadOnlyList<(CandidateMove Move, float Probability, float Logit)> neuralRanked,
        IEngineAnalyzer engine,
        CarefulConfig config,
        CancellationToken ct = default)
    {
        if (neuralRanked.Count == 0)
            throw new InvalidOperationException("No candidate moves to select from.");

        int timeout = config.AnalysisTimeoutMilliseconds;

        // Search 1: engine's unconstrained best move
        string? stockfishBest = await engine.GetBestMoveAsync(fen, config.Depth, timeout / 2, ct);
        if (stockfishBest == null)
            throw new InvalidOperationException("Stockfish failed to produce a best move.");

        // Build candidate set C: top neural candidates ∪ { B }
        var candidateSet = neuralRanked
            .Take(config.MaxNeuralCandidates)
            .Select(c => c.Move.Uci)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        candidateSet.Add(stockfishBest);

        // Search 2: score candidate set at the same depth
        IReadOnlyDictionary<string, int> scores;
        try
        {
            scores = await engine.ScoreMovesAsync(fen, candidateSet.ToList(), config.Depth, timeout / 2, ct);
        }
        catch when (config.FallbackToStockfishBestMove)
        {
            var fallbackMove = neuralRanked.First(c => c.Move.Uci.Equals(stockfishBest, StringComparison.OrdinalIgnoreCase)).Move;
            return (fallbackMove, "stockfish-best", new Dictionary<string, int>());
        }

        int maxScore = scores.Count > 0 ? scores.Values.Max() : 0;

        // Filter: loss <= maxCentipawnLoss
        var survivors = neuralRanked
            .Where(c => scores.TryGetValue(c.Move.Uci, out int s) && (maxScore - s) <= config.MaxCentipawnLoss)
            .ToList();

        // Invariant: survivor set is never empty (best scoring move has loss 0)
        var chosen = survivors.Count > 0
            ? survivors[0].Move
            : neuralRanked.First(c => c.Move.Uci.Equals(stockfishBest, StringComparison.OrdinalIgnoreCase)).Move;

        return (chosen, null, scores);
    }
}

// =====================================================================================
// 4. MoveService (Unified API and CLI Entry Point)
// =====================================================================================

public sealed record CandidateResultDto(string Move, float Probability, int? EngineScoreCp);
public sealed record MoveResultDto(string Move, string Mode, string? Fallback, IReadOnlyList<CandidateResultDto> Candidates);

public sealed class MoveService
{
    private readonly IChessRulesAdapter _rules;
    private readonly PolicyPredictor _predictor;
    private readonly IEngineAnalyzer _engine;
    private readonly CarefulConfig _carefulConfig;

    public MoveService(
        IChessRulesAdapter rules,
        PolicyPredictor predictor,
        IEngineAnalyzer engine,
        CarefulConfig? carefulConfig = null)
    {
        _rules = rules;
        _predictor = predictor;
        _engine = engine;
        _carefulConfig = carefulConfig ?? new CarefulConfig();
    }

    public async Task<MoveResultDto> GetMoveAsync(string fen, string mode, CancellationToken ct = default)
    {
        if (!_rules.TryNormalizeFen(fen, out string normalizedFen, out var fenError))
            throw new ArgumentException($"Invalid FEN: {fenError}", nameof(fen));

        if (_rules.IsTerminal(normalizedFen))
            throw new InvalidOperationException("Position is terminal (checkmate or stalemate).");

        var neuralRanked = _predictor.Predict(normalizedFen);
        if (neuralRanked.Count == 0)
            throw new InvalidOperationException("No legal moves available.");

        if (string.Equals(mode, "imitate", StringComparison.OrdinalIgnoreCase))
        {
            var best = neuralRanked[0].Move.Uci;
            var dtos = neuralRanked.Select(c => new CandidateResultDto(c.Move.Uci, c.Probability, null)).ToList();
            return new MoveResultDto(best, "imitate", null, dtos);
        }

        if (string.Equals(mode, "careful", StringComparison.OrdinalIgnoreCase))
        {
            var (move, fallback, scores) = await CarefulMoveSelector.SelectAsync(
                normalizedFen, neuralRanked, _engine, _carefulConfig, ct);

            var dtos = neuralRanked.Select(c =>
            {
                scores.TryGetValue(c.Move.Uci, out int score);
                return new CandidateResultDto(c.Move.Uci, c.Probability, scores.ContainsKey(c.Move.Uci) ? score : null);
            }).ToList();

            return new MoveResultDto(move.Uci, "careful", fallback, dtos);
        }

        throw new ArgumentException($"Unknown mode: '{mode}'. Allowed modes: 'imitate', 'careful'.", nameof(mode));
    }
}