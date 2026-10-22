using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina;
using Lumina.Excel.Sheets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// FishImport — GatherBuddy Reborn's fish data, checked, into Demeter's shipped snapshot.
//
//   dotnet run --project Tools/FishImport -c Release -- \
//       --ref <GBR commit> --tracker-ref <fish tracker commit> \
//       --out Demeter/Assets/fish-gbr.json.gz --report <report.md> [--game C:\game\sqpack] [--src <dir>]
//
// 1. GatherBuddy writes its fish data as C# chains —
//      data.Apply(52005, Patch.TrailToTheHeavens).Bait(data, 43857).Weather(data, 3).Time(360, 480)…
//    They are read as SYNTAX (Roslyn), never compiled or run: someone else's code, only its data wanted.
//    The meaning of each call is GatherBuddy's own (GatherBuddy.GameData/Data/Fish/DataBase.cs).
// 2. Every id is resolved against the game's own sheets (Lumina, the local install): names, whether
//    Bait(id) is a bait or a fish to mooch from, duplicates settled by the item's real name.
// 3. A second, independent source — the Carbuncle Plushy fish tracker (MIT) — is compared field by
//    field. Every disagreement goes in the report. Nothing is guessed: the snapshot carries
//    GatherBuddy's answer, and the report says where the two differ, for a person to judge.

const string GbrRepo = "FFXIV-CombatReborn/GatherBuddyReborn";
const string GbrPath = "GatherBuddy.GameData/Data/Fish";
const string TrackerRepo = "icykoneko/ff14-fish-tracker-app";
const string TrackerPath = "private/fishData.yaml";

string? src = null, gitRef = null, trackerRef = null, output = null, reportPath = null, holesOut = null;
var game = @"C:\game\sqpack";
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--src": src = args[++i]; break;
        case "--ref": gitRef = args[++i]; break;
        case "--tracker-ref": trackerRef = args[++i]; break;
        case "--out": output = args[++i]; break;
        case "--report": reportPath = args[++i]; break;
        case "--game": game = args[++i]; break;
        case "--holes-out": holesOut = args[++i]; break;
    }
}
if (gitRef is null || trackerRef is null || output is null || reportPath is null)
{
    Console.Error.WriteLine("usage: --ref <GBR sha> --tracker-ref <tracker sha> --out <file.json.gz> --report <file.md> [--src <dir>] [--game <sqpack>]");
    return 2;
}

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Demeter-FishImport");

// ── 1. GatherBuddy's data, parsed ──

var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
if (src is not null)
    foreach (var f in Directory.GetFiles(src, "Data*.cs"))
        files[Path.GetFileName(f)] = File.ReadAllText(f);
else
{
    var listing = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{GbrRepo}/contents/{GbrPath}?ref={gitRef}"));
    foreach (var entry in listing.RootElement.EnumerateArray())
    {
        var name = entry.GetProperty("name").GetString()!;
        if (name.StartsWith("Data", StringComparison.Ordinal) && name.EndsWith(".cs", StringComparison.Ordinal))
            files[name] = await http.GetStringAsync($"https://raw.githubusercontent.com/{GbrRepo}/{gitRef}/{GbrPath}/{name}");
    }
}

var report = new Report();
var raw = new List<FishEntry>();
foreach (var (name, text) in files)
{
    if (name == "DataBase.cs")
        continue; // the helpers, not data
    foreach (var statement in CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>())
    {
        if (statement.Expression is not InvocationExpressionSyntax outer)
            continue;
        var calls = Unchain(outer);
        if (calls.Count == 0 || calls[0].Name != "Apply")
            continue;
        try
        {
            var e = Build(calls, statement);
            e.SourceFile = name;
            raw.Add(e);
        }
        catch (Exception ex)
        {
            report.Add("Unreadable entries", $"{name}:{statement.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {ex.Message}");
        }
    }
}

// ── 2. The game's sheets ──

