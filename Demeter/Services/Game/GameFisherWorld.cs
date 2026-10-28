using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Demeter.Services.Fishing;
using Demeter.Services.Gathering;

namespace Demeter.Services.Game;

/// <summary>
/// The real <see cref="IFisherWorld"/>.
///
/// <para>
/// Almost all of it is ClientStructs: the fishing state, the bait and the mooch flags off the
/// <c>FishingEventHandler</c>; the catch reported by a hook on <c>AgentCatch.UpdateCatch</c>, taken
/// from ClientStructs' own function pointer — no signature. The pattern is GatherBuddy Reborn's
/// fishing parser (Apache-2.0; see NOTICE.md).
/// </para>
///
/// <para>
/// One exception, decided with the user 2026-10-22: <b>the tug</b>. ClientStructs does not lay it
/// out, and GatherBuddy and AutoHook both read it from a static address found by the signature below
/// — the only raw signature in Demeter. When it is not found (a patch moved it) the tug reads Unknown
/// and the fisher judges bites by their timing alone.
/// </para>
///
/// <para>
/// The catch hook is enabled only while a fishing run is going, like every hook should be: a hook
/// left running on code the game calls for everyone is a cost nobody asked for.
/// </para>
/// </summary>
public sealed unsafe class GameFisherWorld : IFisherWorld, IDisposable
{
    /// <summary>GatherBuddy Reborn's <c>SeTugType</c> signature; the byte there is 36/37/38 for light/medium/heavy.</summary>
    private const string TugSignature = "48 8D 35 ?? ?? ?? ?? 4C 8B CE";

    private readonly GameBasics _basics;
    private readonly GameGatherWorld _gather;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly Action<string> _log;
    private readonly nint _tugAddress;
    private readonly Hook<AgentCatch.Delegates.UpdateCatch>? _catchHook;
    private readonly ConcurrentQueue<Fishing.Catch> _catches = new();

    public GameFisherWorld(GameBasics basics, GameGatherWorld gather, IGameGui gameGui, IObjectTable objectTable,
        ISigScanner sigScanner, IGameInteropProvider interop, Action<string> log)
    {
        _basics = basics;
        _gather = gather;
        _gameGui = gameGui;
        _objectTable = objectTable;
        _log = log;

        try
        {
            _tugAddress = sigScanner.GetStaticAddressFromSig(TugSignature);
        }
        catch (Exception ex)
        {
            _tugAddress = 0;
            log($"The tug cannot be read this patch ({ex.GetType().Name}) — bites are judged by their timing alone.");
        }

        try
        {
            _catchHook = interop.HookFromAddress<AgentCatch.Delegates.UpdateCatch>(
                (nint)AgentCatch.MemberFunctionPointers.UpdateCatch, OnCatch);
        }
        catch (Exception ex)
        {
            log($"The catch report could not be hooked ({ex.GetType().Name}: {ex.Message}) — catches are counted from the bag only.");
        }
    }

    /// <summary>Hear catches while fishing; not otherwise.</summary>
    public bool Listening
    {
        get => _catchHook?.IsEnabled ?? false;
        set
        {
            if (_catchHook is null || _catchHook.IsEnabled == value)
                return;
            if (value) _catchHook.Enable();
            else _catchHook.Disable();
        }
    }

    public bool TugReadable => _tugAddress != 0;

    public DateTime UtcNow => DateTime.UtcNow;
    public bool IsOccupied => _basics.IsOccupied;

    private static FishingEventHandler* Handler
    {
        get
        {
            var framework = EventFramework.Instance();
            return framework == null ? null : framework->EventHandlerModule.FishingEventHandler;
        }
    }

    public FishState State
    {
        get
        {
            try
            {
                var handler = Handler;
                return handler == null ? FishState.None : (FishState)(int)handler->State;
            }
            catch
            {
                return FishState.None;
            }
        }
    }

    public Tug Tug
    {
        get
        {
            if (_tugAddress == 0)
                return Tug.Unknown;
            try
            {
                return *(byte*)_tugAddress switch
                {
                    36 => Tug.Light,
                    37 => Tug.Medium,
                    38 => Tug.Heavy,
                    _ => Tug.Unknown,
                };
            }
            catch
            {
                return Tug.Unknown;
            }
        }
    }

