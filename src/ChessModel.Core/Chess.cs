// Chess adapter (backed by pax.chess), PGN reader, game fingerprint, chess records
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pax = pax.chess;
using PaxValidation = pax.chess.Validation;

namespace ChessModel.Core;

// =====================================================================================
// 1. Small chess records
// =====================================================================================

public enum PieceKind { Pawn = 0, Knight = 1, Bishop = 2, Rook = 3, Queen = 4, King = 5 }

/// <summary>
/// One legal move. Squares are ABSOLUTE (a1 = 0 ... h8 = 63); encoders do any mirroring.
/// Uci is normalized: castling is e1g1 / e1c1 / e8g8 / e8c8, promotions carry a lowercase letter.
/// </summary>
public sealed record CandidateMove(
    string Uci,
    int From,
    int To,
    PieceKind Piece,
    PieceKind? Captured,
    PieceKind? Promotion,
    bool IsCapture,
    bool IsCastling,
    bool IsEnPassant);

public static class Squares
{
    public static string Name(int sq) => $"{(char)('a' + (sq & 7))}{(char)('1' + (sq >> 3))}";

    public static bool TryParse(string? s, out int sq)
    {
        sq = -1;
        if (s is null || s.Length != 2 || s[0] < 'a' || s[0] > 'h' || s[1] < '1' || s[1] > '8') return false;
        sq = (s[1] - '1') * 8 + (s[0] - 'a');
        return true;
    }
}

// =====================================================================================
// 2. IChessRulesAdapter
// =====================================================================================

public interface IChessRulesAdapter
{
    string StartFen { get; }

    /// <summary>Validates a FEN and returns it in canonical form.</summary>
    bool TryNormalizeFen(string fen, out string normalized, out string? error);

    /// <summary>All legal moves, in a deterministic order (by source square, then generation order).</summary>
    IReadOnlyList<CandidateMove> GetLegalMoves(string fen);

    /// <summary>Applies a UCI move. Returns false if the FEN is invalid or the move is illegal.</summary>
    bool TryApplyUci(string fen, string uci, out string newFen);

    bool IsInCheck(string fen);

    /// <summary>True when the side to move has no legal move (checkmate or stalemate).</summary>
    bool IsTerminal(string fen);

    bool IsWhiteToMove(string fen);
}

// =====================================================================================
// 3. ChessLibraryAdapter
//
// Backed by the pax.chess NuGet package (v0.7.0+, net10.0, AGPL-3.0-or-later).
// Everything that touches pax.chess types lives in this section, behind IChessRulesAdapter;
// nothing else in the project sees them (they are aliased as "Pax." on purpose).
//
// pax.chess supplies: board model, legal-move generation, check/mate/stalemate detection,
// FEN (de)serialization and UCI helpers. This adapter adds the parts the library leaves open:
//   * strict FEN validation (the library's FEN parser does not validate and needs all 6 fields),
//   * promotion expansion (the library returns ONE move per promoting pawn, without a piece),
//   * canonical FEN output (Lichess en-passant convention, corrected castling rights and
//     half-move clock; see Advance()).
// =====================================================================================

public sealed class ChessLibraryAdapter : IChessRulesAdapter
{
    public const string StandardStartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>Promotion pieces in generation order (q, r, b, n).</summary>
    private static readonly Pax.PieceType[] PromotionOrder =
        { Pax.PieceType.Queen, Pax.PieceType.Rook, Pax.PieceType.Bishop, Pax.PieceType.Knight };

    public string StartFen => StandardStartFen;