var lumina = new GameData(game, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
var items = lumina.GetExcelSheet<Item>()!;
string ItemName(uint id) => items.GetRowOrDefault(id)?.Name.ExtractText().Trim() ?? string.Empty;
var itemByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
foreach (var it in items)
{
    var n = it.Name.ExtractText().Trim();
    if (n.Length > 0) itemByName.TryAdd(n, it.RowId);
}
var fishItems = new HashSet<uint>(lumina.GetExcelSheet<FishParameter>()!.Select(f => f.Item.RowId).Where(id => id != 0));
foreach (var s in lumina.GetExcelSheet<SpearfishingItem>()!) if (s.Item.RowId != 0) fishItems.Add(s.Item.RowId);
var weatherName = lumina.GetExcelSheet<Weather>()!.ToDictionary(w => w.RowId, w => w.Name.ExtractText());
var weatherByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
foreach (var (id, n) in weatherName) if (n.Length > 0) weatherByName.TryAdd(n, id);
var spotsOf = new Dictionary<uint, List<string>>();
foreach (var spot in lumina.GetExcelSheet<FishingSpot>()!)
{
    var place = spot.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
    foreach (var it in spot.Item)
        if (it.RowId != 0)
            (spotsOf.TryGetValue(it.RowId, out var l) ? l : spotsOf[it.RowId] = []).Add(place);
}

// Duplicates: the entry whose label is the item's real name wins; the rest are reported.
var fish = new SortedDictionary<uint, FishEntry>();
foreach (var group in raw.GroupBy(f => f.Id))
{
    var real = ItemName(group.Key);
    var chosen = group.FirstOrDefault(f => string.Equals(f.Label, real, StringComparison.OrdinalIgnoreCase)) ?? group.First();
    if (group.Count() > 1)
        report.Add("Duplicate ids in GatherBuddy", $"{group.Key} \"{real}\": applied {group.Count()}× ({string.Join(", ", group.Select(g => $"\"{g.Label}\" in {g.SourceFile}"))}) — kept \"{chosen.Label}\"");
    if (real.Length == 0)
        report.Add("Ids that are not items", $"{group.Key} (\"{chosen.Label}\")");
    // GatherBuddy's comments went through a "fish" → "data" replace at some point ("Angeldata"); a
    // label that only differs by that is the same name.
    else if (chosen.Label is { } label && !string.Equals(label, real, StringComparison.OrdinalIgnoreCase)
             && !string.Equals(label.Replace("data", "fish", StringComparison.Ordinal), real, StringComparison.OrdinalIgnoreCase))
        report.Add("Label differs from the item's name", $"{group.Key}: GatherBuddy says \"{label}\", the game says \"{real}\"");
    if (real.Length > 0 && !fishItems.Contains(group.Key))
        report.Add("Not a fish in the game's sheets", $"{group.Key} \"{real}\"");
    chosen.Name = real;
    fish[group.Key] = chosen;
}

// Bait(id): a fish id means a one-step mooch from that fish; anything else is a bait item.
foreach (var f in fish.Values)
{
    if (f.BaitOrMooch is not { } id)
        continue;
    if (fishItems.Contains(id) || fish.ContainsKey(id))
        f.Mooch = [id];
    else
        f.Bait = id;
    f.BaitOrMooch = null;
    if (ItemName(id).Length == 0)
        report.Add("Bait that is not an item", $"{f.Id} \"{f.Name}\": bait {id}");
}

// The whole road from the tackle box: follow each mooch fish back to its own bait.
List<uint> CatchPath(FishEntry f, HashSet<uint> seen)
{
    if (!seen.Add(f.Id))
        return [];
    if (f.Mooch is { Count: > 0 } mooch)
    {
        var start = f.Bait is { } b ? new List<uint> { b }
            : fish.TryGetValue(mooch[0], out var first) ? CatchPath(first, seen) : [];
        return [.. start, .. mooch];
    }
    return f.Bait is { } bait ? [bait] : [];
}
foreach (var f in fish.Values)
    f.Path = CatchPath(f, []) is { Count: > 0 } p ? p : null;

