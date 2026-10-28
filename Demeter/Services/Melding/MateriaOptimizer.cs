using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Melding;

/// <summary>
/// The gatherer materia optimizer (pure — runs on a background task against an immutable
/// <see cref="MeldRequest"/>). Picks, per slot, the piece to wear and what goes in every socket
/// (overmelds included), and the food, so that Gathering, Perception and GP reach their targets as
/// cheaply and as reliably as the bags allow — and says why.
///
/// <para>
/// Shape ported from Daedalus (<c>ofnature/Daedalus</c>, <c>Daedalus/Services/Gear/MeldSweepOptimizer.cs</c>,
/// LGPL-3.0, same author): per-piece cap headroom above base + kept melds, materia values clipped at
/// the cap, whole-set totals scored against one model. What differs:
/// </para>
/// <list type="bullet">
/// <item>Ranking is threshold-driven, not Daedalus's DPS multiplier (<c>MeldDpsModel</c>): first reach
/// every target, food included; failing that, the class priority decides which targets come first
/// and how close the rest get; then the cheapest — fewest materia used on average (an overmeld at 10%
/// uses ten), then least lost to caps, then lower grades. Only materia the bags hold is used; what is
/// missing is reported against a second, unlimited plan.</item>
/// <item>Daedalus melds one grade into sweepable sockets and keeps XI overmelds as a fixed floor.
/// Gatherer gear mixes grades on purpose (XII where it is allowed, XI in the later overmelds, where
/// even grades never stick), so every socket is open here, and gear choice is part of the search.
/// That makes Daedalus's "compositions × greedy placement" inexact, so the search is a
/// branch-and-bound over slots instead: each slot's options (piece × socket layout, pruned to the
/// non-dominated ones) are tried best-bound first, and a node is cut when even an optimistic finish
/// cannot beat the best plan found. The optimistic finish uses per-stat suffix maxima, weighted
/// "socket budget" directions that catch targets reachable one at a time but not together, and for
/// the cost a per-stat min-cost table and a Lagrangian bound (stat prices, so the stats share one set
/// of sockets). Partial plans with the same clipped totals at the same depth are merged.</item>
/// <item>Unlimited materia is solved first, exactly (the full Lv100 sets in the tests prove optimal
/// in well under a second). If that plan fits the bags it is the answer. If not, the bags-only search
/// starts from it cut down to fit, and — being far wider, since every stack is tracked — runs on a
/// small budget with every pair of slots re-solved exactly before and after; it says when it is not
/// proven.</item>
/// </list>
///
/// <para>
/// Retrieved materia is counted as lost, and melds are kept as a prefix (retrieval works from the
/// last socket back) — both to be checked in game. Deterministic: the same request gives the same
/// plan, ties broken by input order.
/// </para>
/// </summary>
public static class MateriaOptimizer
{
    internal const double Eps = 1e-9;

    public static MeldPlan Optimize(MeldRequest request)
    {
        var options = request.Options ?? new MeldOptions();
        var priority = ClassPriority.Normalize(request.Priority, request.Job).ToArray();
        var materia = request.Materia
            .GroupBy(m => m.ItemId).Select(g => g.First())
            .OrderBy(m => m.Stat).ThenBy(m => m.Grade).ThenBy(m => m.ItemId)
            .ToArray();

        // Unlimited materia first: fewer options, so faster. If that plan only uses what the bags hold,
        // it is also the best the bags allow (it is the best of a larger set), and the stock-aware
        // search never has to run.
        var free = new Search(request, options, priority, materia, limitedStock: false);
        var unlimited = free.Run();
        Search.Result limited;
        if (unlimited.Found && free.FitsStock(unlimited.Choice))
        {
            limited = free.Polish(unlimited, withStock: true);
            unlimited = limited;
        }
        else
        {
            var bound = new Search(request, options, priority, materia, limitedStock: true);
            limited = bound.Polish(bound.Run(seed: unlimited), withStock: true);
            unlimited = free.Polish(unlimited, withStock: false);
        }

        // Only worth reporting when the bags are what holds the plan back.
        Search.Result? better = Search.CompareReach(unlimited.Key, limited.Key) > 0 ? unlimited : null;
        var purchases = better == null ? null : PlanBuilder.Build(request, options, priority, better, better, null);
        return PlanBuilder.Build(request, options, priority, limited, unlimited, purchases);
    }

    /// <summary>Materia to bring so one overmeld at <paramref name="percent"/> lands with probability <paramref name="confidence"/> (Teamcraft's formula).</summary>
    public static int AttemptsAtConfidence(int percent, double confidence)
    {
        if (percent >= 100)
            return 1;
        if (percent <= 0)
            return int.MaxValue;
        confidence = Math.Clamp(confidence, 0.0, 0.999999);
        return Math.Max(1, (int)Math.Ceiling(Math.Log(1 - confidence) / Math.Log(1 - percent / 100.0)));
    }

    /// <summary>One way to fill one slot: a piece, how many of its current melds stay, and what is melded after them.</summary>
    internal sealed class Option
    {
        public required GearCandidate Candidate { get; init; }
        public required int CandidateIndex { get; init; }
        public required int Kept { get; init; }
        public required int[] NewTypes { get; init; }
        public required int[] NewPercents { get; init; }

        /// <summary>Piece stats as worn, in priority order.</summary>
        public required int[] Stats { get; init; }
        public required double Cost { get; init; }
        public required double[] Usage { get; init; }
        public required int Waste { get; init; }
        public required int GradeSum { get; init; }
        public required int Retrievals { get; init; }
        public required int Unowned { get; init; }
        public required int Swap { get; init; }
    }

    /// <summary>
    /// The ranking, compared lexicographically: targets reached (in priority order), progress on the
    /// rest (in priority order), then expected materia, waste, food, grades, retrievals, unowned
    /// pieces, swaps. Higher flags/progress are better; everything after is better lower.
    /// </summary>
    internal struct Key
    {
        public int F0, F1, F2;
        public int P0, P1, P2;
        public double Cost;
        public int Waste, Food, Grade, Retrievals, Unowned, Swaps;

        public static Key Worst => new() { F0 = -1, F1 = -1, F2 = -1, P0 = int.MinValue, P1 = int.MinValue, P2 = int.MinValue, Cost = double.MaxValue, Waste = int.MaxValue };

        public int Flag(int i) => i == 0 ? F0 : i == 1 ? F1 : F2;
        public int Progress(int i) => i == 0 ? P0 : i == 1 ? P1 : P2;
    }

    internal sealed class Search
    {
        public sealed record Result(
            Key Key,
            Option[] Choice,
            GearSlot[] SearchSlots,
            FoodOption? Food,
            int[] Targets,
            int[] MaxAlone,
            long Nodes,
            bool BudgetHit,
            bool Found,
            int FoodPenalty,
            double? CostLowerBound)
        {
            public bool AllReached => Found && Key.F0 == 1 && Key.F1 == 1 && Key.F2 == 1;
        }

        private readonly MeldRequest _request;
        private readonly MeldOptions _options;
        private readonly GatherStat[] _order;
        private readonly MateriaGrade[] _materia;
        private readonly bool _limited;

        private readonly int[] _targets = new int[3];
        private readonly int[] _base = new int[3];
        private GearSlot[] _slots = [];
        private Option[][] _space = [];
        private bool[] _secondRing = [];