    public bool TryNormalizeFen(string fen, out string normalized, out string? error)
    {
        try
        {
            normalized = Pax.FenSerializer.Serialize(PositionParser.Parse(fen));
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            normalized = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    public IReadOnlyList<CandidateMove> GetLegalMoves(string fen)
    {
        var pos = PositionParser.Parse(fen);
        var legal = LegalMoves(pos);
        var result = new List<CandidateMove>(legal.Count);
        foreach (var (candidate, _) in legal) result.Add(candidate);
        return result;
    }

    public bool TryApplyUci(string fen, string uci, out string newFen)
    {
        newFen = string.Empty;
        if (string.IsNullOrWhiteSpace(uci)) return false;

        Pax.BoardPosition pos;
        try { pos = PositionParser.Parse(fen); }
        catch (FormatException) { return false; }

        var wanted = uci.Trim().ToLowerInvariant();
        foreach (var (candidate, move) in LegalMoves(pos))
        {
            if (candidate.Uci != wanted) continue;
            newFen = Pax.FenSerializer.Serialize(Advance(pos, candidate, move));
            return true;
        }
        return false;
    }

    public bool IsInCheck(string fen) => InCheck(PositionParser.Parse(fen));

    public bool IsTerminal(string fen)
    {
        var state = PaxValidation.PseudoMoveValidator.GetGameState(PositionParser.Parse(fen));
        return state is Pax.GameState.Checkmate or Pax.GameState.Stalemate;
    }

    public bool IsWhiteToMove(string fen) => PositionParser.Parse(fen).SideToMove == Pax.PieceColor.White;

    // ---------------------------------------------------------------- helpers

    private static bool InCheck(Pax.BoardPosition pos) =>
        PaxValidation.PseudoMoveValidator.IsSquareAttacked(
            pos.Board.GetKingSquare(pos.SideToMove), pos.SideToMove, pos);

    /// <summary>
    /// Every legal move, ordered by source square (0..63) and then by the library's generation order.
    /// Promotions are expanded to four moves each (q, r, b, n).
    /// </summary>
    private static List<(CandidateMove Candidate, Pax.Move Move)> LegalMoves(Pax.BoardPosition pos)
    {
        var result = new List<(CandidateMove, Pax.Move)>(40);
        for (int sq = 0; sq < 64; sq++)
        {
            if (pos.Board[sq] is not { } piece || piece.Color != pos.SideToMove) continue;

            var generated = PaxValidation.PseudoMoveValidator.GetValidMoves(new Pax.Square(sq), pos, out _);
            foreach (var move in generated)
            {
                if (piece.Type == Pax.PieceType.Pawn && move.To.Rank is 0 or 7)
                {
                    foreach (var promotion in PromotionOrder)
                    {
                        var promoted = move with
                        {
                            Promotion = promotion,
                            MoveType = move.MoveType | Pax.MoveType.Promotion
                        };
                        result.Add((ToCandidate(pos, piece, promoted), promoted));
                    }
                }
                else
                {
                    result.Add((ToCandidate(pos, piece, move), move));
                }
            }
        }
        return result;
    }

    private static CandidateMove ToCandidate(Pax.BoardPosition pos, Pax.Piece piece, Pax.Move move)
    {
        bool enPassant = IsEnPassant(pos, piece, move);
        Pax.PieceType? capturedType = enPassant ? Pax.PieceType.Pawn : pos.Board[move.To.Index]?.Type;
        bool castling = (move.MoveType & (Pax.MoveType.CastlingKingSide | Pax.MoveType.CastlingQueenSide)) != 0;

        return new CandidateMove(
            Pax.Extensions.Uci.GetUci(move),
            move.From.Index,
            move.To.Index,
            KindOf(piece.Type),
            capturedType is { } c ? KindOf(c) : null,
            move.Promotion is { } p ? KindOf(p) : null,
            capturedType is not null,
            castling,
            enPassant);
    }

    private static bool IsEnPassant(Pax.BoardPosition pos, Pax.Piece piece, Pax.Move move) =>
        piece.Type == Pax.PieceType.Pawn
        && pos.EnPassantTarget == move.To
        && move.From.File != move.To.File
        && pos.Board[move.To.Index] is null;

    private static PieceKind KindOf(Pax.PieceType type) => type switch
    {
        Pax.PieceType.Pawn => PieceKind.Pawn,
        Pax.PieceType.Knight => PieceKind.Knight,
        Pax.PieceType.Bishop => PieceKind.Bishop,
        Pax.PieceType.Rook => PieceKind.Rook,
        Pax.PieceType.Queen => PieceKind.Queen,
        Pax.PieceType.King => PieceKind.King,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown piece type.")
    };

    /// <summary>
    /// Plays a legal move and returns the resulting position in canonical form. The library's
    /// BoardPosition.MakeMove is used for the board itself; three of its derived fields are recomputed:
    ///  - castling rights: the library clears them by rook FILE only (a rook on a3 would wrongly clear
    ///    the a1 right); here only the real corner squares count;
    ///  - half-move clock: the library does not reset it after an en-passant capture;
    ///  - en-passant square: kept only when a capture is actually possible (Lichess convention).
    /// </summary>
    private static Pax.BoardPosition Advance(Pax.BoardPosition pos, CandidateMove candidate, Pax.Move move)
    {
        var next = pos.MakeMove(move);
        var moving = pos.Board[move.From.Index]!.Value;

        var rights = pos.CastlingRights;
        if (moving.Type == Pax.PieceType.King)
        {
            rights &= moving.Color == Pax.PieceColor.White
                ? ~(Pax.CastlingRights.WhiteKingSide | Pax.CastlingRights.WhiteQueenSide)
                : ~(Pax.CastlingRights.BlackKingSide | Pax.CastlingRights.BlackQueenSide);
        }
        foreach (int sq in new[] { move.From.Index, move.To.Index })
        {
            rights &= sq switch
            {
                0 => ~Pax.CastlingRights.WhiteQueenSide,
                7 => ~Pax.CastlingRights.WhiteKingSide,
                56 => ~Pax.CastlingRights.BlackQueenSide,
                63 => ~Pax.CastlingRights.BlackKingSide,
                _ => ~Pax.CastlingRights.None
            };
        }

        int halfmove = moving.Type == Pax.PieceType.Pawn || candidate.IsCapture ? 0 : pos.HalfmoveClock + 1;
        var ep = next.EnPassantTarget is { } target
            ? PositionParser.CapturableEnPassant(next.Board, next.SideToMove, target)
            : null;

        return new Pax.BoardPosition(next.Board, next.SideToMove, rights, ep, halfmove, next.FullmoveNumber);
    }
}

/// <summary>
/// Strict FEN parser on top of pax.chess. The library's own FenSerializer.Parse neither validates
/// nor tolerates missing counters, so this checks the string first and hands only a validated,
/// completed FEN to the library.
/// </summary>
internal static class PositionParser
{
    public static Pax.BoardPosition Parse(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen)) throw new FormatException("FEN is empty.");
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) throw new FormatException("FEN needs at least 4 fields.");

