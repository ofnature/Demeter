using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Demeter.Config;
using Demeter.Services.Fishing;
using Demeter.Services.Game;
using Demeter.Services.Gathering;
using Demeter.Services.Ipc;
using Demeter.Services.Travel;
using Demeter.Windows;
using static Demeter.Service;

namespace Demeter;

/// <summary>
/// Demeter — gathering (and, in time, fishing) for the plugin suite.
///
/// <para>
/// The gatherer that grew up inside Odysseus, moved out so that anything can ask for it: the
/// player's own gather lists, Hephaestus's material shortfalls, Odysseus's quest gathers and custom
/// deliveries. Everything here runs off one frame tick, and only one thing drives the character at
/// a time — a list run (the player's, or one another plugin asked for) or a single item for a
/// caller that drives its own flow.
/// </para>
///
/// <para>Design and phased scope live in <c>PLAN.md</c> at the repo root (local-only).</para>
/// </summary>
public sealed class DemeterPlugin : IDalamudPlugin
{
    private const string CommandMain = "/demeter";

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly WindowSystem _windowSystem = new("Demeter");
    private readonly DemeterConfig _config = null!;
    private readonly GameBasics _game = null!;
    private readonly OwnGatherer _gatherer = null!;
    private readonly GatherListRunner _lists = null!;
    private readonly GatherGateway _gateway = null!;
    private readonly ItemGateway _items = null!;
    private readonly DemeterIpc _ipc = null!;
    private readonly MainWindow _mainWindow = null!;
    private readonly GatherListsWindow _listsWindow = null!;
    private readonly DebugWindow? _debugWindow;
    private readonly FishWindow _fishWindow = null!;
    private readonly HoleApproach _approach = null!;
    private readonly FishingRun _fishing = null!;
    private readonly GameFisherWorld _fisherWorld = null!;
    private readonly Windows.Dock.DockLayoutShare _dockLayout = null!;
    private bool _listsWereRunning;
    private string? _homeCommand;

