using System;
using System.Collections.Generic;
using System.Linq;

namespace Demeter.Services.Gathering;

/// <summary>One gatherer action, as the Action sheet describes it.</summary>
/// <param name="ClassJobId">16 Miner, 17 Botanist, 18 Fisher.</param>
/// <param name="Level">The class level it is learned at (<c>Action.ClassJobLevel</c>).</param>
/// <param name="UnlockLink"><c>Action.UnlockLink</c>: 0 for none; above 0x10000 a Quest row; otherwise an unlock link.</param>
/// <param name="QuestName">The unlocking quest's name, when the link is a quest.</param>
public sealed record GatherAbility(uint ActionId, string Name, uint ClassJobId, int Level, uint UnlockLink, string? QuestName);

/// <summary>Where one ability stands on this character.</summary>
public sealed record AbilityStanding(GatherAbility Ability, bool Learned, string Missing);

/// <summary>What the abilities need to know of the character — readable from any job.</summary>
public interface IAbilityState
{
    /// <summary>The real level of a class, unsynced; 0 when it is not unlocked.</summary>
    int LevelOf(uint classJobId);

    /// <summary>The unlock link (or quest) is done. Zero is always done.</summary>
    bool UnlockDone(uint unlockLink);
}

/// <summary>
/// Which gathering actions the character has, and what each missing one still needs.
///
/// <para>
/// Daedalus's rule (<c>ActionUnlockHelper</c> / <c>ActionService.IsActionLearned</c>), kept to the
/// half that is readable from any job: the class is high enough, <b>and</b> the action's
/// <c>UnlockLink</c> is satisfied — a level alone does not teach Collect, a quest does. Daedalus adds
/// <c>GetActionStatus != 565</c>, which only answers for the job being played; a whole-character
/// report ("what does my Botanist still lack?") cannot use it, and the status codes for gatherer
/// actions are not verified, so it is left out.
/// </para>
///
/// <para>
/// The list comes from the Action sheet, not typed in: every action of the three classes, with its
/// level and its unlock. Daedalus types its lists in per combat job and only reports what is
/// already within level; this reports "needs Lv 63" as well, so the whole road is visible.
/// </para>
/// </summary>
public sealed class GatheringAbilities
{
    public const uint Miner = GatherReadiness.Miner;
    public const uint Botanist = GatherReadiness.Botanist;
    public const uint Fisher = GatherReadiness.Fisher;

    /// <summary>Quest rows are written into <c>UnlockLink</c> offset by this.</summary>
    public const uint QuestLinkBase = 0x10000;

    private readonly Dictionary<uint, GatherAbility> _byId;
    private readonly IAbilityState _state;

    public GatheringAbilities(IEnumerable<GatherAbility> abilities, IAbilityState state)
    {
        _byId = new Dictionary<uint, GatherAbility>();
        foreach (var a in abilities)
            _byId.TryAdd(a.ActionId, a);
        _state = state;
    }

    public IReadOnlyCollection<GatherAbility> All => _byId.Values;

    /// <summary>
    /// The character has this action. An id the catalogue does not hold is not a gatherer action
    /// we know anything about, and is answered yes — the game refusing it says the rest.
    /// </summary>
    public bool Knows(uint actionId)
        => !_byId.TryGetValue(actionId, out var a) || Standing(a).Learned;

    public AbilityStanding Standing(GatherAbility a)
    {
        var level = _state.LevelOf(a.ClassJobId);
        var levelOk = level >= a.Level;
        var unlocked = _state.UnlockDone(a.UnlockLink);
        if (levelOk && unlocked)
            return new AbilityStanding(a, true, string.Empty);

        var needs = new List<string>();
        if (!levelOk)
            needs.Add(level <= 0 ? $"{JobName(a.ClassJobId)} unlocked, then Lv {a.Level}" : $"Lv {a.Level} (now {level})");
        if (!unlocked)
            needs.Add(a.QuestName is { Length: > 0 } quest ? $"quest \"{quest}\"" : $"unlock {a.UnlockLink}");
        return new AbilityStanding(a, false, string.Join(" and ", needs));
    }

    /// <summary>Every action of a class not yet learned, lowest level first — the road ahead.</summary>
    public IReadOnlyList<AbilityStanding> Missing(uint classJobId)
        => _byId.Values.Where(a => a.ClassJobId == classJobId)
            .Select(Standing)
            .Where(s => !s.Learned)
            .OrderBy(s => s.Ability.Level).ThenBy(s => s.Ability.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>"Collect needs quest "X"" — for a refusal that names the cure; null when it is learned.</summary>
    public string? WhyNot(uint actionId)
        => _byId.TryGetValue(actionId, out var a) && Standing(a) is { Learned: false } s
            ? $"{a.Name} needs {s.Missing}"
            : null;

    public static string JobName(uint classJobId) => classJobId switch
    {
        Miner => "Miner",
        Botanist => "Botanist",
        Fisher => "Fisher",
        _ => $"class {classJobId}",
    };
}
