namespace PoLocalCompare.Api.Features.Leaderboard;

/// <summary>
/// Order-independent strength estimate with an uncertainty band, fitted over every judged duel.
/// </summary>
/// <remarks>
/// <para>
/// The leaderboard's ELO is sequential: the same set of results replayed in a different order
/// lands on different numbers, and it carries no notion of how much evidence sits behind a
/// rating — a model with three duels and one with three hundred look equally certain. This is
/// the Chatbot-Arena answer to both: a Bradley–Terry model (P(i beats j) = σ(θᵢ − θⱼ)) fitted
/// by maximum a-posteriori over the whole history at once, with standard errors read off the
/// curvature of the log-posterior at the optimum.
/// </para>
/// <para>
/// A Gaussian prior (mean = starting rating, <see cref="PriorSdElo"/> wide) does two jobs. It
/// keeps a model that has only ever won finite instead of running off to infinity, and it
/// makes a model with no games at all report at least the prior's width — "we know nothing" —
/// rather than a spuriously tight band. Draws count as half a win each way, the same
/// convention <see cref="EloCalculator"/> uses.
/// </para>
/// <para>
/// This is a read-side projection. It never writes, and it deliberately does not replace
/// <c>CurrentElo</c> as the ranking: ELO is what <see cref="RecordVerdictHandler"/>
/// moves and what every stored row carries. The band is shown beside it.
/// </para>
/// </remarks>
public static class BradleyTerry
{
    /// <summary>Prior standard deviation on the Elo scale. Wide on purpose: it is a guard, not an opinion.</summary>
    public const double PriorSdElo = 400;

    /// <summary>Fewer judged games than this and a rating is flagged provisional.</summary>
    public const int ProvisionalGames = 10;

    /// <summary>One nat of log-odds expressed in Elo points (400 / ln 10).</summary>
    private static readonly double EloPerNat = 400 / Math.Log(10);

    /// <summary>One judged duel. <paramref name="ScoreA"/> is 1 for an A win, 0 for a loss, 0.5 for a draw.</summary>
    public readonly record struct Game(ModelId A, ModelId B, double ScoreA);

    /// <summary>Fitted strength on the Elo scale, the half-width of its 95% interval, and the evidence behind it.</summary>
    public sealed record Estimate(double Rating, double Interval95, int Games)
    {
        public bool IsProvisional => Games < ProvisionalGames;
    }

    /// <summary>
    /// One game per duel from the per-model history rows. Each duel is written twice — once
    /// into each model's partition — so the rows are de-duplicated on the duel id; reading
    /// both halves would count every result twice and halve every interval.
    /// </summary>
    public static List<Game> GamesFrom(IEnumerable<EloRecord> history)
    {
        var seen = new HashSet<DuelId>();
        var games = new List<Game>();
        foreach (var row in history)
        {
            double? score = row.Outcome.ToUpperInvariant() switch
            {
                "WIN" => 1.0,
                "LOSS" => 0.0,
                "DRAW" => 0.5,
                _ => null,
            };
            if (score is null || row.ModelId == row.OpponentModelId) continue;
            if (!seen.Add(row.DuelId)) continue;
            games.Add(new Game(row.ModelId, row.OpponentModelId, score.Value));
        }
        return games;
    }