        // Suffix bounds, index k = from slot k to the end.
        private int[][] _maxRest = [];
        private double[][] _lambda = [];
        private int[][] _support = [];
        private double[][][] _ubs = []; // [k][direction][α]
        private double[][][] _nu = []; // [direction][α][materia type]: what each unit of stock is worth
        private double[] _room = [];
        private bool[] _binding = [];
        private int[][] _lo = [];
        private int[][] _hi = [];
        private double[][][] _dp = [];
        private bool _sumCostBound;

        // Lagrangian cost bound, per food: prices μ per stat point and, per suffix, Σ min(cost − μ·stats).
        private double[][] _mu = [];
        private double[][] _lag = [];

        // Current partial plan.
        private readonly int[] _cur = new int[3];
        private double _cost;
        private int _waste, _grade, _retrievals, _unowned, _swaps;
        private double[] _stock = [];
        private Option?[] _choice = [];
        private int[] _choiceIndex = [];

        private FoodOption? _food;
        private int _foodPenalty;
        private readonly int[] _req = new int[3];

        private Key _best = Key.Worst;
        private Option[]? _bestChoice;
        private FoodOption? _bestFood;
        private int _bestFoodPenalty;
        private long _nodes;
        private bool _budgetHit;

        private (Key Bound, int Index)[][] _children = [];

        // Transposition table: per (depth, totals clipped at what the targets need), the best partial
        // plans seen. Whatever finishes one finishes the other for the same price, so a partial plan
        // that is no better than one already explored can be dropped. Cleared per food.
        private readonly Dictionary<(int Depth, int G, int P, int Gp, int Ring), List<Seen>> _seen = new();

        private sealed record Seen(double Cost, int Waste, int Grade, int Retrievals, int Unowned, int Swaps, double[]? Stock);

        public long Budget => _limited ? _options.StockNodeBudget : _options.NodeBudget;

        public Search(MeldRequest request, MeldOptions options, GatherStat[] order, MateriaGrade[] materia, bool limitedStock)
        {
            _request = request;
            _options = options;
            _order = order;
            _materia = materia;
            _limited = limitedStock;
            for (var i = 0; i < 3; i++)
            {
                _targets[i] = Math.Max(0, request.Targets.Value(order[i]));
                _base[i] = request.CharacterBase[order[i]];
            }
        }

        /// <param name="seed">With limited stock: the unlimited plan, repaired to fit the bags and used as the plan to beat.</param>
        public Result Run(Result? seed = null)
        {
            BuildSpace();
            BuildBounds();

            _stock = new double[_materia.Length];
            for (var t = 0; t < _materia.Length; t++)
                _stock[t] = _limited ? (_request.Stock.TryGetValue(_materia[t].ItemId, out var n) ? Math.Max(0, n) : 0) : double.PositiveInfinity;
            _choice = new Option?[_slots.Length];
            _choiceIndex = new int[_slots.Length];
            _children = _space.Select(s => new (Key, int)[s.Length]).ToArray();
            Array.Copy(_base, _cur, 3);

            if (seed is { Found: true })
            {
                Seed(seed);
                ImprovePairs();
            }

            // Root bound for every food, then the most promising food first: a good plan found early
            // cuts the other foods' searches short. Ties keep the order of Foods() (no food first).
            var roots = new List<(FoodOption? Food, int Penalty, Key Root, int Order)>();
            foreach (var (food, penalty) in Foods())
            {
                SetFood(food, penalty);
                roots.Add((food, penalty, Bound(0), roots.Count));
            }

            foreach (var (food, penalty, root, _) in roots.OrderByDescending(r => r.Root, KeyOrder.Instance).ThenBy(r => r.Order))
            {
                if (Compare(root, _best) <= 0)
                    continue;
                SetFood(food, penalty);
                _seen.Clear();
                Dfs(0);
                if (_budgetHit)
                    break;
            }

            if (_budgetHit && _bestChoice != null)
                ImprovePairs();

            var maxAlone = new int[3];
            for (var i = 0; i < 3; i++)
                maxAlone[i] = _base[i] + (_maxRest.Length > 0 ? _maxRest[0][i] : 0);

            return new Result(_best, _bestChoice ?? [], _slots, _bestFood, (int[])_targets.Clone(), maxAlone, _nodes, _budgetHit,
                _bestChoice != null, _bestFoodPenalty, LowerBound(roots.Select(r => r.Root).ToList()));
        }

        /// <summary>
        /// The least expected materia any plan reaching what the best plan reaches could use: the best
        /// plan's own cost when the search finished, else the lowest root bound among the foods that
        /// could match its reach. Null when a cut-short search leaves even the reach unproven.
        /// </summary>
        private double? LowerBound(List<Key> roots)
        {
            if (_bestChoice == null)
                return null;
            if (!_budgetHit)
                return _best.Cost;
            if (roots.Any(r => CompareReach(r, _best) > 0))
                return null;
            return roots.Where(r => CompareReach(r, _best) == 0).Select(r => r.Cost).DefaultIfEmpty(_best.Cost).Min();
        }

        /// <summary>
        /// A starting plan for the stock-limited search, so it has something good to cut against from
        /// the first node: the unlimited plan with every stack brought within the bags (a lower grade of
        /// the same stat where one is left, else the dearest meld dropped), then improved one move at a
        /// time — add a meld, change one — while a move makes the plan better.
        /// </summary>
        private void Seed(Result unlimited)
        {
            var choice = new Option[_slots.Length];
            var rings = new Queue<Option>(Enumerable.Range(0, unlimited.Choice.Length)
                .Where(k => unlimited.SearchSlots[k] is GearSlot.RingRight or GearSlot.RingLeft)
                .Select(k => unlimited.Choice[k]));
            for (var k = 0; k < _slots.Length; k++)
            {
                Option? from = null;
                if (_slots[k] is GearSlot.RingRight or GearSlot.RingLeft)
                    from = rings.Count > 0 ? rings.Dequeue() : null;
                else
                {
                    var at = Array.IndexOf(unlimited.SearchSlots, _slots[k]);
                    from = at >= 0 ? unlimited.Choice[at] : null;
                }

                choice[k] = (from == null ? null : MakeOption(from.Candidate, from.CandidateIndex, from.Kept, from.NewTypes)) ?? _space[k][0];
            }

            var left = new double[_materia.Length];
            for (var t = 0; t < left.Length; t++)
                left[t] = _stock[t] - choice.Sum(o => o.Usage[t]);

            // Repair: the dearest meld of an overdrawn stack first.
            while (Enumerable.Range(0, left.Length).Any(t => left[t] < -Eps))
            {
                var (k, j) = Enumerable.Range(0, choice.Length)
                    .SelectMany(k => Enumerable.Range(0, choice[k].NewTypes.Length).Select(j => (k, j)))
                    .Where(x => left[choice[x.k].NewTypes[x.j]] < -Eps)
                    .OrderByDescending(x => 100.0 / choice[x.k].NewPercents[x.j])
                    .ThenByDescending(x => Array.IndexOf(_order, _materia[choice[x.k].NewTypes[x.j]].Stat))
                    .First();
                var o = choice[k];
                var stat = _materia[o.NewTypes[j]].Stat;
                Option? replacement = null;
                foreach (var t in Enumerable.Range(0, _materia.Length).Where(t => _materia[t].Stat == stat && t != o.NewTypes[j]).OrderByDescending(t => _materia[t].Value))
                {
                    var types = (int[])o.NewTypes.Clone();
                    types[j] = t;
                    var c = MakeOption(o.Candidate, o.CandidateIndex, o.Kept, types);
                    if (c != null && c.Usage[t] - o.Usage[t] <= left[t] + Eps)
                    {
                        replacement = c;
                        break;
                    }
                }

                replacement ??= MakeOption(o.Candidate, o.CandidateIndex, o.Kept, o.NewTypes.Where((_, i) => i != j).ToArray())!;
                for (var t = 0; t < left.Length; t++)
                    left[t] -= replacement.Usage[t] - o.Usage[t];
                choice[k] = replacement;
            }

            // Improve: best single move each round (add a meld at the end of a piece, or change one).
            var food = unlimited.Food;
            var penalty = unlimited.FoodPenalty;
            var key = KeyOf(choice, food, penalty);
            while (true)
            {
                Option? bestMove = null;
                var bestSlot = -1;
                var bestKey = key;
                for (var k = 0; k < choice.Length; k++)
                {
                    var o = choice[k];
                    foreach (var types in Moves(o))
                    {
                        var c = MakeOption(o.Candidate, o.CandidateIndex, o.Kept, types);
                        if (c == null)
                            continue;
                        var fits = true;
                        for (var t = 0; t < left.Length && fits; t++)
                            fits = c.Usage[t] - o.Usage[t] <= left[t] + Eps;
                        if (!fits)
                            continue;
                        choice[k] = c;
                        var next = KeyOf(choice, food, penalty);
                        choice[k] = o;
                        if (Compare(next, bestKey) > 0)
                        {
                            bestKey = next;
                            bestMove = c;
                            bestSlot = k;
                        }
                    }
                }

                if (bestMove == null)
                    break;
                for (var t = 0; t < left.Length; t++)
                    left[t] -= bestMove.Usage[t] - choice[bestSlot].Usage[t];
                choice[bestSlot] = bestMove;
                key = bestKey;
            }

            _best = key;
            _bestChoice = choice;
            _bestFood = food;
            _bestFoodPenalty = penalty;
        }

