using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Demeter.Services.Fishing;

/// <summary>What <c>Demeter.Fish.CanFishHere</c> answers. camelCase, extend-only.</summary>
public sealed class FishCheckDto
{
    public bool Ok { get; set; }
    public string Code { get; set; } = "none";
    public string Reason { get; set; } = string.Empty;
}

/// <summary>What <c>Demeter.Fish.GetStatusJson</c> answers. camelCase, extend-only.</summary>
public sealed class FishStatusDto
{
    /// <summary>idle, running, finishing, done, stoppedAsAsked or faulted.</summary>
    public string State { get; set; } = "idle";
    public string Code { get; set; } = "none";
    public string Reason { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public int Casts { get; set; }

    /// <summary>Item id (as text, JSON keys being text) to how many were landed.</summary>
    public Dictionary<string, int> Landed { get; set; } = new();
    public FishBiteDto? LastBite { get; set; }
    public bool Amiss { get; set; }
}

public sealed class FishBiteDto
{
    public double Seconds { get; set; }

    /// <summary>light, medium, heavy or unknown.</summary>
    public string Tug { get; set; } = "unknown";
    public bool Hooked { get; set; }
}

/// <summary>
/// <c>Demeter.Fish.*</c>: fish from where the character stands, for a caller that chose the place
/// (Selene). JSON in, JSON out; a request is refused while anything else of Demeter's is driving the
/// character, and everything else is refused while one of these runs.
/// </summary>
public sealed class FishGateway
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly FishSession _session;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _othersBusy;
    private readonly Action<string> _log;

    /// <param name="othersBusy">Anything else of Demeter's driving the character: a list, an item, the Fish window's runs.</param>
    public FishGateway(FishSession session, Func<bool> enabled, Func<bool> othersBusy, Action<string> log)
    {
        _session = session;
        _enabled = enabled;
        _othersBusy = othersBusy;
        _log = log;
    }

    /// <summary>Why the last <see cref="Start"/> was refused; empty after one that began.</summary>
    public string LastRefusal { get; private set; } = string.Empty;

    public string CanFishHere(string json)
    {
        var (code, reason) = Check(json, out _);
        return Serialize(new FishCheckDto { Ok = code == FishSessionCode.None, Code = Text(code), Reason = reason });
    }

    /// <summary>True when the session began; <c>Demeter.IsBusy</c> reads true from then.</summary>
    public bool Start(string json)
    {
        var (code, reason) = Check(json, out var request);
        if (code == FishSessionCode.None && _session.Begin(request!))
        {
            LastRefusal = string.Empty;
            return true;
        }
        if (code == FishSessionCode.None)
            (code, reason) = (_session.Code, _session.Reason);
        LastRefusal = reason;
        _log($"Asked to fish here{(request?.RequestedBy is { Length: > 0 } who ? $" by {who}" : string.Empty)} — refused: {reason} ({Text(code)}).");
        return false;
    }

    public void Stop() => _session.Stop();

    public string StatusJson()
    {
        try
        {
            return Serialize(new FishStatusDto
            {
                State = Text(_session.State),
                Code = Text(_session.Code),
                Reason = _session.Reason,
                RequestedBy = _session.RequestedBy,
                Casts = _session.Casts,
                Landed = _session.Landed.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                LastBite = _session.LastBite is { } b
                    ? new FishBiteDto { Seconds = b.Seconds, Tug = b.Tug.ToString().ToLowerInvariant(), Hooked = b.Hooked }
                    : null,
                Amiss = _session.AmissSeen,
            });
        }
        catch
        {
            return "{}";
        }
    }

    private (FishSessionCode Code, string Reason) Check(string json, out FishSessionRequest? request)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize<FishSessionRequest>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Json)
                      ?? new FishSessionRequest();
        }
        catch (JsonException ex)
        {
            return (FishSessionCode.BadRequest, $"the request is not JSON Demeter reads ({ex.Message})");
        }
        if (!_enabled())
            return (FishSessionCode.Off, "Demeter is switched off");
        if (_session.Busy)
            return (FishSessionCode.Busy, $"already fishing here for {(_session.RequestedBy.Length > 0 ? _session.RequestedBy : "a caller")}");
        if (_othersBusy())
            return (FishSessionCode.Busy, "Demeter is busy with something else");
        return _session.Check(request);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string Text<T>(T value) where T : Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
