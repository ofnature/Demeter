using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Demeter.Services.Fishing;
using Demeter.Services.Gathering;

namespace Demeter.Services.Game;

/// <summary>
/// The real <see cref="IHoleWorld"/>: the Fisher's actions and state, through ActionManager and the
/// condition flags. Decision-free, like the rest of the game layer.
/// </summary>
public sealed unsafe class GameFishingWorld : IHoleWorld
{
    /// <summary>Fisher's Cast and Quit (read off the Action sheet 2026-10-22).</summary>
    public const uint CastAction = 289;
    public const uint QuitAction = 299;

    private readonly GameBasics _basics;
    private readonly GameGatherWorld _gather;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly Action<string> _log;

    public GameFishingWorld(GameBasics basics, GameGatherWorld gather, IObjectTable objectTable, ICondition condition, Action<string> log)
    {
        _basics = basics;
        _gather = gather;
        _objectTable = objectTable;
        _condition = condition;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public bool IsOccupied => _basics.IsOccupied;
    public bool IsMounted => _basics.IsMounted;
    public uint CurrentClassJob => _basics.CurrentClassJob;
    public bool IsFishing => _condition[ConditionFlag.Fishing];

    public bool EquipGearsetFor(uint classJobId, out string reason) => _gather.EquipGearsetFor(classJobId, out reason);
    public bool GearingUp => _gather.GearingUp;
    public string GearUpFailure => _gather.GearUpFailure;

    public void Face(float rotation)
    {
        try
        {
            if (_objectTable.LocalPlayer is { } player)
                ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address)->SetRotation(rotation);
        }
        catch (Exception ex)
        {
            _log($"Facing the water failed: {ex.Message}");
        }
    }

    public uint CastStatus
    {
        get
        {
            try
            {
                var manager = ActionManager.Instance();
                return manager == null ? uint.MaxValue : manager->GetActionStatus(ActionType.Action, CastAction);
            }
            catch
            {
                return uint.MaxValue;
            }
        }
    }

    public bool Cast() => Use(CastAction, "Cast");

    public bool Quit() => Use(QuitAction, "Quit");

    private bool Use(uint action, string name)
    {
        try
        {
            var manager = ActionManager.Instance();
            return manager != null && manager->UseAction(ActionType.Action, action);
        }
        catch (Exception ex)
        {
            _log($"{name} failed: {ex.Message}");
            return false;
        }
    }

    public void Log(string message) => _log(message);
}
