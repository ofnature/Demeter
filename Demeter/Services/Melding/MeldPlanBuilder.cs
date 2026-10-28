using System;
using System.Collections.Generic;
using System.Linq;
using static Demeter.Services.Melding.MateriaOptimizer;

namespace Demeter.Services.Melding;

/// <summary>Turns a search result into the <see cref="MeldPlan"/> the adapter and the window read, with the reasons.</summary>
internal static class PlanBuilder
{
    /// <param name="reference">The unlimited-materia result: what the gear can reach at all, for the "tops out at" reasons.</param>
    /// <param name="purchases">The unlimited plan when it reaches more than the bags allow; its materia are the ones to acquire.</param>
    public static MeldPlan Build(
        MeldRequest request,
        MeldOptions options,
        GatherStat[] priority,
        Search.Result result,
        Search.Result reference,
        MeldPlan? purchases)
    {
        var materia = request.Materia
            .GroupBy(m => m.ItemId).Select(g => g.First())
            .OrderBy(m => m.Stat).ThenBy(m => m.Grade).ThenBy(m => m.ItemId)
            .ToArray();

        var placed = Place(result);
        var slots = new List<SlotPlan>();
        foreach (var (slot, option) in placed.OrderBy(p => p.Slot))
            slots.Add(SlotFor(request, options, materia, slot, option));

        var gear = request.CharacterBase;
        foreach (var s in slots)
            gear += s.Stats;
        var food = result.Food;
        var withFood = food?.Apply(gear) ?? gear;

        var needs = Needs(request, materia, slots);
        var toAcquire = purchases == null
            ? (IReadOnlyList<MateriaNeed>)[]
            : purchases.Materia.Where(m => m.ToBuy > 0 || m.ToBuyAtConfidence > 0).ToList();

        var stats = new List<StatOutcome>();
        foreach (var stat in priority)
            stats.Add(Outcome(request, priority, stat, gear, withFood, food, reference, purchases, toAcquire));

        var notes = new List<string>();
        if (result.BudgetHit)
        {
            notes.Add(result.CostLowerBound is { } bound
                ? $"The search stopped at its budget of {result.Nodes - 1:N0} nodes. What this plan reaches is the best possible; its {result.Key.Cost:0.#} expected materia is at most {result.Key.Cost - bound:0.#} above the least any such plan could use ({bound:0.#})."
                : $"The search stopped at its budget of {result.Nodes - 1:N0} nodes: this is the best plan it found (no two slots can be changed together for the better), not proven the best.");
        }
        notes.AddRange(StockNotes(request, materia, needs));
        if (purchases != null)
        {
            var reach = string.Join(", ", purchases.Stats.Select(s => $"{GatherStats.Name(s.Stat)} {s.Total}{(s.Target == null ? "" : s.Reached ? " ✓" : $" (−{s.ShortBy})")}"));
            notes.Add($"With the materia under ToAcquire as well: {reach}.");
        }

        return new MeldPlan(
            request.Job,
            priority,
            slots,
            food,
            FoodWhy(request, food, gear),
            stats,
            needs,
            toAcquire,
            purchases,
            Math.Round(slots.Sum(s => s.Sockets.Sum(k => k.Action is SocketAction.Meld or SocketAction.RetrieveThenMeld ? k.ExpectedMateria : 0)), 6),
            result.CostLowerBound is { } lb ? Math.Round(lb, 6) : null,
            slots.Sum(s => s.Waste),
            !result.BudgetHit,
            result.Nodes,
            notes);
    }

    /// <summary>The searched slots onto real slots; the two ring picks go where the worn rings already are.</summary>
    private static List<(GearSlot Slot, Option Option)> Place(Search.Result result)
    {
        var placed = new List<(GearSlot, Option)>();
        Option? right = null, left = null;
        for (var k = 0; k < result.Choice.Length; k++)
        {
            var slot = result.SearchSlots[k];
            var option = result.Choice[k];
            if (slot == GearSlot.RingRight) right = option;
            else if (slot == GearSlot.RingLeft) left = option;
            else placed.Add((slot, option));
        }

        if (right != null && left != null
            && ((right.Candidate.IsEquipped && right.Candidate.Slot == GearSlot.RingLeft)
                || (left.Candidate.IsEquipped && left.Candidate.Slot == GearSlot.RingRight)))
            (right, left) = (left, right);
        if (right != null) placed.Add((GearSlot.RingRight, right));
        if (left != null) placed.Add((GearSlot.RingLeft, left));
        return placed;
    }

