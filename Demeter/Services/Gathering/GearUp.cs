using System;

namespace Demeter.Services.Gathering;

/// <summary>The game calls <see cref="GearUp"/> makes; <c>GameBasics</c> answers them.</summary>
public interface IGearUpGame
{
    uint CurrentClassJob { get; }

    /// <summary>The game's Recommended Gear is still working something out or putting it on.</summary>
    bool RecommendBusy { get; }

    /// <summary>Have the game pick the best gear the character owns for this class.</summary>
    bool RecommendFor(uint classJobId);

    /// <summary>The main-hand tool it picked, by item id; 0 when the character owns none.</summary>
    uint RecommendedMainHand { get; }

    /// <summary>Put that tool on, which is what changes the class — as dragging it onto the main hand does.</summary>
    bool PutOnRecommendedMainHand();

    /// <summary>Put the rest of the picked gear on: the Recommended Gear button.</summary>
    void PutOnRecommended();

    bool GearsetSlotFree { get; }

    /// <summary>Save what is worn as a new gearset. Its id, or -1 when the game would not.</summary>
    int SaveGearset();
}

/// <summary>
/// A class with no gearset: the game's own Recommended Gear for it is put on and saved as a new
/// gearset, so a gather can switch to it now and every time after. The class's best tool is put
/// on first — that is what changes the class — and Recommended Gear is then run for the class the
/// character is on, which is the case the button itself handles. Nothing is worn that was not
/// already the character's, and nothing is bought.
/// </summary>
public sealed class GearUp
{
    /// <summary>Each step's wait for the game: the class to change, the gear to go on.</summary>
    private static readonly TimeSpan StepWait = TimeSpan.FromSeconds(10);

    /// <summary>The equipping runs across frames; it is given this long to start before "not busy" is believed.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1.5);

    private enum Step { Idle, Picking, ToolOn, Repicking, Equipping, Done, Failed }

    private readonly IGearUpGame _game;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private Step _step = Step.Idle;
    private uint _classJob;
    private string _className = string.Empty;
    private DateTime _since;

    public GearUp(IGearUpGame game, Func<DateTime> now, Action<string> log)
    {
        _game = game;
        _now = now;
        _log = log;
    }

    public bool Running => _step is Step.Picking or Step.ToolOn or Step.Repicking or Step.Equipping;

    /// <summary>Why the last one stopped short, for the run to fault with. Empty otherwise.</summary>
    public string Failure { get; private set; } = string.Empty;

    /// <summary>Forget a past failure: a new switch is being asked for.</summary>
    public void Forget()
    {
        if (!Running)
        {
            _step = Step.Idle;
            Failure = string.Empty;
        }
    }

    /// <summary>Start, or say (in <see cref="Failure"/>) why it cannot.</summary>
    public bool Begin(uint classJobId, string className)
    {
        _classJob = classJobId;
        _className = className;
        Failure = string.Empty;
        if (!_game.GearsetSlotFree)
            return Fail("every gearset slot is in use — free one, or save a gearset for it yourself");
        if (!_game.RecommendFor(classJobId))
            return Fail("the game's Recommended Gear would not work it out");
        _log($"No gearset for {className}: putting on the game's Recommended Gear for it and saving that as one.");
        Enter(Step.Picking);
        return true;
    }

    /// <summary>Drive it. True while it is still going.</summary>
    public bool Tick()
    {
        if (!Running)
            return false;
        var now = _now();
        if (now - _since > StepWait)
        {
            Fail(_step switch
            {
                Step.ToolOn => $"the game did not put the {_className} tool on (busy, or in combat or a duty?)",
                Step.Equipping => "the game did not finish putting the gear on",
                _ => "the game's Recommended Gear did not answer",
            });
            return false;
        }

        switch (_step)
        {
            case Step.Picking:
                if (_game.RecommendBusy)
                    break;
                if (_game.RecommendedMainHand == 0)
                    return Fail($"there is no {_className} tool in the Armoury Chest or the bags");
                if (_game.CurrentClassJob == _classJob)
                {
                    _game.PutOnRecommended();
                    Enter(Step.Equipping);
                    break;
                }
                if (!_game.PutOnRecommendedMainHand())
                    return Fail($"the game would not put the {_className} tool on");
                Enter(Step.ToolOn);
                break;

            case Step.ToolOn:
                if (_game.CurrentClassJob != _classJob)
                    break;
                if (!_game.RecommendFor(_classJob))
                    return Fail("the game's Recommended Gear would not work it out");
                Enter(Step.Repicking);
                break;

            case Step.Repicking:
                if (_game.RecommendBusy)
                    break;
                _game.PutOnRecommended();
                Enter(Step.Equipping);
                break;

            case Step.Equipping:
                if (now - _since < Settle || _game.RecommendBusy)
                    break;
                var id = _game.SaveGearset();
                if (id < 0)
                    return Fail("the game would not save the gearset");
                _log($"Saved gearset {id + 1} for {_className}: the game's Recommended Gear from what is owned.");
                _step = Step.Done;
                return false;
        }
        return Running;
    }

    private void Enter(Step step)
    {
        _step = step;
        _since = _now();
    }

    private bool Fail(string why)
    {
        _step = Step.Failed;
        Failure = $"no gearset for {_className}, and none could be saved: {why}";
        _log(Failure + ".");
        return false;
    }
}