// ── 3. The fish tracker, compared ──

var yamlText = await http.GetStringAsync($"https://raw.githubusercontent.com/{TrackerRepo}/{trackerRef}/{TrackerPath}");
var tracker = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties()
    .Build().Deserialize<List<TrackerFish>>(yamlText) ?? [];
var compared = 0;
foreach (var t in tracker)
{
    if (t.Name is null)
        continue;
    if (!itemByName.TryGetValue(t.Name, out var id))
    {
        report.Add("Tracker fish not found by name", t.Name);
        continue;
    }
    if (!fish.TryGetValue(id, out var g))
    {
        report.Add("In the tracker, missing from GatherBuddy", $"{id} \"{t.Name}\" ({t.Location})");
        continue;
    }
    compared++;
    var who = $"{id} \"{t.Name}\"";

    // Time: the tracker's 0–24 is "always"; GatherBuddy writes nothing for always.
    var tStart = Minutes(t.StartHour, 0);
    var tEnd = Minutes(t.EndHour, 1440);
    var tAlways = tStart == 0 && tEnd == 1440;
    var gAlways = g.Time is null;
    if (tAlways != gAlways || (!tAlways && (g.Time![0] != tStart || g.Time[1] != tEnd % 1440 && g.Time[1] != tEnd)))
        report.Add("Time window", $"{who}: GatherBuddy {Hours(g.Time)}, tracker {(tAlways ? "always" : $"{t.StartHour}–{t.EndHour}")}");

    CompareSets("Weather", who, g.Weather, t.WeatherSet);
    CompareSets("Previous weather", who, g.PreviousWeather, t.PreviousWeatherSet);

    // A step may be a list of alternatives ("Mayfly or Leech"); GatherBuddy's one choice must be among them.
    var trackerPath = (t.BestCatchPath ?? []).Select(Alternatives).Where(a => a.Count > 0).ToList();
    var gbrPath = (g.Path ?? []).Select(ItemName).ToList();
    if (trackerPath.Count > 0
        && (trackerPath.Count != gbrPath.Count || trackerPath.Where((alts, i) => !alts.Contains(gbrPath[i], StringComparer.OrdinalIgnoreCase)).Any()))
        report.Add("Catch path", $"{who}: GatherBuddy [{string.Join(" → ", gbrPath)}], tracker [{string.Join(" → ", trackerPath.Select(a => string.Join(" / ", a)))}]");

    var hook = t.Hookset?.ToLowerInvariant() switch { "precision" => "Precise", "powerful" => "Powerful", var other => other };
    if (hook is not null && g.HookSet is not null && g.HookSet is "Precise" or "Powerful" && !string.Equals(hook, g.HookSet, StringComparison.OrdinalIgnoreCase))
        report.Add("Hookset", $"{who}: GatherBuddy {g.HookSet}, tracker {t.Hookset}");
    var tug = t.Tug?.ToLowerInvariant() switch { "light" => "Weak", "medium" => "Strong", "heavy" or "legendary" => "Legendary", var other => other };
    if (tug is not null && g.Bite is not null && !string.Equals(tug, g.Bite, StringComparison.OrdinalIgnoreCase))
        report.Add("Tug", $"{who}: GatherBuddy {g.Bite}, tracker {t.Tug}");

    var tPred = (t.Predators ?? []).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    var gPred = (g.Predators ?? []).ToDictionary(p => ItemName((uint)p[0]), p => p[1], StringComparer.OrdinalIgnoreCase);
    if (tPred.Count != gPred.Count || tPred.Any(kv => !gPred.TryGetValue(kv.Key, out var n) || n != kv.Value))
        report.Add("Predators", $"{who}: GatherBuddy {{{string.Join(", ", gPred.Select(kv => $"{kv.Key}×{kv.Value}"))}}}, tracker {{{string.Join(", ", tPred.Select(kv => $"{kv.Key}×{kv.Value}"))}}}");

    if (t.Snagging is { } snag && (g.Snagging == "Required") != snag)
        report.Add("Snagging", $"{who}: GatherBuddy {g.Snagging ?? "unset"}, tracker {snag}");

    if (t.Location is { Length: > 0 } loc && spotsOf.TryGetValue(id, out var spots) && !spots.Contains(loc, StringComparer.OrdinalIgnoreCase))
        report.Add("Tracker spot not among the game's spots", $"{who}: tracker \"{loc}\", game [{string.Join(", ", spots.Distinct())}]");
}

