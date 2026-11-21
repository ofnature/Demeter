using Demeter.Services.Gathering;

namespace Demeter.Tests;

public class GatherListTests
{
    [Fact]
    public void An_item_goes_on_a_list_once_at_the_default_target()
    {
        var list = new GatherList();

        Assert.True(list.Add(5111));
        Assert.False(list.Add(5111));

        var only = Assert.Single(list.Items);
        Assert.Equal(5111u, only.ItemId);
        Assert.Equal(99, only.TargetCount);
    }

    [Fact]
    public void Only_indexed_items_are_gatherable()
    {
        var index = new GatherableIndex([(5111u, "Wind Crystal")]);

        Assert.True(index.Contains(5111));
        Assert.False(index.Contains(4787));
    }
}