    public DemeterPlugin(IDalamudPluginInterface pluginInterface)
    {
        // Create<T> constructs a T. It must be given the service holder, never this plugin — see
        // Service for why that distinction silently kills the client.
        pluginInterface.Create<Service>();
        OpenOwnLog(); // needs the services above; before them it is a load error

        // Everything below is wrapped so a construction failure lands in a file we control —
        // Dalamud's own log stops writing once it hits its size cap.
        try
        {
            _config = PluginInterface.GetPluginConfig() as DemeterConfig ?? new DemeterConfig();
            if (OdysseusGatherSettings.MigrateInto(_config,
                    OdysseusGatherSettings.ReadFile(OdysseusGatherSettings.PathBeside(PluginInterface.ConfigDirectory.FullName))))
            {
                if (_config.GatherLists.Count > 0)
                    Say($"Brought {_config.GatherLists.Count} gather list(s) over from Odysseus.");
                PluginInterface.SavePluginConfig(_config);
            }
            DemeterTheme.SetMode(_config.Theme);

            var aetherytes = new AetheryteCatalog(DataManager, message => Warn(message));
            _game = new GameBasics(ClientState, ObjectTable, Condition, GameGui, DataManager,
                new AriadneIpc(PluginInterface, message => Warn(message)),
                new LifestreamIpc(PluginInterface, message => Warn(message)),
                new DaedalusIpc(PluginInterface), aetherytes,
                new ChatCommandSender(message => Warn(message)),
                message => Say(message));

            // What the character has learned, from the Action sheet: nothing is pressed that is not, and
            // the main window says what each class still lacks and which quest teaches it.
            var abilities = new GatheringAbilities(GameAbilityCatalog.Read(DataManager, message => Warn(message)), _game);
            var gatherWorld = new GameGatherWorld(ClientState, ObjectTable, Condition, GameGui, _game, message => Say(message))
            {
                Abilities = abilities,
            };
            var source = new GatheringSource(DataManager, message => Warn(message));
            var atlas = new NodeAtlas(NodeAtlas.PathBeside(PluginInterface.AssemblyLocation.DirectoryName), message => Warn(message));
            _gatherer = new OwnGatherer(new GatherRunner(gatherWorld, new Traveller(_game)), source, atlas, message => Say(message))
            {
                Enabled = _config.Enabled,
            };
            var gatherables = new GatherableIndex(DataManager, message => Warn(message));
            _lists = new GatherListRunner(_gatherer, _game.ItemCount, gatherables.NameOf, message => Say(message),
                new GearRepair(_game, message => Say(message)), () => _config.RepairAtPercent, () => _game.FreeBagSlots);

            // The list gate's promise is stricter than the gatherer's own CanGather: a node has to
            // exist AND this character has to be able to work it, because a crafting queue plans
            // on the answer.
            GatheringTarget? PlanFor(uint itemId) => _config.Enabled ? GatheringPlan.For(itemId, source, atlas) : null;
            _items = new ItemGateway(_gatherer, () => _config.Enabled,
                () => _lists.State == GatherListRunState.Running || FishingBusy, message => Say(message));
            _gateway = new GatherGateway(
                _lists,
                _config.GatherLists,
                SaveConfig,
                itemId => GatherReadiness.CanGather(PlanFor(itemId), _game.LevelOfJob),
                itemId => !_config.Enabled
                    ? "Demeter is switched off"
                    : GatherReadiness.WhyNot(PlanFor(itemId), _game.LevelOfJob, $"item {itemId}") ?? "nothing is in the way",
                _game.ItemCount,
                () => _lists.State == GatherListRunState.Running || _items.IsRunning || FishingBusy,
                message => Say(message));

            // Fishing, phase 1: what is known about every fish, and when each can next be caught.
            var fish = FishCatalog.Load(FishCatalog.PathBeside(PluginInterface.AssemblyLocation.DirectoryName), message => Warn(message));
            // Phase 2: getting to a hole — the community's recorded stand points, Lifestream and Ariadne
            // to get there, a test cast to prove it, and what worked remembered per hole.
            var holes = FishingHoles.Load(FishingHoles.PathBeside(PluginInterface.AssemblyLocation.DirectoryName), message => Warn(message));
            _approach = new HoleApproach(new GameFishingWorld(_game, gatherWorld, ObjectTable, Condition, message => Say(message)),
                new Traveller(_game), SaveConfig);
            // Phase 3: the fisher — cast, judge each bite, hook or let go, mooch, Intuition, stop at the count.
            var spots = new FishSpots(DataManager, message => Warn(message));
            _fisherWorld = new GameFisherWorld(_game, ObjectTable, SigScanner, GameInterop, message => Say(message));
            _fishing = new FishingRun(_fisherWorld, _approach, fish, holes, abilities.Knows, spots.RatesFor, spots.ItemName);
            _fishWindow = new FishWindow(fish, spots, holes, _approach, GoToHole, FishHere, _fishing,
                () => _lists.State == GatherListRunState.Running || _items.IsRunning);

            _listsWindow = new GatherListsWindow(_config, SaveConfig, _lists, gatherables, _gatherer, _game.ItemCount,
                () => _items.IsRunning || FishingBusy);
#if DEBUG
            _debugWindow = new DebugWindow(_gatherer, gatherWorld);
#endif
            _mainWindow = new MainWindow(new MainWindowDeps(
                _config, SaveConfig, _lists, _gateway, _items, _gatherer, gatherables.NameOf, abilities, _game.LevelOfJob,
                PluginLoaded, ToggleLists,
                _debugWindow is null ? null : () => _debugWindow.IsOpen = true,
                StopAll));

            _ipc = new DemeterIpc(PluginInterface, IsBusy, _gateway, _items, ToggleLists);

            _windowSystem.AddWindow(_mainWindow);
            _windowSystem.AddWindow(_listsWindow);
            _windowSystem.AddWindow(_fishWindow);
            if (_debugWindow is not null)
                _windowSystem.AddWindow(_debugWindow);

            // Minimized, the main window is a tab on the screen edge, stacked with Daedalus's (10),
            // Charon's (20) and Odysseus's (30). Any edge, any spot along it.
            _dockLayout = new Windows.Dock.DockLayoutShare(PluginInterface, "Demeter", order: 40);
            _windowSystem.AddWindow(new Windows.Dock.EdgeDockWindow(
            [
                new Windows.Dock.EdgeDockWindow.Tab("main", "Demeter", () => true, _mainWindow.DrawDocked, 360f, OpenMain),
            ],
                _ => (_config.EdgeTabEdge, _config.EdgeTabAlong),
                (_, edge, along) =>
                {
                    _config.EdgeTabEdge = edge;
                    _config.EdgeTabAlong = along;
                    SaveConfig();
                },
                _dockLayout,
                () => _config.MinimizedToTab && ClientState.IsLoggedIn));

            PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += OpenMain;
            PluginInterface.UiBuilder.OpenConfigUi += OpenMain;
            Framework.Update += OnFrameworkUpdate;

            CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Demeter. \"/demeter lists\" the gather lists, \"/demeter fish\" when and how a fish is caught, \"/demeter stop\" stops gathering"
#if DEBUG
                              + ", \"/demeter debug\" the gathering probes"
#endif
                              + ".",
            });

