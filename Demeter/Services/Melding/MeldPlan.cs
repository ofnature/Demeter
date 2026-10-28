using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Melding;

/// <summary>What to do with one socket. The adapter drives the meld window from these.</summary>
public enum SocketAction
{
    /// <summary>Nothing there, nothing planned.</summary>
    Empty,

    /// <summary>The materia already there stays.</summary>
    Keep,

    /// <summary>Meld <see cref="SocketPlan.Materia"/> into the empty socket.</summary>
    Meld,

    /// <summary>Retrieve what is there (counted as lost), then meld <see cref="SocketPlan.Materia"/>.</summary>
    RetrieveThenMeld,

    /// <summary>Retrieve what is there and leave the socket empty.</summary>
    Retrieve,
}

/// <param name="Index">0-based socket position; guaranteed sockets first, then overmelds.</param>
/// <param name="OvermeldSlot">1–4 for an overmeld socket, 0 for a guaranteed one.</param>
/// <param name="SuccessPercent">Chance one attempt sticks (100 in a guaranteed socket).</param>
/// <param name="ExpectedMateria">Materia an attempt sequence uses on average: 100 / SuccessPercent.</param>
/// <param name="AttemptsAtConfidence">Materia to bring to land it with the plan's shopping confidence.</param>
public sealed record SocketPlan(
    int Index,
    int OvermeldSlot,
    MateriaGrade? Materia,
    MeldedMateria? Current,
    SocketAction Action,
    int SuccessPercent,
    double ExpectedMateria,
    int AttemptsAtConfidence)
{
    public bool IsOvermeld => OvermeldSlot > 0;
}

/// <param name="Equipped">What is in the slot now, if the adapter said.</param>
/// <param name="Swap">The piece to wear is not the one worn now.</param>
/// <param name="Acquire">The piece is not owned: buy, craft or trade for it first.</param>
/// <param name="Stats">The piece's stats once melded (cap-clipped).</param>
/// <param name="Waste">Meld points lost to the piece's caps.</param>
public sealed record SlotPlan(
    GearSlot Slot,
    GearCandidate Wear,
    GearCandidate? Equipped,
    bool Swap,
    bool Acquire,
    IReadOnlyList<SocketPlan> Sockets,
    GatherStats Stats,
    int Waste,
    string Why);

/// <param name="Gear">Total before food: the character's own base plus every piece.</param>
/// <param name="ShortBy">0 when reached (or no target).</param>
public sealed record StatOutcome(
    GatherStat Stat,
    StatTarget? Target,
    int Gear,
    int Food,
    int Total,
    bool Reached,
    int ShortBy,
    string Why);

/// <summary>One materia type in a plan: how much it uses, how much is held, how much is missing.</summary>
/// <param name="Sockets">New melds of this type.</param>
/// <param name="Expected">Materia used on average, overmeld failures included.</param>
/// <param name="AtConfidence">Materia to bring so each overmeld lands with the shopping confidence.</param>
/// <param name="ToBuy">Missing on average: ⌈Expected⌉ − InStock.</param>
/// <param name="ToBuyAtConfidence">Missing at the shopping confidence: AtConfidence − InStock.</param>
public sealed record MateriaNeed(
    MateriaGrade Materia,
    int Sockets,
    double Expected,
    int AtConfidence,
    int InStock,
    int ToBuy,
    int ToBuyAtConfidence);

/// <summary>
/// The optimizer's answer: what to wear, what to meld where, what to eat, what that reaches and why.
/// Deterministic for the same <see cref="MeldRequest"/>.
/// </summary>
/// <param name="Slots">One entry per slot that had a candidate, in container order.</param>
/// <param name="Food">The food to eat; null when no food helps (or none was offered).</param>
/// <param name="Stats">Gathering, Perception, GP in the class's priority order.</param>
/// <param name="Materia">Materia the plan melds, from the bags.</param>
/// <param name="ToAcquire">Materia to buy or make to do better than the bags allow (empty when the bags suffice).</param>
/// <param name="WithPurchases">The plan with unlimited materia, when that would reach more than the bags can.</param>
/// <param name="ExpectedMateria">All materia the plan uses on average.</param>
/// <param name="ExpectedMateriaLowerBound">
/// No plan reaching what this one reaches can use fewer materia on average. Equal to
/// <paramref name="ExpectedMateria"/> when proven optimal; null when a cut-short search could not
/// bound it. The gap between the two is how far from the best this plan might be.
/// </param>
/// <param name="ProvenOptimal">False only when the search hit its node budget and returned its best so far.</param>
public sealed record MeldPlan(
    GatherJob Job,
    IReadOnlyList<GatherStat> Priority,
    IReadOnlyList<SlotPlan> Slots,
    FoodOption? Food,
    string FoodWhy,
    IReadOnlyList<StatOutcome> Stats,
    IReadOnlyList<MateriaNeed> Materia,
    IReadOnlyList<MateriaNeed> ToAcquire,
    MeldPlan? WithPurchases,
    double ExpectedMateria,
    double? ExpectedMateriaLowerBound,
    int Waste,
    bool ProvenOptimal,
    long NodesSearched,
    IReadOnlyList<string> Notes)
{
    public bool AllReached => Stats.All(s => s.Reached);

    public StatOutcome For(GatherStat stat) => Stats.First(s => s.Stat == stat);

    public SlotPlan? For(GearSlot slot) => Slots.FirstOrDefault(s => s.Slot == slot);

    public GatherStats Totals => new(For(GatherStat.Gathering).Total, For(GatherStat.Perception).Total, For(GatherStat.Gp).Total);
}
