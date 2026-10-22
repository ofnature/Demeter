using System.Text.Json;
using Demeter.Services.Gathering;

namespace Demeter.Tests;

public class ItemGatewayTests
{
    /// <summary>A gatherer that takes a few ticks to finish, then reports done (or faulted).</summary>
    private sealed class Own : IOwnGatherer
    {
        public int TicksToFinish { get; set; } = 3;
        public bool FaultAtEnd { get; set; }
        public List<string> Calls { get; } = [];
        private int _left;

        public bool Enabled { get; set; } = true;
        public bool CanGather(uint itemId, uint territoryHint = 0) => Enabled && itemId != 99;
        public string WhyNot(uint itemId, uint territoryHint = 0) => "no node";
        public uint? ZoneOf(uint itemId) => 1187;
        public string Where(uint itemId) => "Yak T'el — Miner Lv 90";
        public string Timer(uint itemId, DateTime utc) => "up · 4:12 left";
        public TimeSpan? WaitFor(uint itemId, DateTime utc) => TimeSpan.Zero;
        public TimeSpan? UpFor(uint itemId, DateTime utc) => TimeSpan.FromSeconds(252);

        public bool Start(uint itemId, int count, int collectability, uint territoryHint = 0)
        {
            Calls.Add($"Start {itemId}x{count}@{collectability} hint {territoryHint}");
            _left = TicksToFinish;
            Busy = true;
            Faulted = false;
            return true;
        }

        public void Tick()
        {
            Calls.Add("Tick");
            if (Busy && --_left <= 0)
            {
                Busy = false;
                Faulted = FaultAtEnd;
            }
        }

        public bool Busy { get; private set; }
        public bool Faulted { get; private set; }
        public bool AtNode => Busy;
        public string Status => Faulted ? "could not reach it" : Busy ? "working" : "done";
        public bool DryRun { get; set; }
        public bool ProbeOnly { get; set; }
        public void Stop() => Calls.Add("Stop");
    }

    private static (ItemGateway Gateway, Own Own, List<string> Log) Make(bool enabled = true, bool listsRunning = false)
    {
        var own = new Own();
        var log = new List<string>();
        return (new ItemGateway(own, () => enabled, () => listsRunning, log.Add), own, log);
    }

    [Fact]
    public void Start_passes_everything_through_and_ticks_until_done()
    {
        var (gateway, own, _) = Make();
        Assert.True(gateway.Start(44850, 6, 240, 1187));
        Assert.Contains("Start 44850x6@240 hint 1187", own.Calls);
        Assert.True(gateway.IsRunning);

        for (var i = 0; i < 10 && gateway.IsRunning; i++) gateway.Tick();
        Assert.False(gateway.IsRunning);
        Assert.Equal(3, own.Calls.Count(c => c == "Tick"));
    }

    [Fact]
    public void Switched_off_it_refuses_and_says_why()
    {
        var (gateway, _, log) = Make(enabled: false);
        Assert.False(gateway.CanGather(44850, 0));
        Assert.Equal("Demeter is switched off", gateway.WhyNot(44850, 0));
        Assert.False(gateway.Start(44850, 1, 0, 0));
        Assert.Contains(log, m => m.Contains("switched off"));
    }

    [Fact]
    public void A_running_gather_list_refuses_an_item_request()
    {
        var (gateway, own, _) = Make(listsRunning: true);
        Assert.False(gateway.Start(44850, 1, 0, 0));
        Assert.DoesNotContain(own.Calls, c => c.StartsWith("Start", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_hands_on_to_the_gatherer_and_ticking_continues_until_it_has_closed()
    {
        var (gateway, own, _) = Make();
        gateway.Start(44850, 1, 0, 0);
        gateway.Stop();
        Assert.Contains("Stop", own.Calls);
        Assert.True(gateway.IsRunning);   // the node window still has to shut — the runner's Closing
    }

    [Fact]
    public void Status_json_is_camel_case_and_carries_a_fault()
    {
        var (gateway, own, _) = Make();
        own.FaultAtEnd = true;
        own.TicksToFinish = 1;
        gateway.Start(44850, 1, 0, 0);
        gateway.Tick();

        using var doc = JsonDocument.Parse(gateway.StatusJson());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("busy").GetBoolean());
        Assert.True(root.GetProperty("faulted").GetBoolean());
        Assert.Equal("could not reach it", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("atNode", out _));
    }

    [Fact]
    public void Describe_json_says_where_and_when()
    {
        var (gateway, _, _) = Make();
        using var doc = JsonDocument.Parse(gateway.DescribeJson(44850, DateTime.UtcNow));
        var root = doc.RootElement;
        Assert.Equal(1187u, root.GetProperty("zoneId").GetUInt32());
        Assert.Equal("Yak T'el — Miner Lv 90", root.GetProperty("where").GetString());
        Assert.Equal(0, root.GetProperty("waitSeconds").GetDouble());
        Assert.Equal(252, root.GetProperty("upForSeconds").GetDouble());
    }
}
