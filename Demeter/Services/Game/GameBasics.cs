using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
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
/// The game-facing basics Demeter needs: the character's state, the mount, Ariadne and Lifestream, gearsets, the bag and self-repair. Copied from Odysseus's <c>GameStepWorld</c> (same
/// author, both AGPL) — only the members the gatherer and its travel use. Decision-free, like the
/// original: every judgement lives in <see cref="Traveller"/>, <see cref="GatherRunner"/> and the
/// list runner, where the fakes can test it.
/// </summary>
public sealed unsafe class GameBasics : ITravelWorld, IRepairWorld, IAbilityState
{
    /// <summary>GeneralAction 9 — Mount Roulette.</summary>
    private const uint MountRouletteGeneralAction = 9;

    /// <summary>GeneralAction 23 — Dismount.</summary>
    private const uint DismountGeneralAction = 23;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly AriadneIpc _ariadne;
    private readonly LifestreamIpc _lifestream;
    private readonly DaedalusIpc _daedalus;
    private readonly AetheryteCatalog _aetherytes;
    private readonly ChatCommandSender _chat;
    private readonly Action<string> _log;

    public GameBasics(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui, IDataManager data,
        AriadneIpc ariadne, LifestreamIpc lifestream, DaedalusIpc daedalus, AetheryteCatalog aetherytes,
        ChatCommandSender chat, Action<string> log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _gameGui = gameGui;
        _data = data;
        _ariadne = ariadne;
        _lifestream = lifestream;
        _daedalus = daedalus;
        _aetherytes = aetherytes;
        _chat = chat;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;
    public uint TerritoryId => _clientState.TerritoryType;

    /// <summary>
    /// A Cosmic Exploration zone (Sinus Ardorum, Phaenna, Oizys): TerritoryIntendedUse 60, read from the
    /// sheet. <c>GameMain.CurrentTerritoryIntendedUseId</c> did not say so on Sinus Ardorum (2026-11-14).
    /// </summary>
    public bool InCosmicExploration
        => _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(_clientState.TerritoryType)?.TerritoryIntendedUse.RowId == 60;
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

    public bool InCombat => _condition[ConditionFlag.InCombat];
    public bool IsDead => _objectTable.LocalPlayer?.IsDead ?? false;
    public bool DaedalusDisabledByUser => _daedalus.IsDisabledByUser();
    public uint CurrentClassJob => _objectTable.LocalPlayer?.ClassJob.RowId ?? 0;

    // ── Mount ──

    public bool IsMounted => _condition[ConditionFlag.Mounted];
    public bool IsInFlight => _condition[ConditionFlag.InFlight];

    public void Mount() => UseGeneralAction(MountRouletteGeneralAction, "Mount");
    public void Dismount() => UseGeneralAction(DismountGeneralAction, "Dismount");

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

    // ── Ariadne ──

    public bool NavReady => _ariadne.IsReady;
    public bool MoveTo(Vector3 destination, bool fly, float range) => _ariadne.MoveTo(destination, fly, range);
    public bool IsMoving => _ariadne.IsBusy;
    public string MoveResult => _ariadne.LastResult;
    public void StopMoving() => _ariadne.Stop();
    public Task<Vector3?> NearestReachablePoint(Vector3 near, float within) => _ariadne.NearestReachablePoint(near, within);

    // ── Teleport ──

    public uint? NearestAttunedAetheryte(uint territoryId, Vector3 near)
        => Attuned(_aetherytes.InTerritory(territoryId, near));

    /// <summary>
    /// Odysseus's route (GameStepWorld.RouteTo), with one change: on the same network but not at a
    /// shard, it teleports to the city's crystal first. Lifestream only starts a hop from a shard it
    /// counts the character as at, and Demeter has no walk-to-the-shard step.
    /// </summary>
    public TravelRoute? RouteTo(uint territoryId, Vector3 near)
    {
        if (Attuned(_aetherytes.InTerritory(territoryId, near)) is { } direct)
            return new TravelRoute(direct, null, territoryId);

        // No aetheryte in the zone. It may still be half a city, reachable only across the aethernet.
        if (_aetherytes.ShardIn(territoryId, near) is not { } shard)
            return null;
        if (_aetherytes.GroupOfTerritory(TerritoryId) == shard.Group && _lifestream.ActiveAetheryte != 0)
            return new TravelRoute(null, shard.Name, territoryId);
        if (_aetherytes.HubOfGroup(shard.Group) is not { } hub || Attuned([hub]) is null)
            return null;
        return new TravelRoute(hub, shard.Name, _aetherytes.TerritoryOf(hub) ?? territoryId);
    }

    /// <summary>
    /// By the shard's PlaceName row when the sheet knows it (no spelling to disagree about: a name
    /// that matches nothing returns true and stands still), by name otherwise or when asked to.
    /// </summary>
    public bool AethernetTeleport(string destination, bool byNameOnly)
    {
        if (!byNameOnly && _aetherytes.StopNamed(destination) is { PlaceNameId: not 0 } stop)
        {
            _log($"Aethernet to {stop.Name} by id {stop.PlaceNameId}.");
            if (_lifestream.AethernetTeleportByPlaceName(stop.PlaceNameId))
                return true;
            _log($"Aethernet to {stop.Name} by id was refused; trying by name.");
        }
        _log($"Aethernet to \"{destination}\" by name.");
        return _lifestream.AethernetTeleport(destination);
    }

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

    /// <summary>Whether the character has attuned this aetheryte; false when it cannot be read.</summary>
    public bool IsAttuned(uint aetheryteId)
    {
        try
        {
            var state = UIState.Instance();
            return state != null && state->IsAetheryteUnlocked(aetheryteId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether a quest is done; false when it cannot be read.</summary>
    public bool IsQuestComplete(uint questId)
    {
        try
        {
            return QuestManager.IsQuestComplete(questId);
        }
        catch
        {
            return false;
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

    int IAbilityState.LevelOf(uint classJobId) => LevelOfJob(classJobId);

    /// <summary>
    /// Daedalus's check (<c>ActionUnlockHelper.IsUnlockLinkSatisfied</c>): an unlock link, or a quest
    /// written into one, is done. Readable from any job. Unreadable is answered yes — the game
    /// refusing the action is a better way to learn it than refusing work on a bad read.
    /// </summary>
    public bool UnlockDone(uint unlockLink)
    {
        if (unlockLink == 0)
            return true;
        try
        {
            var state = UIState.Instance();
            return state == null || state->IsUnlockLinkUnlockedOrQuestCompleted(unlockLink);
        }
        catch
        {
            return true;
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