    /// <summary>
    /// Fits every model in <paramref name="models"/> plus anyone who appears in a game (a
    /// retired opponent is still evidence about the models it played). Returns an estimate for
    /// each of them; a model with no games gets the prior.
    /// </summary>
    public static Dictionary<ModelId, Estimate> Fit(
        IEnumerable<ModelId> models,
        IReadOnlyList<Game> games,
        double anchor = 1200)
    {
        var index = new Dictionary<ModelId, int>();
        foreach (var id in models.Concat(games.SelectMany(g => new[] { g.A, g.B })))
            index.TryAdd(id, index.Count);

        var n = index.Count;
        if (n == 0) return [];

        var pairs = games.Select(g => (A: index[g.A], B: index[g.B], g.ScoreA)).ToArray();
        var counts = new int[n];
        foreach (var (a, b, _) in pairs) { counts[a]++; counts[b]++; }

        var priorPrecision = Math.Pow(EloPerNat / PriorSdElo, 2); // 1/σ² in nats
        var theta = new double[n];
        double[,] information = new double[n, n];

        // Newton–Raphson on a strictly concave objective (the prior guarantees it), so this
        // converges in a handful of steps. The step cap is a guard for the first iteration on
        // a lopsided record, not something a normal fit ever hits.
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var gradient = new double[n];
            information = new double[n, n];
            for (var i = 0; i < n; i++)
            {
                gradient[i] = -theta[i] * priorPrecision;
                information[i, i] = priorPrecision;
            }

            foreach (var (a, b, score) in pairs)
            {
                var p = 1.0 / (1.0 + Math.Exp(theta[b] - theta[a]));
                var w = p * (1 - p);
                gradient[a] += score - p;
                gradient[b] -= score - p;
                information[a, a] += w;
                information[b, b] += w;
                information[a, b] -= w;
                information[b, a] -= w;
            }

            var step = CholeskySolve(information, gradient);
            var largest = 0.0;
            for (var i = 0; i < n; i++)
            {
                var delta = Math.Clamp(step[i], -2, 2);
                theta[i] += delta;
                largest = Math.Max(largest, Math.Abs(delta));
            }
            if (largest < 1e-9) break;
        }

        // Bradley–Terry only identifies differences: adding a constant to every θ fits the
        // data exactly as well. The raw per-model variance therefore carries the prior's
        // uncertainty about where the whole scale sits, which no number of games can shrink —
        // a model with 300 duels would still show a band of ±hundreds. What the band should
        // express is how well a model is placed against the field, so each variance is taken
        // relative to the mean of the models that have actually played (weights w):
        //   Var(θᵢ − Σⱼ wⱼθⱼ) = Cᵢᵢ − 2·Σⱼ wⱼCᵢⱼ + Σⱼₖ wⱼwₖCⱼₖ.
        // Unplayed models are left out of that mean on purpose; their prior-sized uncertainty
        // would otherwise leak into every other model's band.
        var covariance = CholeskyInverse(information);
        var played = counts.Count(c => c > 0);
        var weights = counts.Select(c => played == 0 ? 1.0 / n : c > 0 ? 1.0 / played : 0).ToArray();
        var weightedRows = new double[n];
        var weightedTotal = 0.0;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) weightedRows[i] += weights[j] * covariance[i, j];
            weightedTotal += weights[i] * weightedRows[i];
        }

        return index.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var i = kv.Value;
                var variance = Math.Max(0, covariance[i, i] - 2 * weightedRows[i] + weightedTotal);
                return new Estimate(
                    Rating: Math.Round(anchor + theta[i] * EloPerNat, 1),
                    Interval95: Math.Round(1.96 * Math.Sqrt(variance) * EloPerNat, 1),
                    Games: counts[i]);
            });
    }

    private static double[,] Cholesky(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var lower = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                var sum = matrix[i, j];
                for (var k = 0; k < j; k++) sum -= lower[i, k] * lower[j, k];
                lower[i, j] = i == j ? Math.Sqrt(sum) : sum / lower[j, j];
            }
        }
        return lower;
    }

    private static double[] CholeskySolve(double[,] matrix, double[] rhs) =>
        SolveWith(Cholesky(matrix), rhs);

    private static double[] SolveWith(double[,] lower, double[] rhs)
    {
        var n = rhs.Length;
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sum = rhs[i];
            for (var k = 0; k < i; k++) sum -= lower[i, k] * y[k];
            y[i] = sum / lower[i, i];
        }
        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var sum = y[i];
            for (var k = i + 1; k < n; k++) sum -= lower[k, i] * x[k];
            x[i] = sum / lower[i, i];
        }
        return x;
    }

    /// <summary>Full inverse — the posterior covariance. The roster is tens of models, so n³ is nothing.</summary>
    private static double[,] CholeskyInverse(double[,] matrix)
    {
        var n = matrix.GetLength(0);
        var lower = Cholesky(matrix);
        var inverse = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            var unit = new double[n];
            unit[i] = 1;
            var column = SolveWith(lower, unit);
            for (var j = 0; j < n; j++) inverse[j, i] = column[j];
        }
        return inverse;
    }
}