void CompareSets(string what, string who, List<uint>? gbr, List<string>? trk)
{
    var g = (gbr ?? []).Select(w => weatherName.GetValueOrDefault(w, $"#{w}")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var t = (trk ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var n in t.Where(n => !weatherByName.ContainsKey(n)))
        report.Add("Tracker weather not in the game's sheet", $"{who}: \"{n}\"");
    if (!g.SetEquals(t))
        report.Add(what, $"{who}: GatherBuddy [{string.Join(", ", g)}], tracker [{string.Join(", ", t)}]");
}

// The tracker writes hours as 17, 9.5 or "18:30".
static int Minutes(string? hours, int fallback)
{
    if (string.IsNullOrWhiteSpace(hours))
        return fallback;
    var parts = hours.Split(':');
    return parts.Length == 2
        ? int.Parse(parts[0]) * 60 + int.Parse(parts[1])
        : (int)Math.Round(double.Parse(hours, System.Globalization.CultureInfo.InvariantCulture) * 60);
}

static List<string> Alternatives(object? step) => step switch
{
    string s when !string.IsNullOrWhiteSpace(s) => [s],
    IEnumerable<object> list => list.OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
    _ => [],
};

static string Hours(int[]? time) => time is null ? "always" : $"{time[0] / 60.0:0.##}–{time[1] / 60.0:0.##}";

// ── 4. Out ──

var snapshot = new Snapshot(
    new Source(GbrRepo, gitRef, GbrPath, "Apache-2.0", DateTime.UtcNow.ToString("O")),
    new Source(TrackerRepo, trackerRef, TrackerPath, "MIT", DateTime.UtcNow.ToString("O")),
    fish.Values.ToList());
var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await using (var fileStream = File.Create(output))
await using (var gzip = new GZipStream(fileStream, CompressionLevel.SmallestSize))
await using (var writer = new StreamWriter(gzip))
    await writer.WriteAsync(json);

var md = new StringBuilder();
md.AppendLine("# Fish data cross-check").AppendLine();
md.AppendLine($"- GatherBuddy Reborn `{gitRef}` — {raw.Count} entries, {fish.Count} fish");
md.AppendLine($"- Fish tracker `{trackerRef}` — {tracker.Count} fish, {compared} compared");
md.AppendLine($"- Game data: `{game}`");
md.AppendLine($"- Generated {DateTime.UtcNow:O}").AppendLine();
md.AppendLine("The snapshot carries GatherBuddy's answer. Below is every place the sources disagree.").AppendLine();
foreach (var (section, lines) in report.Sections)
{
    md.AppendLine($"## {section} ({lines.Count})").AppendLine();
    foreach (var l in lines) md.AppendLine($"- {l}");
    md.AppendLine();
}
File.WriteAllText(reportPath, md.ToString());

Console.WriteLine($"{fish.Count} fish → {output} ({new FileInfo(output).Length:N0} bytes); {compared} compared with the tracker.");

// ── 5. Fishing holes: where people stood, and how long bites took ──
// GatherBuddy Reborn's community catch records (GatherBuddy/CustomInfo/fish_records.json, same
// commit): each catch with the hole, the position and facing it was made from, the bait, the bite
// time in ms and the effects up. Grouped per hole into distinct stand points (most used first) and
// bite-time ranges per (fish, bait), with and without Chum — Chum shortens the wait.
if (holesOut is not null)
{
    var recordsJson = await http.GetStringAsync($"https://raw.githubusercontent.com/{GbrRepo}/{gitRef}/GatherBuddy/CustomInfo/fish_records.json");
    var records = JsonSerializer.Deserialize<List<CatchRecord>>(recordsJson) ?? [];
    const uint Valid = 0x8000, Chum = 0x2, Lures = 0x40000 | 0x80000 | 0x100000 | 0x200000, ValidLure = 0x400000;
    const float SameSpot = 2.5f;
    var holes = new SortedDictionary<ushort, HoleEntry>();
    foreach (var r in records)
    {
        if (r.FishingSpot == 0)
            continue;
        if (!holes.TryGetValue(r.FishingSpot, out var hole))
            holes[r.FishingSpot] = hole = new HoleEntry { Id = r.FishingSpot };

        if (r.PositionX != 0 || r.PositionY != 0 || r.PositionZ != 0)
        {
            var near = hole.Stands.FirstOrDefault(p => Math.Abs(p.X - r.PositionX) <= SameSpot && Math.Abs(p.Z - r.PositionZ) <= SameSpot
                                                       && Math.Abs(p.Y - r.PositionY) <= SameSpot);
            if (near is null)
                hole.Stands.Add(new StandEntry { X = r.PositionX, Y = r.PositionY, Z = r.PositionZ, Rotation = r.Rotation, Uses = 1 });
            else
                near.Uses++;
        }

        // Bite times only from records the recorder itself trusts: valid, a catch, and no lure it could not account for.
        if ((r.Effects & Valid) == 0 || r.CatchId == 0 || r.Bite is < 1000 or > 65000
            || ((r.Effects & Lures) != 0 && (r.Effects & ValidLure) == 0))
            continue;
        var key = $"{r.CatchId}:{r.BaitId}";
        if (!hole.Bites.TryGetValue(key, out var bite))
            hole.Bites[key] = bite = new BiteEntry { Fish = r.CatchId, Bait = r.BaitId };
        var chum = (r.Effects & Chum) != 0;
        bite.Add(r.Bite / 1000.0, chum);
    }
    foreach (var hole in holes.Values)
        hole.Stands = hole.Stands.OrderByDescending(p => p.Uses).Take(12).ToList();

    var holesJson = JsonSerializer.Serialize(new
    {
        source = new Source(GbrRepo, gitRef, "GatherBuddy/CustomInfo/fish_records.json", "Apache-2.0", DateTime.UtcNow.ToString("O")),
        records = records.Count,
        holes = holes.Values.Select(h => new { id = h.Id, stands = h.Stands, bites = h.Bites.Values.OrderBy(b => b.Fish).ThenBy(b => b.Bait) }),
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    await using (var holeFile = File.Create(holesOut))
    await using (var holeGzip = new GZipStream(holeFile, CompressionLevel.SmallestSize))
    await using (var holeWriter = new StreamWriter(holeGzip))
        await holeWriter.WriteAsync(holesJson);
    Console.WriteLine($"{holes.Count} holes, {holes.Values.Sum(h => h.Stands.Count)} stand points, {holes.Values.Sum(h => h.Bites.Count)} bite ranges from {records.Count} records → {holesOut} ({new FileInfo(holesOut).Length:N0} bytes)");
}
foreach (var (section, lines) in report.Sections)
    Console.WriteLine($"  {lines.Count,5}  {section}");
return 0;

// ── the chain ──

static List<Call> Unchain(InvocationExpressionSyntax outer)
{
    // a.Apply(...).Bait(...).Bite(...) nests inside-out: the outermost invocation is the last call.
    var calls = new List<Call>();
    ExpressionSyntax? node = outer;
    while (node is InvocationExpressionSyntax invocation && invocation.Expression is MemberAccessExpressionSyntax access)
    {
        calls.Add(new Call(access.Name.Identifier.Text, invocation.ArgumentList.Arguments.Select(a => a.Expression).ToList()));
        node = access.Expression;
    }
    calls.Reverse();
    return calls;
}

static FishEntry Build(List<Call> calls, ExpressionStatementSyntax statement)
{
    var apply = calls[0];
    var entry = new FishEntry
    {
        Id = UInt(apply.Args[0]),
        Patch = EnumName(apply.Args[1]),
        // The comment on the Apply line names the fish: a label, checked against the game, never trusted.
        Label = statement.DescendantTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia))
            .ToString().TrimStart('/').Trim() is { Length: > 0 } label ? label : null,
    };

    foreach (var call in calls.Skip(1))
    {
        // Most helpers take "data" first; skip it so the positions match the helpers' parameters.
        var a = call.Args.Count > 0 && call.Args[0] is IdentifierNameSyntax { Identifier.Text: "data" }
            ? call.Args.Skip(1).ToList()
            : call.Args;
        switch (call.Name)
        {
            // Bait(id): a bait item, or — when the id is a fish — a one-step mooch. Resolved above.
            case "Bait":
                if (a.Count > 0) entry.BaitOrMooch = UInt(a[0]);
                break;
            // Mooch(fish) — from that fish; Mooch(bait, fish1, fish2…) — the whole chain from the bait.
            case "Mooch":
                if (a.Count == 1)
                    entry.Mooch = [UInt(a[0])];
                else
                {
                    entry.Bait = UInt(a[0]);
                    entry.Mooch = a.Skip(1).Select(UInt).ToList();
                }
                break;
            case "Weather": entry.Weather = a.Select(UInt).ToList(); break;
            case "Transition": entry.PreviousWeather = a.Select(UInt).ToList(); break;
            case "Time": entry.Time = [Int(a[0]), Int(a[1])]; break; // Eorzea minutes of the day
            case "Bite":
                entry.HookSet = EnumName(a[0]);
                if (a.Count > 1) entry.Bite = EnumName(a[1]);
                break;
            case "Snag": entry.Snagging = EnumName(a[0]); break;
            case "Lure": entry.Lure = EnumName(a[0]); break;
            case "Predators": // (intuition seconds, (fish, count)…)
                entry.IntuitionSeconds = Int(a[0]);
                entry.Predators = a.Skip(1).Select(Tuple).ToList();
                break;
            case "MultiHook": entry.MultiHook = a.Count == 1 ? [Int(a[0]), Int(a[0])] : [Int(a[0]), Int(a[1])]; break;
            case "Spear":
                entry.SpearSize = EnumName(a[0]);
                if (a.Count > 1) entry.SpearSpeed = EnumName(a[1]);
                break;
            case "Ocean": entry.Ocean = a.Select(EnumName).ToList(); break;
            case "OceanType": entry.OceanSpecies = EnumName(a[0]); break;
            case "ForceBig": entry.ForceBig = a[0].ToString() == "true"; break;
            case "ForceLegendary": entry.Legendary = true; break;
            case "Slap": entry.SurfaceSlap = UInt(a[0]); break;
            case "Points": entry.Points = Int(a[0]); break;
            case "Mission": entry.CosmicMission = Int(a[0]); break;
            case "Comment": entry.Guide = a[0] is LiteralExpressionSyntax lit ? lit.Token.ValueText : a[0].ToString(); break;
            default: throw new InvalidOperationException($"unknown call .{call.Name} on fish {entry.Id}");
        }
    }
    return entry;
}

static uint UInt(ExpressionSyntax e) => (uint)Int(e);

static int Int(ExpressionSyntax e) => e switch
{
    LiteralExpressionSyntax lit => Convert.ToInt32(lit.Token.Value),
    PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } neg => -Int(neg.Operand),
    CastExpressionSyntax cast => Int(cast.Expression),
    ParenthesizedExpressionSyntax p => Int(p.Expression),
    _ => throw new InvalidOperationException($"not a number: {e}"),
};

