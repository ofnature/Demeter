using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Demeter.Services.Gathering;
using Demeter.Services.Ipc;
using Demeter.Services.Travel;

namespace Demeter.Services.Game;

/// <summary>One gearset slot: its class, that class's parent, and the class's real level.</summary>
public sealed record GearsetInfo(int Id, uint ClassJobId, uint ParentClassJobId, int Level);

/// <summary>
/// The game-facing basics Demeter needs: the character's state, the mount, the pathing and teleport
/// plugins, gearsets, the bag and self-repair. Copied from Odysseus's <c>GameStepWorld</c> (same
/// author, both AGPL) — only the members the gatherer and its travel use. Decision-free, like the
/// original: every judgement lives in <see cref="Traveller"/>, <see cref="GatherRunner"/> and the
/// list runner, where the fakes can test it.
/// </summary>
public sealed unsafe class GameBasics : ITravelWorld, IRepairWorld
{
    /// <summary>GeneralAction 9 — Mount Roulette.</summary>
    private const uint MountRouletteGeneralAction = 9;

    /// <summary>GeneralAction 23 — Dismount.</summary>
    private const uint DismountGeneralAction = 23;

    /// <summary>GeneralAction 4 — Sprint; status 50 while it runs.</summary>
    private const uint SprintGeneralAction = 4;
    private const uint SprintStatus = 50;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly VnavIpc _vnav;
    private readonly LifestreamIpc _lifestream;
    private readonly DaedalusIpc _daedalus;
    private readonly AetheryteCatalog _aetherytes;
    private readonly ChatCommandSender _chat;
    private readonly Action<string> _log;

