using ChGhost.Core.Models;

namespace ChGhost.Core.Engine;

public static class HumanMoveSelector
{
    private const int MateForcedThreshold = 6;

    public static MoveRecommendation Select(
        IReadOnlyList<CandidateMove> candidates,
        char[,] grid,
        HumanizerOptions options,
        Random? rng = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(options);

        if (candidates.Count == 0)
            throw new ArgumentException("Список кандидатов пуст.", nameof(candidates));

        rng ??= Random.Shared;

        if (!options.PacingEnabled)
        {
            CandidateMove primary = candidates.FirstOrDefault(c => c.MultiPvIndex == 1) ?? candidates[0];
            IReadOnlyList<CandidateMove> fastAlternatives = Array.Empty<CandidateMove>();
            if (options.AdvisorMode && options.AdvisorMaxArrows > 0 && candidates.Count > 1)
            {
                var remaining = candidates
                    .Where(c => !c.Uci.Equals(primary.Uci, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.EffectiveCp)
                    .ThenByDescending(c => c.Depth)
                    .ThenBy(c => c.MultiPvIndex)
                    .ToList();
                fastAlternatives = BuildAdvisorAlternatives(remaining, primary, primary.EffectiveCp, options);
            }
            return new MoveRecommendation(primary, fastAlternatives, 0, 0.0);
        }

        // Гарантируем порядок от лучшего к худшему
        var ordered = candidates.Count == 1
            ? candidates
            : candidates.OrderByDescending(c => c.EffectiveCp).ThenBy(c => c.MultiPvIndex).ToList();

        var best = ordered[0];

        // 1. Единственный кандидат или форсированный мат — играем точно и без лишней задержки
        if (ordered.Count == 1 || (best.IsMate && Math.Abs(best.MateIn) <= MateForcedThreshold))
        {
            int fastDelay = Math.Min(options.MinDelayMs, rng.Next(40, Math.Max(45, Math.Min(140, options.MaxDelayMs))));
            return new MoveRecommendation(best, Array.Empty<CandidateMove>(), fastDelay, 0.05);
        }

        long gapToSecond = (long)best.EffectiveCp - ordered[1].EffectiveCp;
        bool isForcedTactic = gapToSecond >= options.ForcedTacticGapCp;

        // 2. Форсированная тактика (большой отрыв PV1 от PV2) — берём PV1
        if (isForcedTactic)
        {
            double tacticalComplexity = IsObviousCapture(best.Uci, grid) ? 0.12 : 0.35;
            int delay = ComputeHumanDelayMs(options, tacticalComplexity, rng);
            return new MoveRecommendation(best, Array.Empty<CandidateMove>(), delay, tacticalComplexity);
        }

        // 3. Собираем пул ходов в допустимом коридоре потерь MaxCpLoss
        int maxLoss = Math.Max(0, options.MaxCpLoss);
        double temperature = Math.Max(8.0, options.Temperature);
        var pool = new List<(CandidateMove Move, double Weight)>(ordered.Count);
        double totalWeight = 0.0;

        foreach (var cand in ordered)
        {
            long cpLoss = (long)best.EffectiveCp - cand.EffectiveCp;
            if (cpLoss > maxLoss) continue;

            double humanBonus = EstimateHumanIntuitionBonus(cand.Uci, grid);
            double score = -(double)cpLoss + humanBonus;
            double weight = Math.Exp(Math.Clamp(score / temperature, -12.0, 12.0));

            pool.Add((cand, weight));
            totalWeight += weight;
        }

        CandidateMove chosen = best;
        if (pool.Count > 1 && totalWeight > 0.0)
        {
            double roll = rng.NextDouble() * totalWeight;
            double acc = 0.0;
            foreach (var (move, weight) in pool)
            {
                acc += weight;
                if (roll <= acc)
                {
                    chosen = move;
                    break;
                }
            }
        }

        // 4. Формируем альтернативные стрелки для режима советника (исключая выбранный ход)
        IReadOnlyList<CandidateMove> alternatives = options.AdvisorMode
            ? BuildAdvisorAlternatives(ordered, chosen, best.EffectiveCp, options)
            : Array.Empty<CandidateMove>();

        // 5. Вычисляем сложность позиции для естественного пейсинга
        double complexity = EstimatePositionComplexity(ordered, grid, chosen);
        int pacingDelay = ComputeHumanDelayMs(options, complexity, rng);

        return new MoveRecommendation(chosen, alternatives, pacingDelay, complexity);
    }

    private static IReadOnlyList<CandidateMove> BuildAdvisorAlternatives(
        IReadOnlyList<CandidateMove> candidatesToConsider,
        CandidateMove chosen,
        int referenceEffectiveCp,
        HumanizerOptions options)
    {
        if (options.AdvisorMaxArrows <= 0 || candidatesToConsider.Count == 0)
            return Array.Empty<CandidateMove>();

        var alternatives = new List<CandidateMove>(options.AdvisorMaxArrows);
        foreach (var cand in candidatesToConsider)
        {
            if (cand.Uci.Equals(chosen.Uci, StringComparison.OrdinalIgnoreCase)) continue;
            long gap = (long)referenceEffectiveCp - cand.EffectiveCp;
            if (gap <= options.AdvisorMaxGapCp)
            {
                alternatives.Add(cand);
                if (alternatives.Count >= options.AdvisorMaxArrows)
                    break;
            }
        }
        return alternatives;
    }

    /// <summary>
    /// Оценка «человеческой интуитивности» хода (в эквиваленте сантипешек, примерно от -15 до +25 cp).
    /// Поощряет естественные ходы (рокировка, взятия, развитие лёгких фигур, центр) и слегка штрафует
    /// неинтуитивные компьютерные профилактические перестановки ладьи/короля.
    /// </summary>
    public static double EstimateHumanIntuitionBonus(string uci, char[,] grid)
    {
        if (!TryDecodeUci(uci, out int fromR, out int fromC, out int toR, out int toC))
            return 0.0;

        char piece = grid[fromR, fromC];
        char target = grid[toR, toC];
        if (piece == '1') return 0.0;

        char upper = char.ToUpperInvariant(piece);
        bool isWhite = char.IsUpper(piece);
        double bonus = 0.0;

        // Рокировка — один из самых естественных ходов человека
        if (upper == 'K' && Math.Abs(toC - fromC) == 2)
            return 22.0;

        // Превращение пешки
        if (uci.Length == 5)
            return uci[4] == 'q' || uci[4] == 'Q' ? 20.0 : -10.0;

        // Взятие фигуры соперника
        if (target != '1')
        {
            int targetVal = PieceValue(target);
            int moverVal = PieceValue(piece);
            bonus += targetVal >= moverVal ? 16.0 : 8.0;
        }

        // Развитие лёгких фигур (конь/слон) с начальной горизонтали ближе к центру
        int backRank = isWhite ? 7 : 0;
        if ((upper == 'N' || upper == 'B') && fromR == backRank && toR != backRank)
        {
            bonus += 10.0;
            if (toC is >= 2 and <= 5) bonus += 4.0;
        }

        // Борьба пешками за центр (d4/d5/e4/e5/c4/c5)
        if (upper == 'P' && toC is >= 2 and <= 5 && toR is >= 3 and <= 4)
            bonus += 8.0;

        // Неинтуитивные тихие ходы королём в угол или сдвиг ладьи на 1 клетку по задней линии
        if (target == '1')
        {
            if (upper == 'K' && fromR == backRank && toR == backRank && (toC == 0 || toC == 7))
                bonus -= 12.0;
            if (upper == 'R' && fromR == backRank && toR == backRank && Math.Abs(toC - fromC) == 1 && (fromC == 0 || fromC == 7))
                bonus -= 9.0;
            if (upper == 'P' && (fromC == 0 || fromC == 7) && Math.Abs(toR - fromR) == 1)
                bonus -= 5.0;
        }

        return bonus;
    }

    private static double EstimatePositionComplexity(
        IReadOnlyList<CandidateMove> ordered, char[,] grid, CandidateMove chosen)
    {
        // Очевидное выгодное взятие люди делают быстро
        if (IsObviousCapture(chosen.Uci, grid))
            return 0.18;

        int closeOptions = 0;
        int bestCp = ordered[0].EffectiveCp;
        for (int i = 1; i < ordered.Count; i++)
        {
            if ((long)bestCp - ordered[i].EffectiveCp <= 80)
                closeOptions++;
        }

        int pieces = 0;
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
                if (grid[r, c] != '1') pieces++;

        // Чем больше фигур (миттельшпиль) и плотнее кандидаты, тем выше сложность
        double phaseFactor = Math.Clamp((pieces - 6) / 22.0, 0.15, 1.0);
        double branchFactor = Math.Clamp(closeOptions / 3.0, 0.2, 1.0);

        return Math.Clamp(0.25 + 0.45 * branchFactor + 0.30 * phaseFactor, 0.15, 1.0);
    }

    private static int ComputeHumanDelayMs(HumanizerOptions options, double complexity, Random rng)
    {
        if (!options.PacingEnabled || options.MaxDelayMs <= 0)
            return 0;

        int minMs = Math.Max(0, options.MinDelayMs);
        int maxMs = Math.Max(minMs, options.MaxDelayMs);
        if (minMs == maxMs) return minMs;

        // Логнормальный шум (Бокс–Мюллер): большинство пауз умеренные, изредка «задумывается» дольше
        double u1 = Math.Max(1e-9, rng.NextDouble());
        double u2 = rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        double logNormalFactor = Math.Exp(0.28 * z);

        double baseDelay = minMs + (maxMs - minMs) * Math.Clamp(complexity, 0.0, 1.0) * 0.72;
        int result = (int)Math.Round(baseDelay * logNormalFactor);
        return Math.Clamp(result, minMs, maxMs);
    }

    private static bool IsObviousCapture(string uci, char[,] grid)
    {
        if (!TryDecodeUci(uci, out int fromR, out int fromC, out int toR, out int toC))
            return false;
        char mover = grid[fromR, fromC];
        char target = grid[toR, toC];
        return mover != '1' && target != '1' && PieceValue(target) >= PieceValue(mover);
    }

    private static bool TryDecodeUci(string uci, out int fromR, out int fromC, out int toR, out int toC)
    {
        fromR = fromC = toR = toC = -1;
        if (string.IsNullOrEmpty(uci) || uci.Length < 4) return false;

        fromC = uci[0] - 'a';
        fromR = '8' - uci[1];
        toC = uci[2] - 'a';
        toR = '8' - uci[3];

        return fromR is >= 0 and < 8
            && fromC is >= 0 and < 8
            && toR is >= 0 and < 8
            && toC is >= 0 and < 8;
    }

    private static int PieceValue(char piece) => char.ToUpperInvariant(piece) switch
    {
        'P' => 100,
        'N' => 320,
        'B' => 330,
        'R' => 500,
        'Q' => 900,
        'K' => 10000,
        _ => 0
    };
}