        /// <summary>
        /// After a cut-short search: re-solve every pair of slots exactly with the others held, and keep
        /// any pair that improves the plan, until a full pass changes nothing. Deterministic, and every
        /// result is at least "no two slots can be changed together for the better".
        /// </summary>
        private void ImprovePairs()
        {
            var choice = (Option[])_bestChoice!.Clone();
            var food = _bestFood;
            var penalty = _bestFoodPenalty;
            var key = KeyOf(choice, food, penalty);
            var held = new double[_materia.Length];
            for (var t = 0; t < held.Length; t++)
                held[t] = _limited ? (_request.Stock.TryGetValue(_materia[t].ItemId, out var n) ? Math.Max(0, n) : 0) : double.PositiveInfinity;

            for (var pass = 0; pass < 4; pass++)
            {
                var improved = false;
                for (var a = 0; a < choice.Length; a++)
                {
                    for (var b = a + 1; b < choice.Length; b++)
                    {
                        var left = (double[])held.Clone();
                        for (var k = 0; k < choice.Length; k++)
                        {
                            if (k == a || k == b)
                                continue;
                            for (var t = 0; t < left.Length; t++)
                                left[t] -= choice[k].Usage[t];
                        }

                        Option? bestA = null, bestB = null;
                        var bestKey = key;
                        var (oldA, oldB) = (choice[a], choice[b]);
                        foreach (var x in _space[a])
                        {
                            if (!FitsLeft(x, null, left))
                                continue;
                            foreach (var y in _space[b])
                            {
                                if (!FitsLeft(x, y, left) || !RingsCompatible(choice, a, x, b, y))
                                    continue;
                                choice[a] = x;
                                choice[b] = y;
                                var next = KeyOf(choice, food, penalty);
                                if (Compare(next, bestKey) > 0)
                                {
                                    bestKey = next;
                                    bestA = x;
                                    bestB = y;
                                }
                            }
                        }

                        choice[a] = bestA ?? oldA;
                        choice[b] = bestB ?? oldB;
                        if (bestA != null)
                        {
                            key = bestKey;
                            improved = true;
                        }
                    }
                }

                if (!improved)
                    break;
            }

            _best = key;
            _bestChoice = choice;
        }

        private static bool FitsLeft(Option x, Option? y, double[] left)
        {
            for (var t = 0; t < left.Length; t++)
            {
                if (x.Usage[t] + (y?.Usage[t] ?? 0) > left[t] + Eps)
                    return false;
            }

            return true;
        }

        /// <summary>The two ring slots, after changing slots a and b, still hold two wearable rings.</summary>
        private bool RingsCompatible(Option[] choice, int a, Option x, int b, Option y)
        {
            int r1 = -1, r2 = -1;
            for (var k = 0; k < _slots.Length; k++)
            {
                if (_slots[k] is not (GearSlot.RingRight or GearSlot.RingLeft))
                    continue;
                if (r1 < 0) r1 = k; else r2 = k;
            }

            if (r1 < 0 || r2 < 0 || (a != r1 && a != r2 && b != r1 && b != r2))
                return true;
            Option Pick(int k) => k == a ? x : k == b ? y : choice[k];
            var (o1, o2) = (Pick(r1), Pick(r2));
            var (c1, c2) = (o1.Candidate, o2.Candidate);
            if (c1.InstanceId != null && c1.InstanceId == c2.InstanceId)
                return false;
            if (o1.CandidateIndex == o2.CandidateIndex && c1.IsOwned)
                return false;
            return !(c1.ItemId == c2.ItemId && (c1.IsUnique || c2.IsUnique));
        }

        private IEnumerable<int[]> Moves(Option o)
        {
            if (o.Kept + o.NewTypes.Length < o.Candidate.TotalSockets)
            {
                for (var t = 0; t < _materia.Length; t++)
                    yield return [.. o.NewTypes, t];
            }

            for (var j = 0; j < o.NewTypes.Length; j++)
            {
                for (var t = 0; t < _materia.Length; t++)
                {
                    if (t == o.NewTypes[j])
                        continue;
                    var types = (int[])o.NewTypes.Clone();
                    types[j] = t;
                    yield return types;
                }
            }

            if (o.NewTypes.Length > 0)
                yield return o.NewTypes[..^1];
        }

