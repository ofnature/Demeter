using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Demeter.Services.Gathering;

namespace Demeter.Config;

/// <summary>
/// The gather settings Odysseus had before the gatherer moved here, read once from its config.
///
/// <para>
/// Read as plain JSON rather than through Odysseus's types, which Demeter does not have: Dalamud
/// writes every object with a <c>$type</c> naming Odysseus's classes, and those names are simply
/// skipped. Odysseus's copy is left exactly as it was — it drops the fields in its own time.
/// </para>
/// </summary>
public sealed record OdysseusGatherSettings(List<GatherList> Lists, GatherHome? Home, int? RepairAtPercent, string? PathingProvider)
{
    /// <summary>
    /// <c>pluginConfigs/Odysseus.json</c>, the sibling of Demeter's own config directory
    /// (<c>pluginConfigs/Demeter/</c>).
    /// </summary>
    public static string PathBeside(string demeterConfigDirectory)
        => Path.Combine(Path.GetDirectoryName(demeterConfigDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? string.Empty, "Odysseus.json");

    /// <summary>Null when there is no file or it cannot be read; never throws.</summary>
    public static OdysseusGatherSettings? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null when the JSON is not an object; missing fields come back empty or null.</summary>
    public static OdysseusGatherSettings? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var lists = new List<GatherList>();
            if (root.TryGetProperty("GatherLists", out var raw) && raw.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in raw.EnumerateArray())
                {
                    if (l.ValueKind != JsonValueKind.Object)
                        continue;
                    var list = new GatherList
                    {
                        Name = Str(l, "Name") ?? "Odysseus list",
                        Enabled = Bool(l, "Enabled") ?? true,
                        RemoveCompleted = Bool(l, "RemoveCompleted") ?? false,
                    };
                    if (l.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var i in items.EnumerateArray())
                            if (i.ValueKind == JsonValueKind.Object && Int(i, "ItemId") is { } id and > 0)
                                list.Items.Add(new GatherListItem { ItemId = (uint)id, TargetCount = Math.Max(1, Int(i, "TargetCount") ?? 99) });
                    lists.Add(list);
                }
            }

            GatherHome? home = Int(root, "GatherHome") is { } h && Enum.IsDefined(typeof(GatherHome), h) ? (GatherHome)h : null;
            return new OdysseusGatherSettings(lists, home, Int(root, "RepairAtPercent"), Str(root, "PathingProvider"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Carry Odysseus's settings over, once. Lists only when Demeter has none of its own; the other
    /// settings only alongside them, so a Demeter already set up keeps its own. True when the
    /// config changed and wants saving.
    /// </summary>
    public static bool MigrateInto(DemeterConfig config, OdysseusGatherSettings? from)
    {
        if (config.OdysseusListsMigrated)
            return false;
        config.OdysseusListsMigrated = true;
        if (from is null || config.GatherLists.Count > 0)
            return true;

        config.GatherLists.AddRange(from.Lists);
        if (from.Home is { } home) config.GatherHome = home;
        if (from.RepairAtPercent is { } repair and >= 0 and <= 100) config.RepairAtPercent = repair;
        if (from.PathingProvider is { Length: > 0 } provider) config.PathingProvider = provider;
        return true;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
