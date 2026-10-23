using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChessModel.Core;

public sealed record DatasetSplitFractions(double Train = 0.8, double Validation = 0.1, double Test = 0.1);

public sealed record DatasetConfig(
    IReadOnlyList<string> PlayerNames,
    IReadOnlyList<string> AllowedTimeControls,
    int SplitSeed = 42,
    DatasetSplitFractions? Split = null);

public sealed record PositionRecord(
    int SchemaVersion,
    string GameId,
    int Ply,
    string Fen,
    string TargetUci,
    string PlayerColor,
    string TimeControl,
    string? Result,
    string Split);

public sealed class DatasetManifest
{
    public int SchemaVersion { get; set; } = 1;
    public int TotalGamesInput { get; set; }
    public int AcceptedGames { get; set; }
    public int SkippedGames { get; set; }
    public Dictionary<string, int> SkipReasons { get; set; } = new();
    public Dictionary<string, int> GamesPerPlayer { get; set; } = new();
    public Dictionary<string, int> GamesPerTimeControl { get; set; } = new();
    public int TotalPositions { get; set; }
    public Dictionary<string, int> PositionsPerSplit { get; set; } = new();
}

public static class DatasetReader
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<PositionRecord> ReadJsonl(string filePath, string? splitFilter = null)
    {
        if (!File.Exists(filePath)) yield break;
        using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<PositionRecord>(line, JsonOpts);
            if (record != null && (splitFilter == null || string.Equals(record.Split, splitFilter, StringComparison.OrdinalIgnoreCase)))
                yield return record;
        }
    }
}

public sealed class PgnDatasetBuilder
{
    private readonly IChessRulesAdapter _rules;
    private readonly DatasetConfig _config;

    public PgnDatasetBuilder(IChessRulesAdapter rules, DatasetConfig config)
    {
        _rules = rules;
        _config = config;
    }

    public DatasetManifest Build(string pgnDir, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var pgnFiles = Directory.GetFiles(pgnDir, "*.pgn", SearchOption.AllDirectories);
        var reader = new PgnGameReader(_rules);

        var manifest = new DatasetManifest();
        var seenFingerprints = new HashSet<string>();
        var positionsPath = Path.Combine(outputDir, "positions.jsonl");

        using var outStream = File.Create(positionsPath);
        using var writer = new StreamWriter(outStream, Encoding.UTF8);

        foreach (var pgnPath in pgnFiles)
        {
            foreach (var game in reader.ReadFile(pgnPath))
            {
                manifest.TotalGamesInput++;

                // 1. Variant check
                var variant = game.Header("Variant");
                if (variant != null && !variant.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                {
                    RecordSkip(manifest, "non-standard-variant");
                    continue;
                }

                // 2. Custom FEN check
                if (!string.IsNullOrWhiteSpace(game.Header("FEN")) && game.Header("SetUp") != "0")
                {
                    RecordSkip(manifest, "custom-start-fen");
                    continue;
                }

                // 3. Player identification
                string? white = game.Header("White");
                string? black = game.Header("Black");
                bool isWhite = _config.PlayerNames.Any(p => string.Equals(p, white, StringComparison.OrdinalIgnoreCase));
                bool isBlack = _config.PlayerNames.Any(p => string.Equals(p, black, StringComparison.OrdinalIgnoreCase));

                if (!isWhite && !isBlack) { RecordSkip(manifest, "player-not-found"); continue; }
                if (isWhite && isBlack) { RecordSkip(manifest, "ambiguous-player"); continue; }

                string matchedName = isWhite ? white! : black!;
                manifest.GamesPerPlayer[matchedName] = manifest.GamesPerPlayer.GetValueOrDefault(matchedName) + 1;

                // 4. Time control normalization & filter
                string rawTc = game.Header("TimeControl")?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(rawTc)) { RecordSkip(manifest, "time-control-missing"); continue; }

                string normalizedTc = rawTc.Contains('+') ? rawTc : $"{rawTc}+0";
                bool tcAllowed = _config.AllowedTimeControls.Contains(rawTc, StringComparer.OrdinalIgnoreCase) ||
                                 _config.AllowedTimeControls.Contains(normalizedTc, StringComparer.OrdinalIgnoreCase);

                if (!tcAllowed) { RecordSkip(manifest, "time-control-not-allowed"); continue; }

                manifest.GamesPerTimeControl[normalizedTc] = manifest.GamesPerTimeControl.GetValueOrDefault(normalizedTc) + 1;

                // 5. Replay and verify moves
                var replay = reader.Replay(game);
                if (replay.Error != null) { RecordSkip(manifest, $"replay-error: {replay.Error}"); continue; }

                // 6. Deduplicate exact games
                string fingerprint = GameFingerprint.Compute(game, replay.Moves.Select(m => m.Uci).ToList());
                if (!seenFingerprints.Add(fingerprint)) { RecordSkip(manifest, "duplicate-game"); continue; }

                // 7. Deterministic game-level split
                string split = AssignSplit(fingerprint, _config.SplitSeed, _config.Split ?? new DatasetSplitFractions());

                manifest.AcceptedGames++;

                // 8. Write positions before the player's turns
                for (int ply = 0; ply < replay.Moves.Count; ply++)
                {
                    bool isPlayerTurn = isWhite ? (ply % 2 == 0) : (ply % 2 == 1);
                    if (!isPlayerTurn) continue;

                    var move = replay.Moves[ply];
                    var rec = new PositionRecord(
                        SchemaVersion: 1,
                        GameId: fingerprint,
                        Ply: ply,
                        Fen: move.FenBefore,
                        TargetUci: move.Uci,
                        PlayerColor: isWhite ? "white" : "black",
                        TimeControl: normalizedTc,
                        Result: game.ResultToken,
                        Split: split);

                    writer.WriteLine(JsonSerializer.Serialize(rec));
                    manifest.TotalPositions++;
                    manifest.PositionsPerSplit[split] = manifest.PositionsPerSplit.GetValueOrDefault(split) + 1;
                }
            }
        }

        manifest.SkippedGames = manifest.TotalGamesInput - manifest.AcceptedGames;
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outputDir, "manifest.json"), manifestJson);

        return manifest;
    }

    private static void RecordSkip(DatasetManifest manifest, string reason) =>
        manifest.SkipReasons[reason] = manifest.SkipReasons.GetValueOrDefault(reason) + 1;

    private static string AssignSplit(string id, int seed, DatasetSplitFractions split)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}:{seed}"));
        uint val = BitConverter.ToUInt32(hash, 0) % 10000;
        double r = val / 10000.0;

        if (r < split.Train) return "train";
        if (r < split.Train + split.Validation) return "validation";
        return "test";
    }
}