        /// <summary>True when the plan's expected materia use fits in the bags, stack by stack.</summary>
        public bool FitsStock(Option[] choice)
        {
            for (var t = 0; t < _materia.Length; t++)
            {
                var used = choice.Sum(o => o.Usage[t]);
                var held = _request.Stock.TryGetValue(_materia[t].ItemId, out var n) ? Math.Max(0, n) : 0;
                if (used > held + Eps)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Tie-break polish after the search: wherever a lower grade of the same stat fits the same
        /// socket and the plan still reaches exactly as much, use it — less lost to caps, cheaper
        /// materia. The search leaves these out because they never change what a plan reaches or costs.
        /// With <paramref name="withStock"/>, only swaps the bags can pay for.
        /// </summary>
        public Result Polish(Result result, bool withStock)
        {
            if (!result.Found)
                return result;
            var choice = (Option[])result.Choice.Clone();
            var stock = new double[_materia.Length];
            for (var t = 0; t < _materia.Length; t++)
            {
                stock[t] = withStock
                    ? (_request.Stock.TryGetValue(_materia[t].ItemId, out var n) ? Math.Max(0, n) : 0) - choice.Sum(o => o.Usage[t])
                    : double.PositiveInfinity;
            }

            var key = KeyOf(choice, result.Food, result.FoodPenalty);
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var k = 0; k < choice.Length; k++)
                {
                    var o = choice[k];
                    for (var j = 0; j < o.NewTypes.Length; j++)
                    {
                        var from = _materia[o.NewTypes[j]];
                        // Lower grades of the same stat, best first.
                        foreach (var t in Enumerable.Range(0, _materia.Length)
                                     .Where(t => _materia[t].Stat == from.Stat && _materia[t].Value < from.Value)
                                     .OrderByDescending(t => _materia[t].Value))
                        {
                            var types = (int[])o.NewTypes.Clone();
                            types[j] = t;
                            var candidate = MakeOption(o.Candidate, o.CandidateIndex, o.Kept, types);
                            if (candidate == null)
                                continue;
                            var ok = true;
                            for (var u = 0; u < stock.Length && ok; u++)
                                ok = candidate.Usage[u] - o.Usage[u] <= stock[u] + Eps;
                            if (!ok)
                                continue;
                            choice[k] = candidate;
                            var next = KeyOf(choice, result.Food, result.FoodPenalty);
                            if (CompareReach(next, key) == 0 && Compare(next, key) > 0)
                            {
                                for (var u = 0; u < stock.Length; u++)
                                    stock[u] -= candidate.Usage[u] - o.Usage[u];
                                key = next;
                                o = candidate;
                                changed = true;
                                break;
                            }

                            choice[k] = o;
                        }
                    }
                }
            }

            return result with { Key = key, Choice = choice };
        }

        private Key KeyOf(Option[] choice, FoodOption? food, int foodPenalty)
        {
            var key = new Key { Food = foodPenalty };
            var totals = (int[])_base.Clone();
            foreach (var o in choice)
            {
                for (var i = 0; i < 3; i++)
                    totals[i] += o.Stats[i];
                key.Cost += o.Cost;
                key.Waste += o.Waste;
                key.Grade += o.GradeSum;
                key.Retrievals += o.Retrievals;
                key.Unowned += o.Unowned;
                key.Swaps += o.Swap;
            }

            for (var i = 0; i < 3; i++)
            {
                var total = totals[i] + (food?.Bonus(_order[i], totals[i]) ?? 0);
                var flag = total >= _targets[i] ? 1 : 0;
                var progress = Math.Min(_targets[i], total);
                if (i == 0) { key.F0 = flag; key.P0 = progress; }
                else if (i == 1) { key.F1 = flag; key.P1 = progress; }
                else { key.F2 = flag; key.P2 = progress; }
            }

            return key;
        }

        private void SetFood(FoodOption? food, int penalty)
        {
            _food = food;
            _foodPenalty = penalty;
            for (var i = 0; i < 3; i++)
                _req[i] = FoodOption.RequiredBefore(food, _order[i], _targets[i]);
            PreparePrices();
        }

        private sealed class KeyOrder : IComparer<Key>
        {
            public static readonly KeyOrder Instance = new();
            public int Compare(Key x, Key y) => Search.Compare(x, y);
        }

        /// <summary>No food first, then foods in the bags, then the rest, each group in input order.</summary>
        private IEnumerable<(FoodOption? Food, int Penalty)> Foods()
        {
            yield return (null, 0);
            foreach (var f in _request.Foods.Where(f => f.Owned > 0))
                yield return (f, 1);
            if (_options.AllowUnownedFood)
            {
                foreach (var f in _request.Foods.Where(f => f.Owned <= 0))
                    yield return (f, 2);
            }
        }

        // ── search space ──────────────────────────────────────────────────────────

        private void BuildSpace()
        {
            var bySlot = new SortedDictionary<GearSlot, List<(GearCandidate C, int Index)>>();
            for (var i = 0; i < _request.Candidates.Count; i++)
            {
                var c = _request.Candidates[i];
                if (!c.IsOwned && !_options.AllowUnownedGear)
                    continue;
                var slot = c.IsRing ? GearSlot.RingRight : c.Slot;
                if (!bySlot.TryGetValue(slot, out var list))
                    bySlot[slot] = list = [];
                list.Add((c, i));
            }

            var stock = new double[_materia.Length];
            for (var t = 0; t < _materia.Length; t++)
                stock[t] = _limited ? (_request.Stock.TryGetValue(_materia[t].ItemId, out var held) ? Math.Max(0, held) : 0) : double.PositiveInfinity;

            var raw = new List<(GearSlot Slot, List<Option> Options, bool Ring)>();
            foreach (var (slot, list) in bySlot)
            {
                var all = new List<Option>();
                foreach (var (c, index) in list)
                    all.AddRange(BuildOptions(c, index));
                // An option needing more of a stack than the bags hold can never be used.
                all.RemoveAll(o => Enumerable.Range(0, stock.Length).Any(t => o.Usage[t] > stock[t] + Eps));
                raw.Add((slot, all, slot == GearSlot.RingRight));
            }

            // A stack is only worth tracking per option if the whole set could run it out; the rest
            // never bind, and comparing on them would keep options that differ in nothing that matters.
            _binding = new bool[_materia.Length];
            for (var t = 0; t < _materia.Length; t++)
            {
                var most = raw.Sum(r => (r.Ring ? 2 : 1) * r.Options.Select(o => o.Usage[t]).DefaultIfEmpty(0).Max());
                _binding[t] = _limited && most > stock[t] + Eps;
            }

            var spaces = new List<(GearSlot Slot, Option[] Options, double Spread, bool Ring)>();
            foreach (var (slot, all, ring) in raw)
            {
                if (all.Count == 0)
                    continue;
                var pruned = Prune(all, _binding, perCandidate: ring);
                spaces.Add((slot, pruned.ToArray(), Spread(pruned), ring));
            }

            // Big decisions first: the slots whose options differ most, so good plans turn up early.
            var ordered = spaces.OrderByDescending(s => s.Spread).ThenBy(s => s.Slot).ToList();
            var slots = new List<GearSlot>();
            var space = new List<Option[]>();
            var second = new List<bool>();
            foreach (var s in ordered)
            {
                slots.Add(s.Slot);
                space.Add(s.Options);
                second.Add(false);
                if (s.Ring)
                {
                    // The two ring slots share one option list and sit side by side.
                    slots.Add(GearSlot.RingLeft);
                    space.Add(s.Options);
                    second.Add(true);
                }
            }

            _slots = slots.ToArray();
            _space = space.ToArray();
            _secondRing = second.ToArray();
        }

        private double Spread(List<Option> options)
        {
            var spread = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var weight = 1.0 / MaxValue(i);
                spread += (options.Max(o => o.Stats[i]) - options.Min(o => o.Stats[i])) * weight;
            }

            return spread;
        }

        private int MaxValue(int statIndex)
        {
            var best = 0;
            foreach (var m in _materia)
            {
                if (m.Stat == _order[statIndex])
                    best = Math.Max(best, m.Value);
            }

            return Math.Max(1, best);
        }

