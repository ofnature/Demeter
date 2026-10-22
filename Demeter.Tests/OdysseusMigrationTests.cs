using Demeter.Config;
using Demeter.Services.Gathering;

namespace Demeter.Tests;

public class OdysseusMigrationTests
{
    /// <summary>The shape Dalamud writes Odysseus's config in: every object stamped with Odysseus's own type names.</summary>
    private const string OdysseusJson = """
        {
          "$type": "Odysseus.Config.OdysseusConfig, Odysseus",
          "OwnGathering": true,
          "GatherLists": [
            {
              "$type": "Odysseus.Services.Gathering.GatherList, Odysseus",
              "Name": "Shards", "Enabled": false, "RemoveCompleted": false,
              "Items": [
                { "$type": "Odysseus.Services.Gathering.GatherListItem, Odysseus", "ItemId": 5, "TargetCount": 99 },
                { "$type": "Odysseus.Services.Gathering.GatherListItem, Odysseus", "ItemId": 2, "TargetCount": 40 }
              ]
            },
            { "$type": "Odysseus.Services.Gathering.GatherList, Odysseus", "Name": "Hephaestus", "Enabled": true, "RemoveCompleted": true, "Items": [] }
          ],
          "GatherHome": 4,
          "RepairAtPercent": 25,
          "PathingProvider": "Ariadne"
        }
        """;

    [Fact]
    public void Odysseus_lists_are_read_past_its_type_names()
    {
        var read = OdysseusGatherSettings.Parse(OdysseusJson)!;

        Assert.Equal(2, read.Lists.Count);
        var shards = read.Lists[0];
        Assert.Equal("Shards", shards.Name);
        Assert.False(shards.Enabled);
        Assert.Equal([5u, 2u], shards.Items.Select(i => i.ItemId));
        Assert.Equal(40, shards.Items[1].TargetCount);
        Assert.True(read.Lists[1].RemoveCompleted);
        Assert.Equal(GatherHome.FreeCompany, read.Home);
        Assert.Equal(25, read.RepairAtPercent);
        Assert.Equal("Ariadne", read.PathingProvider);
    }

    [Fact]
    public void A_fresh_Demeter_takes_them_once()
    {
        var config = new DemeterConfig();
        Assert.True(OdysseusGatherSettings.MigrateInto(config, OdysseusGatherSettings.Parse(OdysseusJson)));
        Assert.Equal(2, config.GatherLists.Count);
        Assert.Equal(GatherHome.FreeCompany, config.GatherHome);
        Assert.Equal("Ariadne", config.PathingProvider);
        Assert.True(config.OdysseusListsMigrated);

        // Deleted here, never brought back.
        config.GatherLists.Clear();
        Assert.False(OdysseusGatherSettings.MigrateInto(config, OdysseusGatherSettings.Parse(OdysseusJson)));
        Assert.Empty(config.GatherLists);
    }

    [Fact]
    public void A_Demeter_with_lists_of_its_own_keeps_them()
    {
        var config = new DemeterConfig { RepairAtPercent = 50 };
        config.GatherLists.Add(new GatherList { Name = "Mine" });

        Assert.True(OdysseusGatherSettings.MigrateInto(config, OdysseusGatherSettings.Parse(OdysseusJson)));
        Assert.Equal("Mine", Assert.Single(config.GatherLists).Name);
        Assert.Equal(50, config.RepairAtPercent);
        Assert.True(config.OdysseusListsMigrated);
    }

    [Fact]
    public void No_Odysseus_config_is_simply_nothing_to_bring()
    {
        var config = new DemeterConfig();
        Assert.Null(OdysseusGatherSettings.ReadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Odysseus.json")));
        Assert.True(OdysseusGatherSettings.MigrateInto(config, null));
        Assert.Empty(config.GatherLists);
        Assert.True(config.OdysseusListsMigrated);
    }

    [Fact]
    public void Garbage_reads_as_nothing()
    {
        Assert.Null(OdysseusGatherSettings.Parse("not json"));
        Assert.Null(OdysseusGatherSettings.Parse("[]"));
    }

    [Fact]
    public void Odysseus_json_sits_beside_Demeters_config_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "XIVLauncher", "pluginConfigs");
        var dir = Path.Combine(root, "Demeter");
        Assert.Equal(Path.Combine(root, "Odysseus.json"), OdysseusGatherSettings.PathBeside(dir));
        Assert.Equal(Path.Combine(root, "Odysseus.json"), OdysseusGatherSettings.PathBeside(dir + Path.DirectorySeparatorChar));
    }
}
