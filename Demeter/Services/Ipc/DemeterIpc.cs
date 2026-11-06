using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Demeter.Services.Gathering;

namespace Demeter.Services.Ipc;

/// <summary>
/// What Demeter publishes for other plugins. Every gate is guarded: nothing Demeter throws ever
/// surfaces inside a caller, and a refusal is a false with the reason in Demeter's log.
///
/// <para>
/// <b><c>Demeter.IsBusy</c></b> — true while Demeter is driving the character, for any reason.
/// Daedalus polls it through its <c>AutomationBusyBridge</c> and fights for as long as it reads true:
/// a mob that takes a swing at the node circuit gets killed, and travel waits for it. Level-triggered
/// and read about once a second; if Demeter unloads mid-run the gate stops existing and Daedalus
/// reads idle — "rotation stops", never "rotation runs forever".
/// </para>
///
/// <para>
/// <b><c>Demeter.RequestStopAfterRun</c></b> — the suite's "stop after the run you are in": Argus
/// asks it of every automation plugin before it moves the character, then waits for
/// <c>Demeter.IsBusy</c> to read false. Demeter finishes the node or cast in hand and stops there.
/// </para>
///
/// <para>
/// <b>List level</b> (<c>Demeter.Gather.*</c>) is the shape Hephaestus used on Odysseus's
/// <c>Odysseus.Gather.*</c>, renamed: the caller's own named list, all-or-nothing validation.
/// <b>Item level</b> (<c>Demeter.Item.*</c>) is one item for a caller that drives its own flow —
/// Odysseus's quest gathers and custom deliveries.
/// </para>
/// </summary>
public sealed class DemeterIpc : IDisposable
{
    /// <summary>Bumped when a gate is added or a shape changes (extend-only).</summary>
    public const int ApiVersion = 2;

    public const string IsBusyGate = "Demeter.IsBusy";
    public const string RequestStopAfterRunGate = "Demeter.RequestStopAfterRun";
    public const string GetVersionGate = "Demeter.GetVersion";
    public const string ToggleListsGate = "Demeter.Ui.ToggleLists";

    public const string GatherCanGatherGate = "Demeter.Gather.CanGather";
    public const string GatherStartGate = "Demeter.Gather.Start";
    public const string GatherSetListGate = "Demeter.Gather.SetList";
    public const string GatherStopGate = "Demeter.Gather.Stop";
    public const string GatherIsRunningGate = "Demeter.Gather.IsRunning";
    public const string GatherStatusGate = "Demeter.Gather.GetStatusJson";

    public const string ItemCanGatherGate = "Demeter.Item.CanGather";
    public const string ItemWhyNotGate = "Demeter.Item.WhyNot";
    public const string ItemDescribeGate = "Demeter.Item.DescribeJson";
    public const string ItemStartGate = "Demeter.Item.Start";
    public const string ItemStopGate = "Demeter.Item.Stop";
    public const string ItemStatusGate = "Demeter.Item.GetStatusJson";

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<bool> _requestStopAfterRun;
    private readonly ICallGateProvider<int> _version;
    private readonly ICallGateProvider<object?> _toggleLists;
    private readonly ICallGateProvider<uint, bool> _gatherCanGather;
    private readonly ICallGateProvider<string, bool> _gatherStart;
    private readonly ICallGateProvider<string, bool> _gatherSetList;
    private readonly ICallGateProvider<object?> _gatherStop;
    private readonly ICallGateProvider<bool> _gatherIsRunning;
    private readonly ICallGateProvider<string> _gatherStatus;
    private readonly ICallGateProvider<uint, uint, bool> _itemCanGather;
    private readonly ICallGateProvider<uint, uint, string> _itemWhyNot;
    private readonly ICallGateProvider<uint, string> _itemDescribe;
    private readonly ICallGateProvider<uint, int, int, uint, bool> _itemStart;
    private readonly ICallGateProvider<object?> _itemStop;
    private readonly ICallGateProvider<string> _itemStatus;