        // ---- piece placement
        var ranks = parts[0].Split('/');
        if (ranks.Length != 8) throw new FormatException("FEN board must have 8 ranks.");
        int whiteKings = 0, blackKings = 0;
        for (int i = 0; i < 8; i++)
        {
            int file = 0;
            foreach (char c in ranks[i])
            {
                if (c >= '1' && c <= '8') file += c - '0';
                else if ("PNBRQKpnbrqk".IndexOf(c) >= 0)
                {
                    if (file > 7) throw new FormatException("FEN rank has too many files.");
                    if ((c == 'P' || c == 'p') && (i == 0 || i == 7))
                        throw new FormatException("Pawns cannot stand on the first or last rank.");
                    if (c == 'K') whiteKings++;
                    else if (c == 'k') blackKings++;
                    file++;
                }
                else throw new FormatException($"Invalid FEN character '{c}'.");
            }
            if (file != 8) throw new FormatException("Each FEN rank must cover exactly 8 files.");
        }
        if (whiteKings != 1 || blackKings != 1) throw new FormatException("Each side needs exactly one king.");

        // ---- side to move
        var side = parts[1] switch
        {
            "w" => Pax.PieceColor.White,
            "b" => Pax.PieceColor.Black,
            _ => throw new FormatException("Side to move must be 'w' or 'b'.")
        };

        // ---- castling / en passant syntax
        if (parts[2] != "-")
        {
            foreach (char c in parts[2])
                if ("KQkq".IndexOf(c) < 0) throw new FormatException($"Invalid castling character '{c}'.");
        }
        if (parts[3] != "-" && !Squares.TryParse(parts[3], out _))
            throw new FormatException("Invalid en-passant square.");