            Log.Information($"Demeter v{PluginVersion} loaded.");
        }
        catch (Exception ex)
        {
            WriteStartupError(pluginInterface, ex);
            throw;
        }
    }

    /// <summary>Full exception to <c>startup-error.txt</c> in the plugin's config directory, and to the log.</summary>
    private static void WriteStartupError(IDalamudPluginInterface pluginInterface, Exception ex)
    {
        try
        {
            Log.Error(ex, "Demeter failed to construct.");
            var dir = pluginInterface.ConfigDirectory.FullName;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "startup-error.txt"), $"{DateTime.Now:O}\nDemeter v{PluginVersion}\n\n{ex}\n");
        }
        catch
        {
            // Nothing left to report to.
        }
    }

    // ── Demeter's own log file ──
    // Dalamud's log stops writing at its size cap, and a run's own narration is the one thing worth
    // having when something goes wrong. So it also goes to demeter.log beside the config, moved
    // aside at start when it grows past a few MB. (Odysseus's pattern.)
    private StreamWriter? _ownLog;

    private void Say(string message) { Log.Information(message); WriteOwn("INF", message); }
    private void Warn(string message) { Log.Warning(message); WriteOwn("WRN", message); }

    private void OpenOwnLog()
    {
        try
        {
            Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
            var file = Path.Combine(PluginInterface.ConfigDirectory.FullName, "demeter.log");
            if (File.Exists(file) && new FileInfo(file).Length > 8_000_000)
                File.Move(file, Path.ChangeExtension(file, ".old.log"), overwrite: true);
            _ownLog = new StreamWriter(file, append: true) { AutoFlush = true };
            WriteOwn("INF", $"— Demeter v{typeof(DemeterPlugin).Assembly.GetName().Version} —");
        }
        catch (Exception ex)
        {
            Log.Warning($"Own log file unavailable ({ex.GetType().Name}: {ex.Message}); Dalamud's log only.");
        }
    }

    private void WriteOwn(string level, string message)
    {
        try { _ownLog?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}"); }
        catch { /* a log line is never worth a fault */ }
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        try { StopAll(); } catch { }
        _windowSystem.RemoveAllWindows();
        _ipc?.Dispose();
        _fisherWorld?.Dispose();
        try { _dockLayout?.Dispose(); } catch { }
        _ownLog?.Dispose();
    }

    /// <summary>Anything Demeter is driving — what <c>Demeter.IsBusy</c> answers, and what Daedalus fights for.</summary>
    private bool IsBusy() => _config.Enabled && (_lists.State == GatherListRunState.Running || _items.IsRunning || FishingBusy);

    /// <summary>Going to a hole, or fishing at one.</summary>
    private bool FishingBusy => _approach?.Busy == true || _fishing?.Busy == true;

    /// <summary>The Fish window's "Fish": go to the hole and catch this many of the fish.</summary>
    private void FishHere(FishInfo fish, int count, FishingHole hole, IReadOnlyList<StandPoint> recorded)
    {
        if (!_config.Enabled || _lists.State == GatherListRunState.Running || _items.IsRunning || FishingBusy)
            return;
        if (!_config.HoleMemory.TryGetValue(hole.Id, out var memory))
            _config.HoleMemory[hole.Id] = memory = new HoleMemory();
        _fisherWorld.Listening = true;
        _fishing.Begin(fish, count, hole, recorded, memory);
        Say($"Fishing {fish.Name} ×{count} at {hole.Name} ({hole.ZoneName}).");
    }

    /// <summary>The Fish window's "Go there": Fisher on, to the hole, a test cast, reeled in.</summary>
    private void GoToHole(FishingHole hole, IReadOnlyList<StandPoint> recorded)
    {
        if (!_config.Enabled || _lists.State == GatherListRunState.Running || _items.IsRunning || FishingBusy)
            return;
        if (!_config.HoleMemory.TryGetValue(hole.Id, out var memory))
            _config.HoleMemory[hole.Id] = memory = new HoleMemory();
        _approach.KeepFishing = false;
        _approach.Begin(hole, recorded, memory);
        Say($"Going to {hole.Name} ({hole.ZoneName}) to prove a stand point.");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _gatherer.Enabled = _config.Enabled;

        if (!_config.Enabled)
        {
            if (_lists.State == GatherListRunState.Running || _items.IsRunning || FishingBusy)
                StopAll();
        }

        // A single item for a caller that drives its own flow (Odysseus).
        if (_fishing.Busy)
        {
            _fishing.Tick();
            return;
        }
        _fisherWorld.Listening = false;

        if (_approach.Busy)
        {
            _approach.Tick();
            return;
        }

        if (_items.IsRunning)
        {
            _items.Tick();
            return;
        }

        // The lists — the player's, or one another plugin asked for — own the frame while they run.
        if (_lists.State == GatherListRunState.Running)
        {
            _listsWereRunning = true;
            _lists.Tick();
            _gateway.Tick();
            return;
        }
        if (_listsWereRunning)
        {
            _listsWereRunning = false;
            _gateway.Tick();
            // Remove-completed is per list, applied once the run has ended however it ended.
            var pruned = 0;
            foreach (var list in _config.GatherLists)
                if (list.RemoveCompleted)
                    pruned += list.Items.RemoveAll(i => _game.ItemCount(i.ItemId) >= i.TargetCount);
            if (pruned > 0)
            {
                SaveConfig();
                Say($"Gather lists: removed {pruned} completed item(s).");
            }
            // Going home is for a run that finished, not one you stopped. A request says for itself
            // whether to go home; the player's "Afterwards" is for the player's own runs, and sending
            // a crafter to the estate between crafts is not one.
            var requested = _gateway.TakeEndedRequest(out var requestWantsHome);
            if (_lists.State == GatherListRunState.Done && (!requested || requestWantsHome))
                _homeCommand = GatherHomeCommands.For(_config.GatherHome);
        }
        // Sent once the character can act — the last node's window has only just shut.
        if (_homeCommand is { } goHome && _game.IsReady && !_game.IsOccupied)
        {
            _homeCommand = null;
            Say($"Gather lists: heading home ({goHome}).");
            _game.SendChatCommand(goHome);
        }
    }

    private void StopAll()
    {
        if (_fishing.Busy)
            _fishing.Cancel();
        if (_approach.Busy)
            _approach.Cancel();
        _gateway.Stop();
        if (_lists.State == GatherListRunState.Running)
            _lists.Stop();
        _items.Stop();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "lists":
            case "gather":
                _listsWindow.IsOpen = true;
                break;
            case "fish":
                _fishWindow.IsOpen = true;
                break;
            case "stop":
                StopAll();
                break;
#if DEBUG
            case "debug":
                if (_debugWindow is not null)
                    _debugWindow.IsOpen = true;
                break;
#endif
            default:
                OpenMain();
                break;
        }
    }

    private void ToggleLists() => _listsWindow.IsOpen = !_listsWindow.IsOpen;

    private static bool PluginLoaded(string internalName)
    {
        try
        {
            return PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The ordinary window — out of the edge tab, if it was minimized there.</summary>
    private void OpenMain()
    {
        if (_config.MinimizedToTab)
        {
            _config.MinimizedToTab = false;
            SaveConfig();
        }
        _mainWindow.IsOpen = true;
    }

    private void SaveConfig()
    {
        DemeterTheme.SetMode(_config.Theme);
        PluginInterface.SavePluginConfig(_config);
    }
}