    /// <param name="isBusy">Anything Demeter is driving. Read live; never latched.</param>
    /// <param name="requestStopAfterRun">Stop after the node or cast in hand; false (nothing armed) when idle.</param>
    /// <param name="gather">The list-level gateway: a caller's own named list.</param>
    /// <param name="items">The item-level gateway: one item, for a caller driving its own flow.</param>
    /// <param name="toggleLists">Show or hide the gather lists window.</param>
    public DemeterIpc(IDalamudPluginInterface pluginInterface, Func<bool> isBusy, Func<bool> requestStopAfterRun,
        GatherGateway gather, ItemGateway items, Action toggleLists)
    {
        _isBusy = pluginInterface.GetIpcProvider<bool>(IsBusyGate);
        _isBusy.RegisterFunc(() => Guard(isBusy, false));

        _requestStopAfterRun = pluginInterface.GetIpcProvider<bool>(RequestStopAfterRunGate);
        _requestStopAfterRun.RegisterFunc(() => Guard(requestStopAfterRun, false));

        _version = pluginInterface.GetIpcProvider<int>(GetVersionGate);
        _version.RegisterFunc(() => ApiVersion);

        _toggleLists = pluginInterface.GetIpcProvider<object?>(ToggleListsGate);
        _toggleLists.RegisterAction(() => Guard<object?>(() => { toggleLists(); return null; }, null));

        _gatherCanGather = pluginInterface.GetIpcProvider<uint, bool>(GatherCanGatherGate);
        _gatherCanGather.RegisterFunc(itemId => Guard(() => gather.CanGather(itemId), false));

        _gatherStart = pluginInterface.GetIpcProvider<string, bool>(GatherStartGate);
        _gatherStart.RegisterFunc(json => Guard(() => gather.Start(json), false));

        _gatherSetList = pluginInterface.GetIpcProvider<string, bool>(GatherSetListGate);
        _gatherSetList.RegisterFunc(json => Guard(() => gather.SetList(json), false));

        _gatherStop = pluginInterface.GetIpcProvider<object?>(GatherStopGate);
        _gatherStop.RegisterAction(() => Guard<object?>(() => { gather.Stop(); return null; }, null));

        // Told apart from Demeter.IsBusy on purpose: a caller needs to know that the run still going
        // is *its* run, not a list the player started while it was waiting.
        _gatherIsRunning = pluginInterface.GetIpcProvider<bool>(GatherIsRunningGate);
        _gatherIsRunning.RegisterFunc(() => Guard(() => gather.IsRunning, false));

        _gatherStatus = pluginInterface.GetIpcProvider<string>(GatherStatusGate);
        _gatherStatus.RegisterFunc(() => Guard(gather.StatusJson, "{}"));

        _itemCanGather = pluginInterface.GetIpcProvider<uint, uint, bool>(ItemCanGatherGate);
        _itemCanGather.RegisterFunc((itemId, hint) => Guard(() => items.CanGather(itemId, hint), false));

        _itemWhyNot = pluginInterface.GetIpcProvider<uint, uint, string>(ItemWhyNotGate);
        _itemWhyNot.RegisterFunc((itemId, hint) => Guard(() => items.WhyNot(itemId, hint), "Demeter could not say"));

        _itemDescribe = pluginInterface.GetIpcProvider<uint, string>(ItemDescribeGate);
        _itemDescribe.RegisterFunc(itemId => Guard(() => items.DescribeJson(itemId, DateTime.UtcNow), "{}"));

        _itemStart = pluginInterface.GetIpcProvider<uint, int, int, uint, bool>(ItemStartGate);
        _itemStart.RegisterFunc((itemId, count, collectability, hint)
            => Guard(() => items.Start(itemId, count, collectability, hint), false));

        _itemStop = pluginInterface.GetIpcProvider<object?>(ItemStopGate);
        _itemStop.RegisterAction(() => Guard<object?>(() => { items.Stop(); return null; }, null));

        _itemStatus = pluginInterface.GetIpcProvider<string>(ItemStatusGate);
        _itemStatus.RegisterFunc(() => Guard(items.StatusJson, "{}"));
    }

    public void Dispose()
    {
        _isBusy.UnregisterFunc();
        _requestStopAfterRun.UnregisterFunc();
        _version.UnregisterFunc();
        _toggleLists.UnregisterAction();
        _gatherCanGather.UnregisterFunc();
        _gatherStart.UnregisterFunc();
        _gatherSetList.UnregisterFunc();
        _gatherStop.UnregisterAction();
        _gatherIsRunning.UnregisterFunc();
        _gatherStatus.UnregisterFunc();
        _itemCanGather.UnregisterFunc();
        _itemWhyNot.UnregisterFunc();
        _itemDescribe.UnregisterFunc();
        _itemStart.UnregisterFunc();
        _itemStop.UnregisterAction();
        _itemStatus.UnregisterFunc();
    }

    /// <summary>Anything our side throws must stay our side; the caller gets the fallback.</summary>
    private static T Guard<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }
}