    private static SlotPlan SlotFor(MeldRequest request, MeldOptions options, MateriaGrade[] materia, GearSlot slot, Option option)
    {
        var c = option.Candidate;
        var sockets = new List<SocketPlan>();
        var melds = c.Melds;
        for (var pos = 0; pos < c.TotalSockets; pos++)
        {
            var current = pos < melds.Count ? melds[pos] : null;
            var overmeld = pos < c.MateriaSlots ? 0 : pos - c.MateriaSlots + 1;
            if (pos < option.Kept)
            {
                sockets.Add(new SocketPlan(pos, overmeld, Known(materia, current!), current, SocketAction.Keep, 100, 0, 0));
            }
            else if (pos < option.Kept + option.NewTypes.Length)
            {
                var j = pos - option.Kept;
                var m = materia[option.NewTypes[j]];
                var pct = option.NewPercents[j];
                sockets.Add(new SocketPlan(
                    pos, overmeld, m, current,
                    current == null ? SocketAction.Meld : SocketAction.RetrieveThenMeld,
                    pct, 100.0 / pct, AttemptsAtConfidence(pct, options.ShoppingConfidence)));
            }
            else
            {
                sockets.Add(new SocketPlan(pos, overmeld, null, current, current == null ? SocketAction.Empty : SocketAction.Retrieve, 0, 0, 0));
            }
        }

        var stats = GatherStats.Zero;
        var order = GatherStats.All;
        // Option stats are in priority order; rebuild by name so the plan does not depend on it.
        var meld = c.Melds.Take(option.Kept)
            .Where(m => GatherStats.TryFromBaseParam(m.BaseParam, out _))
            .Select(m => ((GatherStat)m.BaseParam, m.Value))
            .Concat(option.NewTypes.Select(t => (materia[t].Stat, materia[t].Value)))
            .ToList();
        var waste = 0;
        foreach (var stat in order)
        {
            var sum = meld.Where(x => x.Item1 == stat).Sum(x => x.Item2);
            stats = stats.With(stat, c.BaseStats[stat] + Math.Min(c.Headroom(stat), sum));
            waste += Math.Max(0, sum - c.Headroom(stat));
        }

        var equipped = request.Candidates.FirstOrDefault(x => x.IsEquipped && x.Slot == slot);
        var rivals = request.Candidates
            .Where(x => (x.IsOwned || options.AllowUnownedGear) && (x.IsRing ? c.IsRing : x.Slot == c.Slot) && !ReferenceEquals(x, c))
            .ToList();
        return new SlotPlan(slot, c, equipped, !c.IsEquipped, !c.IsOwned, sockets, stats, waste, WhySlot(c, option, rivals));
    }

    private static MateriaGrade? Known(MateriaGrade[] materia, MeldedMateria current)
        => materia.FirstOrDefault(m => current.ItemId != 0 ? m.ItemId == current.ItemId : (uint)m.Stat == current.BaseParam && m.Grade == current.Grade);

    private static string WhySlot(GearCandidate chosen, Option option, List<GearCandidate> rivals)
    {
        var suffix = (chosen.IsEquipped ? " Already worn." : "") + (chosen.IsOwned ? "" : " Not owned: get it first.");
        if (option.Kept > 0)
            suffix += $" Keeps {option.Kept} meld(s) already on it.";
        if (rivals.Count == 0)
            return ("The only piece offered for this slot." + suffix).Trim();

        var rival = rivals
            .OrderByDescending(r => r.BaseStats.Gathering + r.BaseStats.Perception + r.BaseStats.Gp * 3)
            .First();
        var a = chosen.BaseStats;
        var b = rival.BaseStats;
        string why;
        if (a == b)
        {
            if (chosen.AdvancedMeldingPermitted && !rival.AdvancedMeldingPermitted)
                why = $"Same base stats as {rival.DisplayName}, but only this one takes overmelds.";
            else if (chosen.TotalSockets > rival.TotalSockets)
                why = $"Same base stats as {rival.DisplayName}, more sockets.";
            else
                why = $"Ties with {rival.DisplayName} on base stats; the cheaper one to meld or keep.";
        }
        else if (a.Gathering >= b.Gathering && a.Perception >= b.Perception && a.Gp >= b.Gp)
        {
            var d = a - b;
            why = $"Higher base stats than {rival.DisplayName} ({Signed(d.Gathering)} Gathering, {Signed(d.Perception)} Perception, {Signed(d.Gp)} GP)"
                + (chosen.TotalSockets < rival.TotalSockets ? $", which outweighs {rival.TotalSockets - chosen.TotalSockets} fewer socket(s)." : ".");
        }
        else if (a.Gathering <= b.Gathering && a.Perception <= b.Perception && a.Gp <= b.Gp)
        {
            why = $"Lower base stats than {rival.DisplayName}, but its {chosen.TotalSockets} socket(s){(chosen.AdvancedMeldingPermitted && !rival.AdvancedMeldingPermitted ? " with overmelds" : "")} get further.";
        }
        else
        {
            why = $"The best mix of base stats and sockets among {rivals.Count + 1} pieces for the targets.";
        }

        return (why + suffix).Trim();
    }

    private static string Signed(int v) => v >= 0 ? $"+{v}" : v.ToString();