static string EnumName(ExpressionSyntax e) => e switch
{
    MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
    IdentifierNameSyntax id => id.Identifier.Text,
    _ => throw new InvalidOperationException($"not an enum member: {e}"),
};

static int[] Tuple(ExpressionSyntax e) => e is TupleExpressionSyntax t
    ? t.Arguments.Select(x => Int(x.Expression)).ToArray()
    : throw new InvalidOperationException($"not a (fish, count) pair: {e}");

record Call(string Name, List<ExpressionSyntax> Args);

sealed class CatchRecord
{
    public uint BaitId { get; set; }
    public uint CatchId { get; set; }
    public uint Effects { get; set; }
    public int Bite { get; set; }
    public ushort FishingSpot { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float Rotation { get; set; }
}

sealed class HoleEntry
{
    public ushort Id { get; set; }
    public List<StandEntry> Stands { get; set; } = [];
    public Dictionary<string, BiteEntry> Bites { get; } = new();
}

sealed class StandEntry
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rotation { get; set; }
    public int Uses { get; set; }
}

sealed class BiteEntry
{
    public uint Fish { get; set; }
    public uint Bait { get; set; }
    public int Count { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? MinChum { get; set; }
    public double? MaxChum { get; set; }

    public void Add(double seconds, bool chum)
    {
        Count++;
        if (chum)
        {
            MinChum = MinChum is { } a ? Math.Min(a, seconds) : seconds;
            MaxChum = MaxChum is { } b ? Math.Max(b, seconds) : seconds;
        }
        else
        {
            Min = Min is { } a ? Math.Min(a, seconds) : seconds;
            Max = Max is { } b ? Math.Max(b, seconds) : seconds;
        }
    }
}

record Source(string Repo, string Commit, string Path, string License, string Generated);

record Snapshot(Source Source, Source CrossCheck, List<FishEntry> Fish);

sealed class Report
{
    public SortedDictionary<string, List<string>> Sections { get; } = new(StringComparer.Ordinal);
    public void Add(string section, string line) => (Sections.TryGetValue(section, out var l) ? l : Sections[section] = []).Add(line);
}

sealed class TrackerFish
{
    public string? Name { get; set; }
    public string? Location { get; set; }
    public string? StartHour { get; set; }
    public string? EndHour { get; set; }
    public List<string>? PreviousWeatherSet { get; set; }
    public List<string>? WeatherSet { get; set; }
    public Dictionary<string, int>? Predators { get; set; }
    public List<object>? BestCatchPath { get; set; }
    public string? Tug { get; set; }
    public string? Hookset { get; set; }
    public bool? Snagging { get; set; }
}

sealed class FishEntry
{
    public uint Id { get; set; }
    public string? Name { get; set; }
    public string Patch { get; set; } = string.Empty;
    [JsonIgnore] public string? Label { get; set; }
    [JsonIgnore] public string? SourceFile { get; set; }
    [JsonIgnore] public uint? BaitOrMooch { get; set; }
    public uint? Bait { get; set; }
    public List<uint>? Mooch { get; set; }
    /// <summary>The whole road from the tackle box: bait, then each fish mooched, ending before this one.</summary>
    public List<uint>? Path { get; set; }
    public List<uint>? Weather { get; set; }
    public List<uint>? PreviousWeather { get; set; }
    public int[]? Time { get; set; }
    public string? HookSet { get; set; }
    public string? Bite { get; set; }
    public string? Snagging { get; set; }
    public string? Lure { get; set; }
    public int? IntuitionSeconds { get; set; }
    public List<int[]>? Predators { get; set; }
    public int[]? MultiHook { get; set; }
    public string? SpearSize { get; set; }
    public string? SpearSpeed { get; set; }
    public List<string>? Ocean { get; set; }
    public string? OceanSpecies { get; set; }
    public bool? ForceBig { get; set; }
    public bool? Legendary { get; set; }
    public uint? SurfaceSlap { get; set; }
    public int? Points { get; set; }
    public int? CosmicMission { get; set; }
    public string? Guide { get; set; }
}
