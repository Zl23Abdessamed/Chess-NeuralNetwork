// Chess, dataset, encoders, model, selector (fake engine)
using ChessModel.Core;
using Xunit;

namespace ChessModel.Tests;

public class CoreTests
{
    private readonly ChessLibraryAdapter _rules = new();

    [Fact]
    public void StandardPosition_Has20LegalMoves()
    {
        var moves = _rules.GetLegalMoves(_rules.StartFen);
        Assert.Equal(20, moves.Count);
    }

    [Fact]
    public void FeatureDimensions_MatchSchema()
    {
        var board = BoardEncoder.Encode(_rules.StartFen);
        var move = MoveEncoder.Encode(_rules.GetLegalMoves(_rules.StartFen)[0], isWhiteToMove: true);
        var combined = BoardEncoder.Combine(board, move);

        Assert.Equal(FeatureSchema.BoardFeatures, board.Length); // 781
        Assert.Equal(FeatureSchema.MoveFeatures, move.Length);   // 148
        Assert.Equal(FeatureSchema.TotalFeatures, combined.Length); // 929
    }

    [Fact]
    public void PerspectiveMirroring_WhiteAndBlackAreIdentical()
    {
        // 1. e4 (White)
        string fenWhite = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";
        // Mirrored: 1... e5 (Black)
        string fenBlack = "rnbqkbnr/pppp1ppp/8/4p3/8/8/PPPPPPPP/RNBQKBNR w KQkq e6 0 1";

        // From Black's perspective in fenWhite vs White's perspective in fenBlack
        var encWhite = BoardEncoder.Encode(fenWhite);
        var encBlack = BoardEncoder.Encode(fenBlack);

        // They must produce identical feature planes
        Assert.Equal(encWhite, encBlack);
    }

    [Fact]
    public async Task CarefulSelector_VetoesBlunder_AndPicksBestSurvivor()
    {
        string fen = _rules.StartFen;
        var moves = _rules.GetLegalMoves(fen);

        // Candidate 0 preferred by neural net, Candidate 1 is second best
        var neuralRanked = new List<(CandidateMove Move, float Probability, float Logit)>
        {
            (moves[0], 0.70f, 2.0f),
            (moves[1], 0.30f, 1.0f)
        };

        // Fake Engine: Move 0 is a massive blunder (-300 cp), Move 1 is solid (0 cp)
        var fakeEngine = new FakeEngine(bestMove: moves[1].Uci, scores: new()
        {
            [moves[0].Uci] = -300,
            [moves[1].Uci] = 0
        });

        var config = new CarefulConfig(MaxCentipawnLoss: 150);

        var (chosen, fallback, _) = await CarefulMoveSelector.SelectAsync(fen, neuralRanked, fakeEngine, config);

        // Blunder (move 0) was vetoed, move 1 was picked
        Assert.Equal(moves[1].Uci, chosen.Uci);
        Assert.Null(fallback);
    }

    [Fact]
    public async Task CarefulSelector_UsesFallback_WhenEngineFails()
    {
        string fen = _rules.StartFen;
        var moves = _rules.GetLegalMoves(fen);

        var neuralRanked = new List<(CandidateMove Move, float Probability, float Logit)>
        {
            (moves[0], 0.90f, 2.0f),
            (moves[1], 0.10f, 1.0f)
        };

        var failingEngine = new FakeEngine(bestMove: moves[1].Uci, throwOnScore: true);
        var config = new CarefulConfig(FallbackToStockfishBestMove: true);

        var (chosen, fallback, _) = await CarefulMoveSelector.SelectAsync(fen, neuralRanked, failingEngine, config);

        Assert.Equal(moves[1].Uci, chosen.Uci);
        Assert.Equal("stockfish-best", fallback);
    }
}

// Mock engine for offline unit testing without starting Stockfish
internal sealed class FakeEngine : IEngineAnalyzer
{
    private readonly string _bestMove;
    private readonly Dictionary<string, int>? _scores;
    private readonly bool _throwOnScore;

    public FakeEngine(string bestMove, Dictionary<string, int>? scores = null, bool throwOnScore = false)
    {
        _bestMove = bestMove;
        _scores = scores;
        _throwOnScore = throwOnScore;
    }

    public Task<string?> GetBestMoveAsync(string fen, int depth, int timeoutMs, CancellationToken ct = default) =>
        Task.FromResult<string?>(_bestMove);

    public Task<IReadOnlyDictionary<string, int>> ScoreMovesAsync(
        string fen, IReadOnlyList<string> uciMoves, int depth, int timeoutMs, CancellationToken ct = default)
    {
        if (_throwOnScore) throw new TimeoutException("Engine timed out");
        return Task.FromResult<IReadOnlyDictionary<string, int>>(_scores ?? new Dictionary<string, int>());
    }

    public void Dispose() { }
}