    private static List<MateriaNeed> Needs(MeldRequest request, MateriaGrade[] materia, List<SlotPlan> slots)
    {
        var result = new List<MateriaNeed>();
        foreach (var m in materia)
        {
            var sockets = slots.SelectMany(s => s.Sockets)
                .Where(k => k.Materia == m && k.Action is SocketAction.Meld or SocketAction.RetrieveThenMeld)
                .ToList();
            if (sockets.Count == 0)
                continue;
            var expected = Math.Round(sockets.Sum(k => k.ExpectedMateria), 6);
            var confident = sockets.Sum(k => k.AttemptsAtConfidence);
            var stock = request.Stock.TryGetValue(m.ItemId, out var n) ? Math.Max(0, n) : 0;
            result.Add(new MateriaNeed(
                m, sockets.Count, expected, confident, stock,
                Math.Max(0, (int)Math.Ceiling(expected - 1e-6) - stock),
                Math.Max(0, confident - stock)));
        }

        return result;
    }

    private static StatOutcome Outcome(
        MeldRequest request,
        GatherStat[] priority,
        GatherStat stat,
        GatherStats gear,
        GatherStats withFood,
        FoodOption? food,
        Search.Result reference,
        MeldPlan? purchases,
        IReadOnlyList<MateriaNeed> toAcquire)
    {
        var target = request.Targets.For(stat);
        var g = gear[stat];
        var total = withFood[stat];
        var bonus = total - g;
        var foodPart = bonus > 0 && food != null ? $" + {food.DisplayName} {bonus}" : "";
        if (target == null || target.Value <= 0)
            return new StatOutcome(stat, null, g, bonus, total, true, 0, $"No target. {total} = gear {g}{foodPart}.");

        if (total >= target.Value)
            return new StatOutcome(stat, target, g, bonus, total, true, 0,
                $"Reached: {total} ≥ {target.Value} (gear {g}{foodPart}). Target: {target.Reason}.");

        var shortBy = target.Value - total;
        var index = Array.IndexOf(priority, stat);
        var alone = reference.MaxAlone[index];
        var aloneFed = alone + request.Foods.Select(f => f.Bonus(stat, alone)).DefaultIfEmpty(0).Max();
        string cause;
        if (aloneFed < target.Value)
        {
            cause = $"even with every socket on {GatherStats.Name(stat)} and the best food, this gear tops out at {aloneFed}";
        }
        else if (purchases != null && purchases.For(stat).Reached)
        {
            var list = string.Join(", ", toAcquire.Where(m => m.ToBuy > 0).Select(m => $"{m.ToBuy}× {m.Materia.Name}"));
            cause = $"the bags run short of materia; with {list} it is reached";
        }
        else
        {
            var ahead = priority.Take(index).Where(s => request.Targets.Value(s) > 0).Select(GatherStats.Name).ToList();
            cause = ahead.Count > 0
                ? $"the sockets go to {string.Join(" and ", ahead)} first (class priority); on its own it could reach {aloneFed}"
                : $"the sockets cannot hold it together with the other targets; on its own it could reach {aloneFed}";
        }

        return new StatOutcome(stat, target, g, bonus, total, false, shortBy,
            $"Short by {shortBy}: {total} of {target.Value} (gear {g}{foodPart}) — {cause}. Target: {target.Reason}.");
    }

    private static string FoodWhy(MeldRequest request, FoodOption? food, GatherStats gear)
    {
        if (request.Foods.Count == 0)
            return "No food offered.";
        if (food == null)
            return $"No food: none of the {request.Foods.Count} offered reaches more or saves materia.";
        var parts = GatherStats.All
            .Select(s => (Stat: s, Bonus: food.Bonus(s, gear[s])))
            .Where(x => x.Bonus > 0)
            .Select(x => $"+{x.Bonus} {GatherStats.Name(x.Stat)}");
        return $"{food.DisplayName}: {string.Join(", ", parts)}{(food.Owned > 0 ? $" ({food.Owned} in the bags)" : " (none in the bags)")}.";
    }

    private static IEnumerable<string> StockNotes(MeldRequest request, MateriaGrade[] materia, List<MateriaNeed> needs)
    {
        foreach (var need in needs)
        {
            // A lower grade melded while the better grade of the same stat ran out: say so.
            foreach (var better in materia.Where(m => m.Stat == need.Materia.Stat && m.Value > need.Materia.Value))
            {
                var stock = request.Stock.TryGetValue(better.ItemId, out var n) ? Math.Max(0, n) : 0;
                var used = needs.FirstOrDefault(x => x.Materia == better)?.Expected ?? 0;
                if (stock - used < 1)
                {
                    yield return $"{better.Name}: {(stock == 0 ? "none in the bags" : $"all {stock} in the bags used")}, so {need.Materia.Name} fills {need.Sockets} socket(s).";
                    break;
                }
            }
        }
    }
}
