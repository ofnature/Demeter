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
    /// <summary>
    /// idle, running, finishing, done, stoppedAsAsked or faulted for fishing; after a StandAt, standing
    /// (walking or turning), stood (there, facing the water), or faulted (code cannotReach).
    /// </summary>
    public string State { get; set; } = "idle";
    public string Code { get; set; } = "none";
    public string Reason { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public int Casts { get; set; }

    /// <summary>Item id (as text, JSON keys being text) to how many were landed.</summary>
    public Dictionary<string, int> Landed { get; set; } = new();
    public FishBiteDto? LastBite { get; set; }
    public bool Amiss { get; set; }

    /// <summary>Points the game said this session earned.</summary>
    public int Points { get; set; }

    /// <summary>The mission the scores are kept under; 0 for none.</summary>
    public uint Mission { get; set; }

    /// <summary>The mission's whole score, as the game keeps it.</summary>
    public uint MissionScore { get; set; }

    /// <summary>After a StandAt: the point stood at or walked to, 1-based (0 for none).</summary>
    public int StandPoint { get; set; }
    public int Gp { get; set; }
    public int MaxGp { get; set; }
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
    private readonly FishStand _stand;

    /// <summary>The last thing asked was a StandAt: the status speaks of it until a Start.</summary>
    private bool _standLast;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _othersBusy;
    private readonly Action<string> _log;

    /// <param name="othersBusy">Anything else of Demeter's driving the character: a list, an item, the Fish window's runs.</param>
    public FishGateway(FishSession session, FishStand stand, Func<bool> enabled, Func<bool> othersBusy, Action<string> log)
    {
        _session = session;
        _stand = stand;
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
            _standLast = false;
            return true;
        }
        if (code == FishSessionCode.None)
            (code, reason) = (_session.Code, _session.Reason);
        LastRefusal = reason;
        _log($"Asked to fish here{(request?.RequestedBy is { Length: > 0 } who ? $" by {who}" : string.Empty)} — refused: {reason} ({Text(code)}).");
        return false;
    }

    /// <summary>Walk to the first point that can be reached and face the water. True when the walk began.</summary>
    public bool StandAt(string json)
    {
        FishStandRequest? request = null;
        FishSessionCode code;
        string reason;
        try
        {
            request = JsonSerializer.Deserialize<FishStandRequest>(string.IsNullOrWhiteSpace(json) ? "{}" : json, Json)
                      ?? new FishStandRequest();
            (code, reason) = Gate(request.RequestedBy);
            if (code == FishSessionCode.None)
                (code, reason) = _stand.Check(request);
        }
        catch (JsonException ex)
        {
            (code, reason) = (FishSessionCode.BadRequest, $"the request is not JSON Demeter reads ({ex.Message})");
        }
        if (code == FishSessionCode.None && _stand.Begin(request!))
        {
            LastRefusal = string.Empty;
            _standLast = true;
            return true;
        }
        if (code == FishSessionCode.None)
            (code, reason) = (_stand.Code, _stand.Reason);
        LastRefusal = reason;
        _log($"Asked to stand at a fishing spot{(request?.RequestedBy is { Length: > 0 } who ? $" by {who}" : string.Empty)} — refused: {reason} ({Text(code)}).");
        return false;
    }

    /// <summary>Fishing: after the cast in hand. Walking to a point: now.</summary>
    public void Stop()
    {
        _stand.Cancel();
        _session.Stop();
    }

    public string StatusJson()
    {
        try
        {
            if (_standLast)
                return Serialize(new FishStatusDto
                {
                    State = _stand.State switch
                    {
                        FishStandState.Walking or FishStandState.Facing => "standing",
                        FishStandState.Stood => "stood",
                        FishStandState.Faulted => "faulted",
                        _ => "idle",
                    },
                    Code = Text(_stand.Code),
                    Reason = _stand.Reason,
                    RequestedBy = _stand.RequestedBy,
                    StandPoint = _stand.Point,
                    Mission = _session.Mission,
                    MissionScore = _session.MissionScore,
                    Gp = _session.Gp,
                    MaxGp = _session.MaxGp,
                });
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
                Points = _session.Points,
                Mission = _session.Mission,
                MissionScore = _session.MissionScore,
                Gp = _session.Gp,
                MaxGp = _session.MaxGp,
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
        var (code, reason) = Gate(request.RequestedBy);
        return code != FishSessionCode.None ? (code, reason) : _session.Check(request);
    }

    /// <summary>What refuses any request: Demeter off, or anything of Demeter's already driving.</summary>
    private (FishSessionCode Code, string Reason) Gate(string requestedBy)
    {
        if (!_enabled())
            return (FishSessionCode.Off, "Demeter is switched off");
        if (_session.Busy)
            return (FishSessionCode.Busy, $"already fishing here for {(_session.RequestedBy.Length > 0 ? _session.RequestedBy : "a caller")}");
        if (_stand.Busy)
            return (FishSessionCode.Busy, $"already walking to a fishing spot for {(_stand.RequestedBy.Length > 0 ? _stand.RequestedBy : "a caller")}");
        if (_othersBusy())
            return (FishSessionCode.Busy, "Demeter is busy with something else");
        return (FishSessionCode.None, string.Empty);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string Text<T>(T value) where T : Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
