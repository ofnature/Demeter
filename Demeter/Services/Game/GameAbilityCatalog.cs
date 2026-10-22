using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Demeter.Services.Gathering;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaQuest = Lumina.Excel.Sheets.Quest;

namespace Demeter.Services.Game;

/// <summary>
/// Every Miner, Botanist and Fisher action off the Action sheet, with its level and what unlocks it.
/// Daedalus's <c>JobActionDumpService.Collect</c> and <c>DebugService.UnlockQuestFor</c> are the
/// pattern: rows for the class (by <c>ClassJob</c>, or by a <c>ClassJobCategory</c> that names only
/// that class — some actions are keyed that way), not PvP, learned at a level; an <c>UnlockLink</c>
/// above 0x10000 is a Quest row and names the quest.
/// </summary>
public static class GameAbilityCatalog
{
    public static IReadOnlyList<GatherAbility> Read(IDataManager data, Action<string>? log = null)
    {
        var result = new List<GatherAbility>();
        try
        {
            var quests = data.GetExcelSheet<LuminaQuest>();
            foreach (var row in data.GetExcelSheet<LuminaAction>())
            {
                if (row.IsPvP || row.ClassJobLevel == 0 || !row.IsPlayerAction)
                    continue;
                var job = JobOf(row);
                if (job == 0)
                    continue;
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var link = row.UnlockLink.RowId;
                string? quest = null;
                if (link > GatheringAbilities.QuestLinkBase && quests.GetRowOrDefault(link) is { } q)
                    quest = q.Name.ExtractText() is { Length: > 0 } qn ? qn : $"quest #{link - GatheringAbilities.QuestLinkBase}";
                result.Add(new GatherAbility(row.RowId, name, job, row.ClassJobLevel, link, quest));
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Reading the gatherer actions failed ({ex.GetType().Name}: {ex.Message}) — every action is assumed learned.");
        }
        return result;
    }

    private static uint JobOf(LuminaAction row)
    {
        var job = row.ClassJob.RowId;
        if (job is GatheringAbilities.Miner or GatheringAbilities.Botanist or GatheringAbilities.Fisher)
            return job;
        if (row.ClassJobCategory.ValueNullable is not { } category)
            return 0;
        // A category naming exactly one gatherer class (and no other gatherer) belongs to it.
        var min = category.MIN;
        var btn = category.BTN;
        var fsh = category.FSH;
        return (min, btn, fsh) switch
        {
            (true, false, false) => GatheringAbilities.Miner,
            (false, true, false) => GatheringAbilities.Botanist,
            (false, false, true) => GatheringAbilities.Fisher,
            _ => 0,
        };
    }
}
