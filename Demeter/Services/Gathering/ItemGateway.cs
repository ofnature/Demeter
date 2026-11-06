using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demeter.Services.Gathering;

/// <summary>What <c>Demeter.Item.GetStatusJson</c> answers. camelCase, extend-only.</summary>
public sealed class ItemStatusDto
{
    public bool Busy { get; set; }
    public bool Faulted { get; set; }
    public bool AtNode { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>What <c>Demeter.Item.DescribeJson</c> answers: where an item is gathered and when. camelCase, extend-only.</summary>
public sealed class ItemDescriptionDto
{
    /// <summary>The zone the gatherer would go to; null when it cannot place the item.</summary>
    public uint? ZoneId { get; set; }

    /// <summary>"Yak T'el — Miner Lv 90", for people. Empty when the sheets do not say.</summary>
    public string Where { get; set; } = string.Empty;

    /// <summary>"up · 4:12 left" or "in 12:34" for a timed-only item; empty otherwise.</summary>
    public string Timer { get; set; } = string.Empty;

    /// <summary>Timed-only: seconds until it can be gathered (0 = now). Null when not timed-only.</summary>
    public double? WaitSeconds { get; set; }

    /// <summary>Timed-only and up now: seconds it stays up. Null otherwise.</summary>
    public double? UpForSeconds { get; set; }
}

/// <summary>
/// One item gathered for another plugin — the gate Odysseus's quest Gather steps and custom
/// deliveries use, where the gather lists' gate is Hephaestus's.
///
/// <para>
/// The caller drives nothing: Demeter ticks the gatherer itself while a request of this kind is
/// in hand, and the caller polls <see cref="StatusJson"/>. The request is refused while a gather
/// list runs, and the list gates refuse while one of these is in hand — two masters for one
/// character is the one thing this must never allow.
/// </para>
/// </summary>
public sealed class ItemGateway
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IOwnGatherer _gatherer;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _listsRunning;
    private readonly Action<string> _log;

    public ItemGateway(IOwnGatherer gatherer, Func<bool> enabled, Func<bool> listsRunning, Action<string> log)
    {
        _gatherer = gatherer;
        _enabled = enabled;
        _listsRunning = listsRunning;
        _log = log;
    }

    /// <summary>A request of ours is in hand: Demeter ticks the gatherer for it.</summary>
    public bool IsRunning { get; private set; }

    public bool CanGather(uint itemId, uint territoryHint)
        => _enabled() && _gatherer.CanGather(itemId, territoryHint);

    public string WhyNot(uint itemId, uint territoryHint)
        => !_enabled() ? "Demeter is switched off" : _gatherer.WhyNot(itemId, territoryHint);

    public string DescribeJson(uint itemId, DateTime utc)
    {
        try
        {
            return JsonSerializer.Serialize(new ItemDescriptionDto
            {
                ZoneId = _gatherer.ZoneOf(itemId),
                Where = _gatherer.Where(itemId),
                Timer = _gatherer.Timer(itemId, utc),
                WaitSeconds = _gatherer.WaitFor(itemId, utc)?.TotalSeconds,
                UpForSeconds = _gatherer.UpFor(itemId, utc)?.TotalSeconds,
            }, Json);
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>True means the gatherer began, and <c>Demeter.IsBusy</c> already reads true.</summary>
    public bool Start(uint itemId, int count, int collectability, uint territoryHint)
    {
        if (!_enabled())
        {
            _log($"Asked to gather item {itemId}, but Demeter is switched off — refused.");
            return false;
        }
        if (_listsRunning() || (_gatherer.Busy && !IsRunning))
        {
            _log($"Asked to gather item {itemId} while a gather list is running — refused.");
            return false;
        }
        if (count <= 0)
        {
            _log($"Asked to gather {count} of item {itemId} — nothing to do.");
            return false;
        }
        if (IsRunning)
            _gatherer.Stop();   // a new ask from the same caller replaces the old one
        IsRunning = _gatherer.Start(itemId, count, collectability, territoryHint);
        return IsRunning;
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        _gatherer.Stop();
    }

    /// <summary>
    /// Finish the node or cast in hand, then stop; <see cref="IsRunning"/> falls once the character
    /// is free. False when no request is in hand. A new <see cref="Start"/> clears it.
    /// </summary>
    public bool RequestStopAfterRun()
    {
        if (!IsRunning)
            return false;
        _gatherer.RequestStopAfterRun();
        return true;
    }

    /// <summary>Every frame. Drives the gatherer while ours, and lets go once it has finished (closing included).</summary>
    public void Tick()
    {
        if (!IsRunning)
            return;
        _gatherer.Tick();
        if (!_gatherer.Busy)
            IsRunning = false;
    }

    public string StatusJson()
    {
        try
        {
            return JsonSerializer.Serialize(new ItemStatusDto
            {
                Busy = IsRunning && _gatherer.Busy,
                Faulted = _gatherer.Faulted,
                AtNode = _gatherer.AtNode,
                Status = _gatherer.Status,
            }, Json);
        }
        catch
        {
            return "{}";
        }
    }
}