        /// <summary>
        /// Every way to fill the piece: keep the first <c>kept</c> current melds, then meld a multiset of
        /// materia into the next sockets, each arranged on the cheapest sockets it can stick in.
        /// </summary>
        private List<Option> BuildOptions(GearCandidate c, int candidateIndex)
        {
            var result = new List<Option>();
            var sockets = c.TotalSockets;
            var current = Math.Min(c.Melds.Count, sockets);
            var types = _materia.Length;

            var percent = new int[sockets, types];
            for (var pos = 0; pos < sockets; pos++)
            {
                for (var t = 0; t < types; t++)
                    percent[pos, t] = Percent(c, pos, t);
            }

            for (var kept = 0; kept <= current; kept++)
            {
                for (var n = 0; n <= sockets - kept; n++)
                {
                    foreach (var multiset in Multisets(types, n))
                    {
                        foreach (var (arrangement, _) in CheapestArrangements(multiset, kept, percent))
                        {
                            var option = MakeOption(c, candidateIndex, kept, arrangement);
                            if (option != null)
                                result.Add(option);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>Success percent of materia type <paramref name="t"/> in socket <paramref name="pos"/>; 0 when it cannot go there.</summary>
        private int Percent(GearCandidate c, int pos, int t)
        {
            var m = _materia[t];
            if (m.MinItemLevel > c.ItemLevel || pos >= c.TotalSockets)
                return 0;
            return pos < c.MateriaSlots ? 100 : _request.Rates.Percent(m.Grade, pos - c.MateriaSlots + 1, c.IsHq);
        }

        /// <summary>The piece with its first <paramref name="kept"/> melds kept and <paramref name="types"/> melded after them; null if one cannot stick.</summary>
        private Option? MakeOption(GearCandidate c, int candidateIndex, int kept, int[] types)
        {
            var meld = new int[3];
            for (var j = 0; j < kept; j++)
            {
                if (GatherStats.TryFromBaseParam(c.Melds[j].BaseParam, out var stat))
                    meld[Array.IndexOf(_order, stat)] += c.Melds[j].Value;
            }

            var usage = new double[_materia.Length];
            var pcts = new int[types.Length];
            var cost = 0.0;
            var grade = 0;
            for (var j = 0; j < types.Length; j++)
            {
                var t = types[j];
                pcts[j] = Percent(c, kept + j, t);
                if (pcts[j] <= 0)
                    return null;
                meld[Array.IndexOf(_order, _materia[t].Stat)] += _materia[t].Value;
                usage[t] += 100.0 / pcts[j];
                cost += 100.0 / pcts[j];
                grade += _materia[t].Grade;
            }

            var stats = new int[3];
            var waste = 0;
            for (var i = 0; i < 3; i++)
            {
                var headroom = c.Headroom(_order[i]);
                stats[i] = c.BaseStats[_order[i]] + Math.Min(headroom, meld[i]);
                waste += Math.Max(0, meld[i] - headroom);
            }

            return new Option
            {
                Candidate = c,
                CandidateIndex = candidateIndex,
                Kept = kept,
                NewTypes = types,
                NewPercents = pcts,
                Stats = stats,
                Cost = cost,
                Usage = usage,
                Waste = waste,
                GradeSum = grade,
                Retrievals = Math.Min(c.Melds.Count, c.TotalSockets) - kept,
                Unowned = c.IsOwned ? 0 : 1,
                Swap = c.IsEquipped ? 0 : 1,
            };
        }

        /// <summary>Non-decreasing type sequences of length <paramref name="n"/> (multisets), in lexicographic order.</summary>
        private static IEnumerable<int[]> Multisets(int types, int n)
        {
            if (n == 0)
            {
                yield return [];
                yield break;
            }

            if (types == 0)
                yield break;
            var current = new int[n];
            while (true)
            {
                yield return (int[])current.Clone();
                var i = n - 1;
                while (i >= 0 && current[i] == types - 1)
                    i--;
                if (i < 0)
                    yield break;
                current[i]++;
                for (var j = i + 1; j < n; j++)
                    current[j] = current[i];
            }
        }

        /// <summary>
        /// The arrangements of a multiset onto sockets <paramref name="start"/>.. with the lowest expected
        /// materia use (Σ 100 / success%). With limited stock every cheapest arrangement that spends a
        /// different mix is kept (a XII in the guaranteed socket and XI in the overmeld, or the other way
        /// round, cost the same but draw on different stacks); with unlimited stock one is enough.
        /// </summary>
        private List<(int[] Arrangement, double Cost)> CheapestArrangements(int[] multiset, int start, int[,] percent)
        {
            var result = new List<(int[], double)>();
            var best = double.MaxValue;
            var perm = (int[])multiset.Clone();
            do
            {
                var cost = 0.0;
                var ok = true;
                for (var j = 0; j < perm.Length; j++)
                {
                    var p = percent[start + j, perm[j]];
                    if (p <= 0)
                    {
                        ok = false;
                        break;
                    }

                    cost += 100.0 / p;
                }

                if (!ok)
                    continue;
                if (cost < best - Eps)
                {
                    best = cost;
                    result.Clear();
                    result.Add(((int[])perm.Clone(), cost));
                }
                else if (_limited && Math.Abs(cost - best) <= Eps)
                {
                    result.Add(((int[])perm.Clone(), cost));
                }
            }
            while (NextPermutation(perm));

            return result;
        }

        private static bool NextPermutation(int[] a)
        {
            var i = a.Length - 2;
            while (i >= 0 && a[i] >= a[i + 1])
                i--;
            if (i < 0)
                return false;
            var j = a.Length - 1;
            while (a[j] <= a[i])
                j--;
            (a[i], a[j]) = (a[j], a[i]);
            Array.Reverse(a, i + 1, a.Length - i - 1);
            return true;
        }

        /// <summary>
        /// Drops options another option beats or matches on everything that counts (stats, cost, waste,
        /// grades, retrievals, ownership, swap — and, with limited stock, materia drawn from each stack).
        /// Ring options are only compared within one piece: a unique ring can be worn once, so the other
        /// ring slot may need the option it would have pruned.
        /// </summary>
        private static List<Option> Prune(List<Option> options, bool[] usage, bool perCandidate)
        {
            var sorted = options
                .Select((o, i) => (o, i))
                .OrderBy(x => x.o.Cost).ThenBy(x => x.o.Waste).ThenBy(x => x.o.GradeSum).ThenBy(x => x.i)
                .Select(x => x.o)
                .ToList();
            var kept = new List<Option>();
            foreach (var o in sorted)
            {
                var dominated = false;
                foreach (var k in kept)
                {
                    if (Dominates(k, o, usage, perCandidate))
                    {
                        dominated = true;
                        break;
                    }
                }

                if (dominated)
                    continue;
                kept.RemoveAll(k => Dominates(o, k, usage, perCandidate) && !Dominates(k, o, usage, perCandidate));
                kept.Add(o);
            }

            // Back to input order (candidate, then enumeration), so ties resolve by what the caller listed first.
            var rank = new Dictionary<Option, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < options.Count; i++)
                rank[options[i]] = i;
            return kept.OrderBy(o => rank[o]).ToList();
        }

        private static bool Dominates(Option a, Option b, bool[] usage, bool perCandidate)
        {
            if (perCandidate && a.CandidateIndex != b.CandidateIndex)
                return false;
            for (var i = 0; i < 3; i++)
            {
                if (a.Stats[i] < b.Stats[i])
                    return false;
            }

            // Waste and grade are left out on purpose: they only break ties, and keeping every
            // XI-instead-of-XII variant would multiply the options. Polish puts them back.
            if (a.Cost > b.Cost + Eps
                || a.Retrievals > b.Retrievals || a.Unowned > b.Unowned || a.Swap > b.Swap)
                return false;
            for (var t = 0; t < a.Usage.Length; t++)
            {
                if (usage[t] && a.Usage[t] > b.Usage[t] + Eps)
                    return false;
            }

            return true;
        }

        // ── bounds ────────────────────────────────────────────────────────────────

        private void BuildBounds()
        {
            var n = _slots.Length;

            // Weighted directions: for every subset of stats, "points ÷ best materia value" (sockets'
            // worth) and plain points. A completion's λ·(final − now) can never exceed the best λ·stats
            // each remaining slot offers, so a target set needing more than that is out of reach.
            var lambdas = new List<double[]>();
            var supports = new List<int[]>();
            for (var mask = 1; mask < 8; mask++)
            {
                var members = Enumerable.Range(0, 3).Where(i => (mask & (1 << i)) != 0).ToArray();
                var bySockets = new double[3];
                foreach (var i in members)
                    bySockets[i] = 1.0 / MaxValue(i);
                lambdas.Add(bySockets);
                supports.Add(members);
                if (members.Length > 1)
                {
                    var plain = new double[3];
                    foreach (var i in members)
                        plain[i] = 1.0;
                    lambdas.Add(plain);
                    supports.Add(members);
                }
            }

            _lambda = lambdas.ToArray();
            _support = supports.ToArray();

            _maxRest = new int[n + 1][];
            _ubs = new double[n + 1][][];
            _lo = new int[n + 1][];
            _hi = new int[n + 1][];
            _dp = new double[n + 1][][];
            _maxRest[n] = new int[3];
            // Stock prices ν = α × (λ-points a unit of each materia buys). α = 0 is the plain socket
            // budget; α = 1 says melds can add no more than the stacks in the bags can pay for; between
            // them, mixes. Any ν ≥ 0 gives a valid bound, so the node takes the tightest. Only with
            // limited stock: unlimited stacks make every ν > 0 worthless.
            var alphas = _limited ? new[] { 0.0, 0.5, 0.8, 1.0 } : new[] { 0.0 };
            _nu = new double[_lambda.Length][][];
            for (var d = 0; d < _lambda.Length; d++)
            {
                _nu[d] = new double[alphas.Length][];
                for (var a = 0; a < alphas.Length; a++)
                {
                    _nu[d][a] = new double[_materia.Length];
                    for (var t = 0; t < _materia.Length; t++)
                        _nu[d][a][t] = alphas[a] * _lambda[d][Array.IndexOf(_order, _materia[t].Stat)] * _materia[t].Value;
                }
            }

            _room = new double[_lambda.Length];
            _ubs[n] = _lambda.Select(_ => new double[alphas.Length]).ToArray();
            _lo[n] = new int[3];
            _hi[n] = new int[3];
            _dp[n] = [[0.0], [0.0], [0.0]];

            for (var k = n - 1; k >= 0; k--)
            {
                var options = _space[k];
                _maxRest[k] = new int[3];
                _lo[k] = new int[3];
                _hi[k] = new int[3];
                _dp[k] = new double[3][];
                for (var i = 0; i < 3; i++)
                {
                    _maxRest[k][i] = _maxRest[k + 1][i] + options.Max(o => o.Stats[i]);
                    _lo[k][i] = _lo[k + 1][i] + options.Min(o => o.Stats[i]);
                    _hi[k][i] = _maxRest[k][i];
                    _dp[k][i] = CostTable(k, i, options);
                }

                _ubs[k] = new double[_lambda.Length][];
                for (var d = 0; d < _lambda.Length; d++)
                {
                    _ubs[k][d] = new double[alphas.Length];
                    for (var a = 0; a < alphas.Length; a++)
                    {
                        var nu = _nu[d][a];
                        var best = double.MinValue;
                        foreach (var o in options)
                        {
                            var v = _lambda[d][0] * o.Stats[0] + _lambda[d][1] * o.Stats[1] + _lambda[d][2] * o.Stats[2];
                            for (var t = 0; t < nu.Length; t++)
                                v -= nu[t] * o.Usage[t];
                            best = Math.Max(best, v);
                        }

                        _ubs[k][d][a] = _ubs[k + 1][d][a] + best;
                    }
                }
            }

            // Summing the per-stat cost bounds is only safe when a materia never sticks better in a later
            // overmeld than an earlier one (true of the sheet); otherwise fall back to the largest.
            var maxGrade = _materia.Length == 0 ? 0 : _materia.Max(m => m.Grade);
            _sumCostBound = _request.Rates.IsNonIncreasing(Math.Max(1, maxGrade));
        }

        /// <summary>
        /// Min expected materia for slots k.. to add up to at least x of one stat, every slot free to pick
        /// its cheapest option for that stat alone. Indexed from <c>_lo[k][i]</c>.
        /// </summary>
        private double[] CostTable(int k, int i, Option[] options)
        {
            // Per slot, the frontier of (stat, cost): cheapest cost for each reachable stat level.
            var frontier = new List<(int Stat, double Cost)>();
            foreach (var o in options.OrderBy(o => o.Cost).ThenByDescending(o => o.Stats[i]))
            {
                if (frontier.Count == 0 || o.Stats[i] > frontier[^1].Stat)
                    frontier.Add((o.Stats[i], o.Cost));
            }

            var lo = _lo[k][i];
            var hi = _hi[k][i];
            var table = new double[hi - lo + 1];
            for (var x = lo; x <= hi; x++)
            {
                var best = double.PositiveInfinity;
                foreach (var (stat, cost) in frontier)
                {
                    var rest = Lookup(k + 1, i, x - stat);
                    if (cost + rest < best)
                        best = cost + rest;
                }

                table[x - lo] = best;
            }

            return table;
        }

        private double Lookup(int k, int i, int x)
        {
            if (x <= _lo[k][i])
                return _dp[k][i][0];
            if (x > _hi[k][i])
                return double.PositiveInfinity;
            return _dp[k][i][x - _lo[k][i]];
        }

        /// <summary>The best any completion of the current partial plan (slots 0..k-1 chosen) could score.</summary>
        private Key Bound(int k)
        {
            ComputeRoom(k);
            var mask = 0;
            for (var v = 7; v >= 0; v--)
            {
                // v's bits from the top are the priority flags: 0b100 = top priority stat reached.
                var m = ((v >> 2) & 1) | (((v >> 1) & 1) << 1) | ((v & 1) << 2);
                if (Feasible(k, m))
                {
                    mask = m;
                    break;
                }
            }

            var key = new Key
            {
                F0 = mask & 1,
                F1 = (mask >> 1) & 1,
                F2 = (mask >> 2) & 1,
                Waste = _waste,
                Food = _foodPenalty,
                Grade = _grade,
                Retrievals = _retrievals,
                Unowned = _unowned,
                Swaps = _swaps,
            };

            var need = new int[3];
            for (var i = 0; i < 3; i++)
            {
                int progress;
                if ((mask & (1 << i)) != 0)
                {
                    progress = _targets[i];
                    need[i] = _req[i];
                }
                else
                {
                    var gear = _cur[i] + _maxRest[k][i];
                    for (var d = 0; d < _lambda.Length; d++)
                    {
                        var lambda = _lambda[d];
                        if (lambda[i] <= 0 || !SupportWithin(d, mask | (1 << i)))
                            continue;
                        var room = _room[d];
                        foreach (var s in _support[d])
                        {
                            if (s != i)
                                room -= lambda[s] * Math.Max(0, _req[s] - _cur[s]);
                        }

                        gear = Math.Min(gear, _cur[i] + (int)Math.Floor(room / lambda[i] + Eps));
                    }

                    var total = gear + (_food?.Bonus(_order[i], gear) ?? 0);
                    progress = Math.Min(_targets[i], total);
                    need[i] = FoodOption.RequiredBefore(_food, _order[i], progress);
                }

                if (i == 0) key.P0 = progress;
                else if (i == 1) key.P1 = progress;
                else key.P2 = progress;
            }

            var lb = 0.0;
            for (var i = 0; i < 3; i++)
            {
                var c = Lookup(k, i, need[i] - _cur[i]);
                if (double.IsPositiveInfinity(c))
                    c = 0; // Only reachable through rounding at the edge; never let the bound overshoot.
                lb = _sumCostBound ? lb + c : Math.Max(lb, c);
            }

            key.Cost = _cost + lb;
            if (Compare(key, _best) <= 0)
                return key; // already beaten; the dearer bound below cannot change that

            // Lagrangian bound: for prices μ ≥ 0, any finish costs at least Σ_slots min(cost − μ·stats)
            // + μ·(still needed). It sees that the stats share one set of sockets, which the per-stat
            // tables above do not, so it is the one that cuts when every target asks for the same slots.
            var lag = _lag[k];
            for (var j = 0; j < _mu.Length; j++)
            {
                var mu = _mu[j];
                var v = lag[j] + mu[0] * (need[0] - _cur[0]) + mu[1] * (need[1] - _cur[1]) + mu[2] * (need[2] - _cur[2]);
                if (v > lb)
                    lb = v;
            }

            key.Cost = _cost + lb;
            return key;
        }

        /// <summary>
        /// Picks the price vectors for the Lagrangian bound for the current food: the best vector for
        /// the whole problem over a grid of natural prices (expected materia per stat point for each
        /// materia and socket rate), then a spread of scalings of it for the nodes below, whose
        /// remaining needs are smaller.
        /// </summary>
        private void PreparePrices()
        {
            var n = _slots.Length;
            var prices = new List<double>[3];
            for (var i = 0; i < 3; i++)
            {
                var set = new SortedSet<double> { 0.0 };
                foreach (var m in _materia)
                {
                    if (m.Stat != _order[i])
                        continue;
                    foreach (var pct in new[] { 100, 17, 12, 10, 7, 6, 5, 3, 2 })
                        set.Add(Math.Round(100.0 / pct / m.Value, 4));
                }

                prices[i] = set.ToList();
            }

            var need = new double[3];
            for (var i = 0; i < 3; i++)
                need[i] = _req[i] - _base[i];

            var best = new double[3];
            var bestValue = double.NegativeInfinity;
            foreach (var a in prices[0])
            foreach (var b in prices[1])
            foreach (var c in prices[2])
            {
                double[] mu = [a, b, c];
                var v = SuffixMin(0, mu) + mu[0] * need[0] + mu[1] * need[1] + mu[2] * need[2];
                if (v > bestValue + Eps)
                {
                    bestValue = v;
                    best = mu;
                }
            }
            var factors = _limited ? new[] { 0.0, 0.5, 1.0 } : new[] { 0.0, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5 };
            var grid = new List<double[]>();
            foreach (var fa in factors)
            foreach (var fb in factors)
            foreach (var fc in factors)
                grid.Add([best[0] * fa, best[1] * fb, best[2] * fc]);
            _mu = grid.ToArray();

            _lag = new double[n + 1][];
            _lag[n] = new double[_mu.Length];
            for (var k = n - 1; k >= 0; k--)
            {
                _lag[k] = new double[_mu.Length];
                for (var j = 0; j < _mu.Length; j++)
                    _lag[k][j] = _lag[k + 1][j] + SlotMin(k, _mu[j]);
            }
        }

        private double SuffixMin(int k, double[] mu)
        {
            var sum = 0.0;
            for (var s = k; s < _slots.Length; s++)
                sum += SlotMin(s, mu);
            return sum;
        }

        private double SlotMin(int k, double[] mu)
        {
            var best = double.PositiveInfinity;
            foreach (var o in _space[k])
            {
                var v = o.Cost - mu[0] * o.Stats[0] - mu[1] * o.Stats[1] - mu[2] * o.Stats[2];
                if (v < best)
                    best = v;
            }

            return best;
        }

        /// <summary>
        /// Per direction, the most λ·stats slots k.. can still add: the tightest over the stock prices of
        /// Σ max(λ·stats − ν·usage) + ν·(stock left).
        /// </summary>
        private void ComputeRoom(int k)
        {
            for (var d = 0; d < _lambda.Length; d++)
            {
                var ub = _ubs[k][d];
                var best = ub[0];
                for (var a = 1; a < ub.Length; a++)
                {
                    var nu = _nu[d][a];
                    var v = ub[a];
                    for (var t = 0; t < nu.Length; t++)
                        v += nu[t] * _stock[t];
                    if (v < best)
                        best = v;
                }

                _room[d] = best;
            }
        }

        private bool SupportWithin(int d, int mask)
        {
            foreach (var s in _support[d])
            {
                if ((mask & (1 << s)) == 0)
                    return false;
            }

            return true;
        }

        private bool Feasible(int k, int mask)
        {
            for (var i = 0; i < 3; i++)
            {
                if ((mask & (1 << i)) != 0 && _cur[i] + _maxRest[k][i] < _req[i])
                    return false;
            }

            for (var d = 0; d < _lambda.Length; d++)
            {
                if (!SupportWithin(d, mask))
                    continue;
                var needed = 0.0;
                foreach (var s in _support[d])
                    needed += _lambda[d][s] * Math.Max(0, _req[s] - _cur[s]);
                if (needed > _room[d] + Eps)
                    return false;
            }

            return true;
        }

        private Key Leaf()
        {
            var key = new Key
            {
                Cost = _cost,
                Waste = _waste,
                Food = _foodPenalty,
                Grade = _grade,
                Retrievals = _retrievals,
                Unowned = _unowned,
                Swaps = _swaps,
            };
            for (var i = 0; i < 3; i++)
            {
                var total = _cur[i] + (_food?.Bonus(_order[i], _cur[i]) ?? 0);
                var flag = total >= _targets[i] ? 1 : 0;
                var progress = Math.Min(_targets[i], total);
                if (i == 0) { key.F0 = flag; key.P0 = progress; }
                else if (i == 1) { key.F1 = flag; key.P1 = progress; }
                else { key.F2 = flag; key.P2 = progress; }
            }

            return key;
        }

        internal static int Compare(in Key a, in Key b)
        {
            var c = CompareReach(a, b);
            if (c != 0)
                return c;
            if (Math.Abs(a.Cost - b.Cost) > 1e-7)
                return a.Cost < b.Cost ? 1 : -1;
            if (a.Waste != b.Waste) return a.Waste < b.Waste ? 1 : -1;
            if (a.Food != b.Food) return a.Food < b.Food ? 1 : -1;
            if (a.Grade != b.Grade) return a.Grade < b.Grade ? 1 : -1;
            if (a.Retrievals != b.Retrievals) return a.Retrievals < b.Retrievals ? 1 : -1;
            if (a.Unowned != b.Unowned) return a.Unowned < b.Unowned ? 1 : -1;
            if (a.Swaps != b.Swaps) return a.Swaps < b.Swaps ? 1 : -1;
            return 0;
        }

        /// <summary>Targets reached, then progress on the rest — the part of the ranking that is about stats, not cost.</summary>
        internal static int CompareReach(in Key a, in Key b)
        {
            for (var i = 0; i < 3; i++)
            {
                if (a.Flag(i) != b.Flag(i))
                    return a.Flag(i) > b.Flag(i) ? 1 : -1;
            }

            for (var i = 0; i < 3; i++)
            {
                if (a.Progress(i) != b.Progress(i))
                    return a.Progress(i) > b.Progress(i) ? 1 : -1;
            }

            return 0;
        }

        // ── depth-first search ────────────────────────────────────────────────────

        private void Dfs(int k)
        {
            if (++_nodes > Budget)
            {
                _budgetHit = true;
                return;
            }

            if (k == _slots.Length)
            {
                var leaf = Leaf();
                if (Compare(leaf, _best) > 0)
                {
                    _best = leaf;
                    _bestChoice = _choice.Select(o => o!).ToArray();
                    _bestFood = _food;
                    _bestFoodPenalty = _foodPenalty;
                }

                return;
            }

            if (k > 0 && !FirstVisit(k))
                return;

            var options = _space[k];
            var children = _children[k];
            var count = 0;
            for (var idx = 0; idx < options.Length; idx++)
            {
                var o = options[idx];
                if (!Allowed(k, idx, o))
                    continue;
                Apply(o, +1);
                var bound = Bound(k + 1);
                Apply(o, -1);
                if (Compare(bound, _best) > 0)
                    children[count++] = (bound, idx);
            }

            // Best bound first (stable on input order), and stop at the first child that cannot win.
            Array.Sort(children, 0, count, ChildOrder.Instance);
            for (var c = 0; c < count; c++)
            {
                var (bound, idx) = children[c];
                if (Compare(bound, _best) <= 0)
                    break;
                var o = options[idx];
                Apply(o, +1);
                _choice[k] = o;
                _choiceIndex[k] = idx;
                Dfs(k + 1);
                _choice[k] = null;
                Apply(o, -1);
                if (_budgetHit)
                    return;
            }
        }

        /// <summary>
        /// Records the current partial plan at depth <paramref name="k"/>; false when an explored one with
        /// the same clipped totals was at least as good on every additive part of the ranking (and, with
        /// limited stock, left at least as much of every stack).
        /// </summary>
        private bool FirstVisit(int k)
        {
            // The ring pair is not independent: the second ring's choices depend on the first's.
            var ring = _secondRing[k] ? _choiceIndex[k - 1] : -1;
            var key = (k, Math.Min(_cur[0], _req[0]), Math.Min(_cur[1], _req[1]), Math.Min(_cur[2], _req[2]), ring);
            if (!_seen.TryGetValue(key, out var list))
            {
                _seen[key] = list = [];
            }
            else
            {
                foreach (var s in list)
                {
                    if (SeenCovers(s))
                        return false;
                }

                list.RemoveAll(CoveredByCurrent);
            }

            list.Add(new Seen(_cost, _waste, _grade, _retrievals, _unowned, _swaps, _limited ? (double[])_stock.Clone() : null));
            return true;
        }

        /// <summary>The earlier partial plan is at least as good: its ranking tail is no worse lexicographically, and it kept no less stock.</summary>
        private bool SeenCovers(Seen s)
        {
            if (CompareTail(s.Cost, s.Waste, s.Grade, s.Retrievals, s.Unowned, s.Swaps) < 0)
                return false;
            if (s.Stock != null)
            {
                for (var t = 0; t < _stock.Length; t++)
                {
                    if (s.Stock[t] < _stock[t] - Eps)
                        return false;
                }
            }

            return true;
        }

        private bool CoveredByCurrent(Seen s)
        {
            if (CompareTail(s.Cost, s.Waste, s.Grade, s.Retrievals, s.Unowned, s.Swaps) <= 0)
                return false;
            if (s.Stock != null)
            {
                for (var t = 0; t < _stock.Length; t++)
                {
                    if (_stock[t] < s.Stock[t] - Eps)
                        return false;
                }
            }

            return true;
        }

        /// <summary>&gt; 0 when (cost, waste, grade, retrievals, unowned, swaps) is better than the current partial plan's.</summary>
        private int CompareTail(double cost, int waste, int grade, int retrievals, int unowned, int swaps)
        {
            if (Math.Abs(cost - _cost) > 1e-7) return cost < _cost ? 1 : -1;
            if (waste != _waste) return waste < _waste ? 1 : -1;
            if (grade != _grade) return grade < _grade ? 1 : -1;
            if (retrievals != _retrievals) return retrievals < _retrievals ? 1 : -1;
            if (unowned != _unowned) return unowned < _unowned ? 1 : -1;
            if (swaps != _swaps) return swaps < _swaps ? 1 : -1;
            return 0;
        }

        private bool Allowed(int k, int idx, Option o)
        {
            if (_limited)
            {
                for (var t = 0; t < o.Usage.Length; t++)
                {
                    if (o.Usage[t] > _stock[t] + Eps)
                        return false;
                }
            }

            if (_secondRing[k])
            {
                // Rings A+B and B+A are the same plan: only take the second at or after the first.
                if (idx < _choiceIndex[k - 1])
                    return false;
                var firstOption = _choice[k - 1]!;
                var first = firstOption.Candidate;
                var c = o.Candidate;
                if (first.InstanceId != null && first.InstanceId == c.InstanceId)
                    return false;
                // An owned candidate is one copy, id or not; a sheet ring can be bought twice.
                if (firstOption.CandidateIndex == o.CandidateIndex && c.IsOwned)
                    return false;
                if (first.ItemId == c.ItemId && (first.IsUnique || c.IsUnique))
                    return false;
            }

            return true;
        }

        private void Apply(Option o, int sign)
        {
            for (var i = 0; i < 3; i++)
                _cur[i] += sign * o.Stats[i];
            _cost += sign * o.Cost;
            _waste += sign * o.Waste;
            _grade += sign * o.GradeSum;
            _retrievals += sign * o.Retrievals;
            _unowned += sign * o.Unowned;
            _swaps += sign * o.Swap;
            if (_limited)
            {
                for (var t = 0; t < o.Usage.Length; t++)
                    _stock[t] -= sign * o.Usage[t];
            }
        }

        private sealed class ChildOrder : IComparer<(Key Bound, int Index)>
        {
            public static readonly ChildOrder Instance = new();

            public int Compare((Key Bound, int Index) x, (Key Bound, int Index) y)
            {
                var c = Search.Compare(y.Bound, x.Bound);
                return c != 0 ? c : x.Index.CompareTo(y.Index);
            }
        }
    }
}