        int halfmove = parts.Length > 4 && int.TryParse(parts[4], out int hm) && hm >= 0 ? hm : 0;
        int fullmove = parts.Length > 5 && int.TryParse(parts[5], out int fm) && fm >= 1 ? fm : 1;

        // ---- let the library build the board from the now-validated string
        Pax.BoardPosition parsed;
        try
        {
            parsed = Pax.FenSerializer.Parse($"{parts[0]} {parts[1]} {parts[2]} {parts[3]} {halfmove} {fullmove}");
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new FormatException($"FEN could not be parsed: {ex.Message}", ex);
        }

        var board = parsed.Board;

        // ---- drop castling rights that the piece placement cannot support
        var rights = parsed.CastlingRights;
        if (!(Is(board, 4, Pax.PieceType.King, Pax.PieceColor.White) && Is(board, 7, Pax.PieceType.Rook, Pax.PieceColor.White)))
            rights &= ~Pax.CastlingRights.WhiteKingSide;
        if (!(Is(board, 4, Pax.PieceType.King, Pax.PieceColor.White) && Is(board, 0, Pax.PieceType.Rook, Pax.PieceColor.White)))
            rights &= ~Pax.CastlingRights.WhiteQueenSide;
        if (!(Is(board, 60, Pax.PieceType.King, Pax.PieceColor.Black) && Is(board, 63, Pax.PieceType.Rook, Pax.PieceColor.Black)))
            rights &= ~Pax.CastlingRights.BlackKingSide;
        if (!(Is(board, 60, Pax.PieceType.King, Pax.PieceColor.Black) && Is(board, 56, Pax.PieceType.Rook, Pax.PieceColor.Black)))
            rights &= ~Pax.CastlingRights.BlackQueenSide;

        // ---- the side NOT to move must not be in check
        var other = side == Pax.PieceColor.White ? Pax.PieceColor.Black : Pax.PieceColor.White;
        if (PaxValidation.PseudoMoveValidator.IsSquareAttacked(board.GetKingSquare(other), other, parsed))
            throw new FormatException("Illegal position: the side not to move is in check.");

        // ---- en passant: right rank required; kept only when a capture is really possible
        Pax.Square? ep = parsed.EnPassantTarget;
        if (ep is { } target)
        {
            if (target.Rank != (side == Pax.PieceColor.White ? 5 : 2))
                throw new FormatException("En-passant square is on the wrong rank.");
            ep = CapturableEnPassant(board, side, target);
        }

        return new Pax.BoardPosition(board, side, rights, ep, parsed.HalfmoveClock, parsed.FullmoveNumber);
    }

    /// <summary>
    /// Returns the en-passant square only when the side to move has a pawn that can capture onto it
    /// (the double-pushed pawn must be there and the target square empty); otherwise null.
    /// </summary>
    public static Pax.Square? CapturableEnPassant(Pax.Board board, Pax.PieceColor sideToMove, Pax.Square ep)
    {
        bool white = sideToMove == Pax.PieceColor.White;
        if (ep.Rank != (white ? 5 : 2)) return null;
        if (board[ep.Index] is not null) return null;

        int pawnRank = white ? 4 : 3;
        var enemyPawn = new Pax.Piece(Pax.PieceType.Pawn, white ? Pax.PieceColor.Black : Pax.PieceColor.White);
        if (board[pawnRank * 8 + ep.File] != enemyPawn) return null;

        var mine = new Pax.Piece(Pax.PieceType.Pawn, sideToMove);
        bool canCapture = (ep.File > 0 && board[pawnRank * 8 + ep.File - 1] == mine)
                          || (ep.File < 7 && board[pawnRank * 8 + ep.File + 1] == mine);
        return canCapture ? ep : null;
    }

    private static bool Is(Pax.Board board, int index, Pax.PieceType type, Pax.PieceColor color) =>
        board[index] == new Pax.Piece(type, color);
}

// =====================================================================================
// 4. GameFingerprint
// =====================================================================================

