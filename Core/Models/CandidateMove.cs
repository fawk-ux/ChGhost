namespace ChGhost.Core.Models;

public sealed record CandidateMove(
    string Uci,
    int ScoreCp,
    bool IsMate,
    int MateIn,
    int Depth,
    int MultiPvIndex)
{
    /// <summary>
    /// Нормализованная оценка в сантипешках для единого сравнения обычных позиций и форсированных матов.
    /// Быстрый мат в свою пользу (+1) оценивается выше медленного (+3);
    /// отсрочка мата в свои ворота (-3) оценивается выше немедленного (-1).
    /// </summary>
    public int EffectiveCp => IsMate
        ? (MateIn > 0
            ? 100_000 - Math.Min(MateIn, 500) * 100
            : -100_000 + Math.Min(Math.Abs(MateIn), 500) * 100)
        : ScoreCp;
}

public sealed record HumanizerOptions(
    int MaxCpLoss = 45,
    double Temperature = 32.0,
    int ForcedTacticGapCp = 160,
    int AdvisorMaxArrows = 2,
    int AdvisorMaxGapCp = 65,
    bool PacingEnabled = true,
    int MinDelayMs = 120,
    int MaxDelayMs = 950,
    bool AdvisorMode = true,
    bool StrictBestMove = false);

public sealed record MoveRecommendation(
    CandidateMove Primary,
    IReadOnlyList<CandidateMove> Alternatives,
    int RecommendedDelayMs,
    double ComplexityScore)
{
    public int PickIndex => Primary.MultiPvIndex;
}
