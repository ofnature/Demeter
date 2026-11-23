using System;

namespace Demeter.Services.Gathering;

/// <summary>
/// Whether <i>this character, right now</i> could gather an item unattended — as opposed to whether
/// a node for it exists anywhere, which is what <see cref="IOwnGatherer.CanGather"/> answers.
///
/// <para>
/// The difference matters because of who asks. A quest step asks "is there a node" and the quest
/// itself has already given you the job. A crafting plugin asks over IPC and then <b>plans on the
/// answer</b> — it will queue a smelt on the strength of ore that is supposedly coming, so a yes
/// that turns out to mean "yes, at level 90, on a class you have not unlocked" is not a near miss;
/// it is a craft that fails a quarter of an hour later for a reason nobody can see.
/// </para>
///
/// <para>
/// Pure, so the promise is pinned by tests rather than by a run in the field.
/// </para>
/// </summary>
public static class GatherReadiness
{
    public const uint Miner = 16;
    public const uint Botanist = 17;
    public const uint Fisher = 18;

    /// <summary>
    /// How far below its own level a node already shows. The wiki has a Botanist 21 finding level
    /// 25 nodes; in the field a Botanist 52 worked Rainbow Cotton Boll's level 55 nodes and a
    /// Botanist 49 never saw them.
    /// </summary>
    public const int ShowsBelow = 4;

    /// <summary>The class level a node of this level shows from.</summary>
    public static int ShowsFrom(int nodeLevel) => Math.Max(1, nodeLevel - ShowsBelow);

    /// <summary>A node of this level shows to a gatherer of that one.</summary>
    public static bool Shows(int have, int nodeLevel) => have >= ShowsFrom(nodeLevel);

    /// <summary>
    /// Why this character cannot gather the target unattended, or null when it can.
    /// </summary>
    /// <param name="target">The chosen node, or null when the sheets name none we can use.</param>
    /// <param name="levelOf">
    /// The character's level on a ClassJob id, or 0 when that class is not unlocked. Unsynced —
    /// a level-synced duty must not make a gatherer look too low for its own nodes.
    /// </param>
    /// <param name="name">What to call the item in the sentence.</param>
    public static string? WhyNot(GatheringTarget? target, Func<uint, int> levelOf, string name)
    {
        if (target is null)
            return $"nothing in the sheets gathers {name} anywhere Demeter can reach";

        if (target.ClassJobId == Fisher)
            return $"{name} is fished — the fisher answers for it, not the node gatherer";

        if (target.ClassJobId is not (Miner or Botanist))
            return $"{name} comes from a node Demeter does not know how to work";

        var job = target.ClassJobId == Miner ? "Miner" : "Botanist";
        var have = levelOf(target.ClassJobId);
        if (have <= 0)
            return $"{name} needs {job}, which is not unlocked on this character";
        if (!Shows(have, target.Level))
            return $"{name} needs {job} {ShowsFrom(target.Level)} for its level {target.Level} nodes; this character is {job} {have}";

        return null;
    }

    /// <summary>The promise form: true only when nothing above is in the way.</summary>
    public static bool CanGather(GatheringTarget? target, Func<uint, int> levelOf)
        => WhyNot(target, levelOf, "it") is null;
}