public static class GameFingerprint
{
    /// <summary>Stable ID: SHA-256 over normalized players, UTC date/time (when present) and the UCI move list.</summary>
    public static string Compute(PgnGame game, IReadOnlyList<string> uciMoves)
    {
        var sb = new StringBuilder();
        sb.Append((game.Header("White") ?? string.Empty).Trim().ToLowerInvariant()).Append('|');
        sb.Append((game.Header("Black") ?? string.Empty).Trim().ToLowerInvariant()).Append('|');
        sb.Append(game.Header("UTCDate") ?? game.Header("Date") ?? string.Empty).Append('|');
        sb.Append(game.Header("UTCTime") ?? game.Header("StartTime") ?? string.Empty).Append('|');
        sb.Append(string.Join(' ', uciMoves));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}

// =====================================================================================
// 5. PgnGameReader
//
// Deliberately NOT built on pax.chess's PgnSerializer: that parser handles one game per string and
// keeps only a few tags as typed fields (no UTCDate/UTCTime, which the fingerprint needs), whereas
// this reader streams multi-game files and reports per-ply errors. SAN is resolved against the
// adapter's legal moves, so all rules knowledge still comes from IChessRulesAdapter.
// =====================================================================================

public sealed record PgnGame(
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<string> SanMoves,
    string? ResultToken,
    int Index,
    string Source)
{
    public string? Header(string key) => Headers.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Ply is 0-based from the first move of the game (custom-FEN games included).</summary>
public sealed record ReplayedMove(int Ply, string FenBefore, string Uci, string San);

public sealed record ReplayResult(string StartFen, IReadOnlyList<ReplayedMove> Moves, string FinalFen, string? Error);

public sealed class PgnGameReader
{
    private static readonly Regex HeaderRegex =
        new(@"^\[\s*(\w+)\s+""((?:[^""\\]|\\.)*)""\s*\]\s*$", RegexOptions.Compiled);
    private static readonly Regex MoveNumberRegex = new(@"^\d+\.+", RegexOptions.Compiled);
    private static readonly Regex SanRegex =
        new(@"^([NBRQK])?([a-h])?([1-8])?x?([a-h][1-8])(?:=?([NBRQ]))?$", RegexOptions.Compiled);
    private static readonly HashSet<string> ResultTokens = new() { "1-0", "0-1", "1/2-1/2", "*" };

    private readonly IChessRulesAdapter _rules;

    public PgnGameReader(IChessRulesAdapter rules) => _rules = rules;

    public IEnumerable<PgnGame> ReadFile(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        foreach (var game in Read(reader, path)) yield return game;
    }

    public IEnumerable<PgnGame> Read(TextReader reader, string source = "")
    {
        var headers = NewHeaders();
        var movetext = new StringBuilder();
        int index = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var t = line.Trim().TrimStart('\uFEFF');
            if (t.Length == 0) continue;

            var hm = t[0] == '[' ? HeaderRegex.Match(t) : Match.Empty;
            if (hm.Success)
            {
                if (movetext.Length > 0) // a header after movetext starts the next game
                {
                    yield return Build(headers, movetext.ToString(), index++, source);
                    headers = NewHeaders();
                    movetext.Clear();
                }
                headers[hm.Groups[1].Value] = hm.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
            }
            else
            {
                movetext.Append(t).Append(' ');
            }
        }
        if (headers.Count > 0 || movetext.Length > 0)
            yield return Build(headers, movetext.ToString(), index, source);
    }

    private static Dictionary<string, string> NewHeaders() => new(StringComparer.OrdinalIgnoreCase);

    private static PgnGame Build(Dictionary<string, string> headers, string movetext, int index, string source)
    {
        var sans = ParseMoveText(movetext, out var result);
        return new PgnGame(headers, sans, result, index, source);
    }

    /// <summary>Main line only. Skips {comments} (incl. [%clk ...]), (variations), $NAGs, move numbers and results.</summary>
    private static List<string> ParseMoveText(string text, out string? result)
    {
        var sans = new List<string>();
        result = null;
        int i = 0, depth = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{')
            {
                int end = text.IndexOf('}', i + 1);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth > 0) depth--; i++; continue; }
            if (depth > 0 || char.IsWhiteSpace(c)) { i++; continue; }

            int j = i;
            while (j < text.Length && !char.IsWhiteSpace(text[j]) && text[j] != '{' && text[j] != '(' && text[j] != ')')
                j++;
            var token = text.Substring(i, j - i);
            i = j;

            if (token.StartsWith('$')) continue; // NAG
            if (ResultTokens.Contains(token)) { result = token; continue; }
            token = MoveNumberRegex.Replace(token, string.Empty); // "12." / "12..." / "12.e4"
            if (token.Length == 0) continue;
            sans.Add(token);
        }
        return sans;
    }