    public GameBasics(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui, IDataManager data,
        VnavIpc vnav, LifestreamIpc lifestream, DaedalusIpc daedalus, AetheryteCatalog aetherytes,
        ChatCommandSender chat, Action<string> log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _gameGui = gameGui;
        _data = data;
        _vnav = vnav;
        _lifestream = lifestream;
        _daedalus = daedalus;
        _aetherytes = aetherytes;
        _chat = chat;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public uint TerritoryId => _clientState.TerritoryType;
    public Vector3 PlayerPosition => _objectTable.LocalPlayer?.Position ?? Vector3.Zero;
    public void Log(string message) => _log(message);

    // ── Character state ──

    public bool IsReady
        => _objectTable.LocalPlayer is not null
           && !_condition[ConditionFlag.BetweenAreas]
           && !_condition[ConditionFlag.BetweenAreas51]
           && !_condition[ConditionFlag.Occupied]
           && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
           && !_condition[ConditionFlag.OccupiedInQuestEvent]
           && !_condition[ConditionFlag.Casting]
           && !_condition[ConditionFlag.Unconscious];

    public bool IsOccupied
        => _condition[ConditionFlag.Occupied]
           || _condition[ConditionFlag.OccupiedInCutSceneEvent]
           || _condition[ConditionFlag.OccupiedInQuestEvent]
           || _condition[ConditionFlag.OccupiedInEvent]
           || _condition[ConditionFlag.WatchingCutscene]
           || _condition[ConditionFlag.WatchingCutscene78];

    public bool IsTravelBusy
        => _lifestream.IsBusy
           || _condition[ConditionFlag.BetweenAreas]
           || _condition[ConditionFlag.BetweenAreas51]
           || _condition[ConditionFlag.Casting];

    public bool IsCasting => _condition[ConditionFlag.Casting];
    public bool InCombat => _condition[ConditionFlag.InCombat];
    public bool IsDead => _objectTable.LocalPlayer?.IsDead ?? false;
    public bool DaedalusDisabledByUser => _daedalus.IsDisabledByUser();
    public uint CurrentClassJob => _objectTable.LocalPlayer?.ClassJob.RowId ?? 0;

    // ── Mount ──

    public bool IsMounted => _condition[ConditionFlag.Mounted];
    public bool IsInFlight => _condition[ConditionFlag.InFlight];

    public void Mount() => UseGeneralAction(MountRouletteGeneralAction, "Mount");
    public void Dismount() => UseGeneralAction(DismountGeneralAction, "Dismount");

    public void Jump() => _chat.Send("/generalaction Jump");

    public bool Sprint()
    {
        try
        {
            if (_objectTable.LocalPlayer is not { } player) return false;
            foreach (var status in player.StatusList)
                if (status.StatusId == SprintStatus) return false;
            var manager = ActionManager.Instance();
            if (manager == null || manager->GetActionStatus(ActionType.GeneralAction, SprintGeneralAction) != 0)
                return false;
            return manager->UseAction(ActionType.GeneralAction, SprintGeneralAction);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Straight off <c>TerritoryType.Mount</c> — false for every city zone.</summary>
    public bool CanMountHere
    {
        get
        {
            try
            {
                return _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType)?.Mount ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// The zone's aether currents are complete. A zone with no current set at all reads false: the
    /// base-game zones fly only after the story unlocks it, and the game refusing the flight is a
    /// worse way to learn that than walking.
    /// </summary>
    public bool CanFlyHere
    {
        get
        {
            try
            {
                var territory = _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType);
                if (territory is not { } t)
                    return false;
                var set = t.AetherCurrentCompFlgSet.RowId;
                if (set == 0)
                    return false;
                var state = PlayerState.Instance();
                return state != null && state->IsAetherCurrentZoneComplete(set);
            }
            catch
            {
                return false;
            }
        }
    }

    private void UseGeneralAction(uint id, string what)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager != null)
                manager->UseAction(ActionType.GeneralAction, id);
        }
        catch (Exception ex)
        {
            _log($"{what} failed: {ex.Message}");
        }
    }

    // ── Pathing (vnavmesh or Ariadne, as Settings says — VnavIpc speaks both) ──

    public bool NavmeshReady => _vnav.IsReady;
    public float NavmeshBuildProgress => _vnav.BuildProgress;
    public bool IsMoving => _vnav.IsBusy;
    public bool IsPathfinding => _vnav.IsPathfinding;
    public int PathWaypointCount => _vnav.WaypointCount;
    public bool MoveTo(Vector3 destination, bool fly) => _vnav.MoveTo(destination, fly);
    public bool MoveCloseTo(Vector3 destination, float tolerance, bool fly) => _vnav.MoveCloseTo(destination, tolerance, fly);
    public bool MoveDirectTo(Vector3 destination, bool fly) => _vnav.MoveDirect(destination, fly);
    public void StopMoving() => _vnav.Stop();
    public Vector3? NearestReachablePoint(Vector3 near, float within) => _vnav.NearestReachablePoint(near, within, within);

    // ── Teleport ──

    public uint? NearestAttunedAetheryte(uint territoryId, Vector3 near)
        => Attuned(_aetherytes.InTerritory(territoryId, near));

    public Vector3? AetherytePosition(uint aetheryteId) => _aetherytes.PositionOf(aetheryteId);

    public bool Teleport(uint aetheryteId) => _lifestream.Teleport(aetheryteId);

    public void AbortTravel()
    {
        if (_lifestream.IsBusy)
            _lifestream.Abort();
    }

    public void RefreshAetheryteList()
    {
        try
        {
            var telepo = Telepo.Instance();
            if (telepo != null)
                telepo->UpdateAetheryteList();
        }
        catch (Exception ex)
        {
            _log($"Refreshing the aetheryte list failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The first of these the character has attuned. When the unlock state cannot be read at all the
    /// first candidate is taken and Lifestream gets to be the one that says no — better than calling
    /// a zone unreachable because of a bad read.
    /// </summary>
    private uint? Attuned(IReadOnlyList<uint> candidates)
    {
        if (candidates.Count == 0)
            return null;
        try
        {
            var state = UIState.Instance();
            if (state == null)
                return candidates[0];
            foreach (var id in candidates)
                if (state->IsAetheryteUnlocked(id))
                    return id;
            return null;
        }
        catch
        {
            return candidates[0];
        }
    }

    // ── Classes and gearsets ──

    /// <summary>
    /// The character's real level on any class, or 0 when it is not unlocked. Unsynced: Dalamud's
    /// <c>LocalPlayer.Level</c> reads the sync inside synced content, and a gatherer standing in a
    /// synced zone has not forgotten how to mine.
    /// </summary>
    public int LevelOfJob(uint classJobId)
    {
        try
        {
            var index = _data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId)?.ExpArrayIndex ?? -1;
            var state = PlayerState.Instance();
            return index >= 0 && state != null ? state->ClassJobLevels[index] : 0;
        }
        catch
        {
            return 0;
        }
    }

    public bool EquipGearset(int gearsetId)
    {
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            if (module == null || !module->IsValidGearset(gearsetId)) return false;
            return module->EquipGearset(gearsetId, 0) >= 0;
        }
        catch (Exception ex)
        {
            _log($"EquipGearset {gearsetId} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The 100 gearset slots, skipping the empty ones and the ones whose class the character has
    /// never levelled. Level comes from <c>ClassJobLevels</c>, not the gearset (which only carries an
    /// item level).
    /// </summary>
    public IReadOnlyList<GearsetInfo> Gearsets()
    {
        var result = new List<GearsetInfo>();
        try
        {
            var module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureGearsetModule.Instance();
            var state = PlayerState.Instance();
            if (module == null) return result;
            var jobs = _data.GetExcelSheet<ClassJob>();
            for (var i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i)) continue;
                var entry = module->GetGearset(i);
                if (entry == null || entry->ClassJob == 0) continue;
                if (jobs.GetRowOrDefault(entry->ClassJob) is not { } job) continue;
                var level = state != null && job.ExpArrayIndex >= 0 ? state->ClassJobLevels[job.ExpArrayIndex] : 0;
                if (level == 0) continue;
                result.Add(new GearsetInfo(i, entry->ClassJob, job.ClassJobParent.RowId, level));
            }
        }
        catch (Exception ex)
        {
            _log($"Gearset scan failed: {ex.Message}");
        }
        return result;
    }

    // ── Bag ──

    /// <summary>Both qualities counted: a delivery or a craft takes an HQ item as readily as an NQ one.</summary>
    public int ItemCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;
            return manager->GetInventoryItemCount(itemId, isHq: false) + manager->GetInventoryItemCount(itemId, isHq: true);
        }
        catch
        {
            return 0;
        }
    }

    public int FreeBagSlots
    {
        get
        {
            try
            {
                var manager = InventoryManager.Instance();
                return manager == null ? 0 : (int)manager->GetEmptySlotsInBag();
            }
            catch
            {
                return 0;
            }
        }
    }

    // ── Repair ──

    public int LowestGearConditionPercent
    {
        get
        {
            try
            {
                var manager = InventoryManager.Instance();
                var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
                if (container == null || !container->IsLoaded)
                    return 100;
                var lowest = 100;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;
                    var percent = (int)Math.Round(slot->Condition / 300.0);   // 30000 is pristine
                    if (percent < lowest)
                        lowest = percent;
                }
                return lowest;
            }
            catch (Exception ex)
            {
                _log($"Reading gear condition failed: {ex.Message}");
                return 100;
            }
        }
    }