    public uint CurrentBait
    {
        get
        {
            var state = PlayerState.Instance();
            return state == null ? 0 : state->FishingBait;
        }
    }

    public int ItemCount(uint itemId) => _basics.ItemCount(itemId);

    /// <summary>The game's own bait switch (command 701, sub 4) — what the bait window sends.</summary>
    public bool ChangeBait(uint baitId)
    {
        try
        {
            return GameMain.ExecuteCommand(701, 4, (int)baitId, 0, 0);
        }
        catch (Exception ex)
        {
            _log($"Changing bait failed: {ex.Message}");
            return false;
        }
    }

    public bool CanMooch
    {
        get
        {
            var handler = Handler;
            return handler != null && handler->CanMoochPreviousCatch;
        }
    }

    public bool CanMooch2
    {
        get
        {
            var handler = Handler;
            return handler != null && handler->CanMooch2PreviousCatch;
        }
    }

    public bool HasStatus(uint statusId)
    {
        if (_objectTable.LocalPlayer is not { } player)
            return false;
        foreach (var status in player.StatusList)
            if (status.StatusId == statusId)
                return true;
        return false;
    }

    public bool UseAction(uint actionId)
    {
        try
        {
            var manager = ActionManager.Instance();
            return manager != null && manager->UseAction(ActionType.Action, actionId);
        }
        catch (Exception ex)
        {
            _log($"Action {actionId} failed: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<Fishing.Catch> TakeCatches()
    {
        var result = new List<Fishing.Catch>();
        while (_catches.TryDequeue(out var c))
            result.Add(c);
        return result;
    }

    public void Log(string message) => _log(message);

    public int CollectableCount(uint itemId, int minimumCollectability) => _gather.CollectableCount(itemId, minimumCollectability);

    /// <summary>
    /// The "keep as a collectable?" yes/no. GatherBuddy Reborn's reading (AutoGather.FishingCollectable,
    /// Apache-2.0): the item is value 14, 500,000 above its id; the collectability is the first number
    /// in the prompt's text (value 0).
    /// </summary>
    public (uint ItemId, int Collectability)? CollectablePrompt
    {
        get
        {
            try
            {
                var addon = _gameGui.GetAddonByName("SelectYesno");
                if (addon.IsNull || !addon.IsVisible)
                    return null;
                var unit = (AtkUnitBase*)addon.Address;
                if (unit->AtkValuesCount < 15)
                    return null;
                var encoded = unit->AtkValues[14].UInt;
                if (encoded < 500_000)
                    return null;
                var text = unit->AtkValues[0].GetValueAsString();
                var number = Regex.Match(text, @"\d+");
                return number.Success && int.TryParse(number.Value, out var value) ? (encoded - 500_000, value) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public void AnswerCollectable(bool keep)
    {
        try
        {
            var addon = _gameGui.GetAddonByName("SelectYesno");
            if (!addon.IsNull && addon.IsVisible)
                ((AtkUnitBase*)addon.Address)->FireCallbackInt(keep ? 0 : 1);
        }
        catch (Exception ex)
        {
            _log($"Answering the collectable prompt failed: {ex.Message}");
        }
    }

    private void OnCatch(AgentCatch* thisPtr, uint itemId, bool isLarge, ushort size, byte amount, byte level, byte stars,
        byte oceanStars, bool isMoochable, bool isFirstTimeCatch, byte a11, byte a12)
    {
        // The game's own popup first, untouched.
        _catchHook!.Original(thisPtr, itemId, isLarge, size, amount, level, stars, oceanStars, isMoochable, isFirstTimeCatch, a11, a12);
        try
        {
            // Collectables are reported 500,000 above the item id.
            var collectable = itemId > 500_000;
            _catches.Enqueue(new Fishing.Catch(collectable ? itemId - 500_000 : itemId, Math.Max((int)amount, 1), collectable, isLarge));
        }
        catch
        {
            // a catch report is never worth a fault inside the game's own call
        }
    }

    public void Dispose()
    {
        _catchHook?.Dispose();
    }
}