    /// <summary>Replays the main line. Never throws on a bad game; the problem is reported in Error.</summary>
    public ReplayResult Replay(PgnGame game)
    {
        string start = _rules.StartFen;
        var customFen = game.Header("FEN");
        if (!string.IsNullOrWhiteSpace(customFen) && game.Header("SetUp") != "0")
        {
            if (!_rules.TryNormalizeFen(customFen, out start, out var fenError))
                return new ReplayResult(customFen, Array.Empty<ReplayedMove>(), customFen, $"invalid start FEN: {fenError}");
        }

        string fen = start;
        var moves = new List<ReplayedMove>(game.SanMoves.Count);
        int ply = 0;
        foreach (var san in game.SanMoves)
        {
            var legal = _rules.GetLegalMoves(fen);
            if (!TryResolveSan(san, legal, out var move, out var error))
                return new ReplayResult(start, moves, fen, $"ply {ply}: {error} ('{san}')");
            if (!_rules.TryApplyUci(fen, move.Uci, out var next))
                return new ReplayResult(start, moves, fen, $"ply {ply}: could not apply {move.Uci}");
            moves.Add(new ReplayedMove(ply, fen, move.Uci, san));
            fen = next;
            ply++;
        }
        return new ReplayResult(start, moves, fen, null);
    }

    /// <summary>Matches a SAN token against the legal moves of the position.</summary>
    public static bool TryResolveSan(string san, IReadOnlyList<CandidateMove> legal, out CandidateMove move, out string error)
    {
        move = null!;
        error = string.Empty;

        var s = san.TrimEnd('+', '#', '!', '?');
        if (s.StartsWith("0-0")) s = s.Replace('0', 'O');

        if (s == "O-O" || s == "O-O-O")
        {
            int wantFile = s == "O-O" ? 6 : 2;
            var castles = legal.Where(c => c.IsCastling && (c.To & 7) == wantFile).ToList();
            if (castles.Count == 1) { move = castles[0]; return true; }
            error = "castling is not legal here";
            return false;
        }

        var m = SanRegex.Match(s);
        if (!m.Success) { error = "unrecognized move text"; return false; }

        var kind = m.Groups[1].Success ? KindFromLetter(m.Groups[1].Value[0]) : PieceKind.Pawn;
        Squares.TryParse(m.Groups[4].Value, out int dest);
        PieceKind? promo = m.Groups[5].Success ? KindFromLetter(m.Groups[5].Value[0]) : null;

        var matches = legal.Where(c => c.Piece == kind && c.To == dest && c.Promotion == promo && !c.IsCastling);
        if (m.Groups[2].Success) matches = matches.Where(c => (c.From & 7) == m.Groups[2].Value[0] - 'a');
        if (m.Groups[3].Success) matches = matches.Where(c => (c.From >> 3) == m.Groups[3].Value[0] - '1');

        var list = matches.ToList();
        if (list.Count == 1) { move = list[0]; return true; }
        error = list.Count == 0 ? "illegal move" : "ambiguous move";
        return false;
    }

    private static PieceKind KindFromLetter(char c) => c switch
    {
        'N' => PieceKind.Knight,
        'B' => PieceKind.Bishop,
        'R' => PieceKind.Rook,
        'Q' => PieceKind.Queen,
        'K' => PieceKind.King,
        _ => PieceKind.Pawn
    };
}