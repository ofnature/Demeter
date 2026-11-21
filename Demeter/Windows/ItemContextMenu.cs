using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using Demeter.Config;
using Demeter.Services.Gathering;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Demeter.Windows;

/// <summary>
/// Right-clicking a gatherable item in the inventory or the crafting log offers it to Demeter:
/// onto the list on screen in the gather lists window, or onto a new list of its own. Items no
/// node or hole gives are not offered — a list could never gather them.
/// </summary>
public sealed class ItemContextMenu : IDisposable
{
    private readonly IContextMenu _menu;
    private readonly DemeterConfig _config;
    private readonly Action _save;
    private readonly GatherableIndex _gatherables;
    private readonly GatherListsWindow _listsWindow;
    private readonly Action<string> _log;

    public ItemContextMenu(IContextMenu menu, DemeterConfig config, Action save, GatherableIndex gatherables,
        GatherListsWindow listsWindow, Action<string> log)
    {
        _menu = menu;
        _config = config;
        _save = save;
        _gatherables = gatherables;
        _listsWindow = listsWindow;
        _log = log;
        _menu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => _menu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (ItemIdOf(args) is not { } itemId || !_gatherables.Contains(itemId))
            return;

        var lists = _config.GatherLists;
        if (lists.Count > 0)
        {
            var current = lists[_listsWindow.SelectedIndex];
            args.AddMenuItem(new MenuItem
            {
                Name = $"Add to Demeter: {current.Name}",
                PrefixChar = 'D',
                PrefixColor = IMenuItem.DalamudDefaultPrefixColor,
                OnClicked = _ => AddTo(current, itemId),
            });
        }
        args.AddMenuItem(new MenuItem
        {
            Name = "Add to new Demeter list",
            PrefixChar = 'D',
            PrefixColor = IMenuItem.DalamudDefaultPrefixColor,
            OnClicked = _ => AddToNewList(itemId),
        });
    }

    /// <summary>
    /// The item the menu is about. The inventory hands it over; the crafting log's menu is a
    /// plain one, and the item right-clicked there (the recipe's result or a material) is kept on
    /// the recipe agent.
    /// </summary>
    private static unsafe uint? ItemIdOf(IMenuOpenedArgs args)
    {
        // HQ and collectable copies carry an offset on the id; the list wants the plain item.
        if (args.MenuType == ContextMenuType.Inventory)
            return args.Target is MenuTargetInventory { TargetItem: { } item } ? item.BaseItemId : null;
        if (args.AddonName != "RecipeNote")
            return null;
        var agent = AgentRecipeNote.Instance();
        if (agent == null || agent->ContextMenuResultItemId == 0)
            return null;
        var id = agent->ContextMenuResultItemId;
        return id >= 1_000_000 ? id - 1_000_000 : id >= 500_000 ? id - 500_000 : id;
    }

    private void AddTo(GatherList list, uint itemId)
    {
        var name = _gatherables.NameOf(itemId);
        if (list.Add(itemId))
        {
            _save();
            _log($"Added {name} to the gather list \"{list.Name}\".");
        }
        else
            _log($"{name} is already on the gather list \"{list.Name}\".");
        Show(list);
    }

    private void AddToNewList(uint itemId)
    {
        var list = new GatherList { Name = _gatherables.NameOf(itemId) };
        list.Add(itemId);
        _config.GatherLists.Add(list);
        _save();
        _log($"Made the gather list \"{list.Name}\".");
        Show(list);
    }

    /// <summary>The lists window, open on the list just changed.</summary>
    private void Show(GatherList list)
    {
        _listsWindow.SelectedIndex = _config.GatherLists.IndexOf(list);
        _listsWindow.IsOpen = true;
    }
}