    public void OpenRepairWindow() => _chat.Send("/generalaction Repair");

    public bool PressRepairAll()
    {
        // No button field is laid out for the repair window; its own callback is the press —
        // value 0 is Repair All, the way every self-repairing plugin fires it.
        if (!IsAddonVisible("Repair"))
            return false;
        FireAddonCallback("Repair", 0);
        return true;
    }

    public void CloseRepairWindow()
    {
        try
        {
            var addon = _gameGui.GetAddonByName("Repair");
            var unit = (AtkUnitBase*)addon.Address;
            if (unit != null && unit->IsVisible)
                unit->Close(true);
        }
        catch (Exception ex)
        {
            _log($"Closing the repair window failed: {ex.Message}");
        }
    }

    // ── UI ──

    public bool IsAddonVisible(string name)
    {
        var addon = _gameGui.GetAddonByName(name);
        return !addon.IsNull && addon.IsVisible;
    }

    public void SelectYesNo(bool yes) => FireAddonCallback("SelectYesno", yes ? 0 : 1);

    public void SendChatCommand(string command) => _chat.Send(command);

    private void FireAddonCallback(string addonName, int value)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(addonName);
            if (addon.IsNull || !addon.IsVisible)
                return;
            ((AtkUnitBase*)addon.Address)->FireCallbackInt(value);
        }
        catch (Exception ex)
        {
            _log($"Dialog \"{addonName}\" callback failed: {ex.Message}");
        }
    }
}
