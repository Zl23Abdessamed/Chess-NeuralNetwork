// Feature schema, board encoder, move encoder
namespace ChessModel.Core;

public static class FeatureSchema
{
    public const int Version = 1;
    public const int BoardFeatures = 781;
    public const int MoveFeatures = 148;
    public const int TotalFeatures = BoardFeatures + MoveFeatures; // 929
}

/// <summary>
/// Encodes positions relative to the side to move (781 features).
/// When Black is to move, ranks are flipped (sq ^ 56) and colors swapped.
/// </summary>
public static class BoardEncoder
{
    public static float[] Encode(string fen)
    {
        var features = new float[FeatureSchema.BoardFeatures];
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return features;

        string pieces = parts[0];
        bool isWhite = parts[1] == "w";
        string castling = parts[2];
        string ep = parts[3];
        int halfmove = parts.Length > 4 && int.TryParse(parts[4], out int hm) ? hm : 0;

        // 1. Piece placement (12 planes * 64 squares = 768)
        int rank = 7, file = 0;
        foreach (char c in pieces)
        {
            if (c == '/') { rank--; file = 0; }
            else if (char.IsDigit(c)) { file += c - '0'; }
            else
            {
                int absSq = rank * 8 + file;
                int relSq = isWhite ? absSq : (absSq ^ 56);

                bool isUpper = char.IsUpper(c);
                bool isOwn = isWhite ? isUpper : !isUpper;
                int kind = char.ToLowerInvariant(c) switch
                {
                    'p' => 0, 'n' => 1, 'b' => 2, 'r' => 3, 'q' => 4, 'k' => 5,
                    _ => -1
                };

                if (kind >= 0)
                {
                    int plane = (isOwn ? 0 : 6) + kind;
                    features[plane * 64 + relSq] = 1.0f;
                }
                file++;
            }
        }

        // 2. Castling rights (4 values: own K, own Q, opp K, opp Q)
        features[768] = (isWhite ? castling.Contains('K') : castling.Contains('k')) ? 1.0f : 0.0f;
        features[769] = (isWhite ? castling.Contains('Q') : castling.Contains('q')) ? 1.0f : 0.0f;
        features[770] = (isWhite ? castling.Contains('k') : castling.Contains('K')) ? 1.0f : 0.0f;
        features[771] = (isWhite ? castling.Contains('q') : castling.Contains('Q')) ? 1.0f : 0.0f;

        // 3. En-passant file (8 values: files 0..7)
        if (ep != "-" && Squares.TryParse(ep, out int epSq))
        {
            features[772 + (epSq & 7)] = 1.0f;
        }

        // 4. Normalized halfmove clock (1 value)
        features[780] = Math.Clamp(halfmove, 0, 100) / 100.0f;

        return features;
    }

    public static void Combine(ReadOnlySpan<float> board, ReadOnlySpan<float> move, Span<float> destination)
    {
        board.CopyTo(destination[..FeatureSchema.BoardFeatures]);
        move.CopyTo(destination[FeatureSchema.BoardFeatures..]);
    }

    public static float[] Combine(float[] board, float[] move)
    {
        var row = new float[FeatureSchema.TotalFeatures];
        Combine(board, move, row);
        return row;
    }
}

/// <summary>
/// Encodes candidate moves relative to the side to move (148 features).
/// </summary>
public static class MoveEncoder
{
    public static float[] Encode(CandidateMove move, bool isWhiteToMove)
    {
        var features = new float[FeatureSchema.MoveFeatures];

        int relFrom = isWhiteToMove ? move.From : (move.From ^ 56);
        int relTo = isWhiteToMove ? move.To : (move.To ^ 56);

        // 1. Source (64) & Destination (64)
        features[relFrom] = 1.0f;
        features[64 + relTo] = 1.0f;

        // 2. Moving piece (6: Pawn=0..King=5)
        features[128 + (int)move.Piece] = 1.0f;

        // 3. Captured piece (6: Pawn=0, Knight=1, Bishop=2, Rook=3, Queen=4, None=5)
        int capturedIdx = 5;
        if (move.IsEnPassant || move.Captured == PieceKind.Pawn) capturedIdx = 0;
        else if (move.Captured == PieceKind.Knight) capturedIdx = 1;
        else if (move.Captured == PieceKind.Bishop) capturedIdx = 2;
        else if (move.Captured == PieceKind.Rook) capturedIdx = 3;
        else if (move.Captured == PieceKind.Queen) capturedIdx = 4;
        features[134 + capturedIdx] = 1.0f;

        // 4. Promotion (5: None=0, Queen=1, Rook=2, Bishop=3, Knight=4)
        int promoIdx = move.Promotion switch
        {
            PieceKind.Queen => 1,
            PieceKind.Rook => 2,
            PieceKind.Bishop => 3,
            PieceKind.Knight => 4,
            _ => 0
        };
        features[140 + promoIdx] = 1.0f;

        // 5. Flags (3: Capture, Castling, EnPassant)
        features[145] = (move.IsCapture || move.IsEnPassant) ? 1.0f : 0.0f;
        features[146] = move.IsCastling ? 1.0f : 0.0f;
        features[147] = move.IsEnPassant ? 1.0f : 0.0f;

        return features;
    }
}