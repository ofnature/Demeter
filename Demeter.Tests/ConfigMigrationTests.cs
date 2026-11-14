using Demeter.Config;
using Demeter.Services.Fishing;

namespace Demeter.Tests;

public class ConfigMigrationTests
{
    [Fact]
    public void Version_1_drops_every_bad_stand_point_once_and_keeps_the_good_ones()
    {
        var memory = new HoleMemory();
        memory.Failed(new StandPoint { X = 1, Z = 1 });
        memory.Worked(new StandPoint { X = 30, Z = 30 });
        var config = new DemeterConfig { Version = 1 };
        config.HoleMemory[36] = memory;

        Assert.True(config.Migrate());
        Assert.Empty(config.HoleMemory[36].Bad);
        Assert.Single(config.HoleMemory[36].Good);
        Assert.Equal(2, config.Version);

        config.HoleMemory[36].Failed(new StandPoint { X = 1, Z = 1 });
        Assert.False(config.Migrate());
        Assert.Single(config.HoleMemory[36].Bad);
    }
}
