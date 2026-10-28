using Demeter.Services.Melding;

namespace Demeter.Tests;

/// <summary>
/// The trimmed game sheets in Fixtures/Melding (see SOURCE.txt there), read into the same row records
/// the in-game adapter fills from Lumina. Loaded once; everything here is immutable.
/// </summary>
internal static class MeldingFixtures
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Melding");

    public static readonly IReadOnlyDictionary<uint, ItemRow> Items = Load("Item", r => new ItemRow(
        r.U("#"), r["Name"], r.I("LevelItem"), r.I("LevelEquip"), r.U("ClassJobCategory"), r.U("EquipSlotCategory"),
        Params(r, "BaseParam", "BaseParamValue"), Params(r, "BaseParamSpecial", "BaseParamValueSpecial"),
        r.I("BaseParamModifier"), r.I("MateriaSlotCount"), r.B("IsAdvancedMeldingPermitted"), r.B("CanBeHq"), r.B("IsUnique")))
        .ToDictionary(i => i.Id);

    public static readonly IReadOnlyDictionary<uint, ClassJobCategoryRow> Categories = Load("ClassJobCategory",
        r => new ClassJobCategoryRow(r.U("#"), r.B("MIN"), r.B("BTN"), r.B("FSH"))).ToDictionary(c => c.Id);

    public static readonly IReadOnlyDictionary<int, ItemLevelRow> ItemLevels = Load("ItemLevel",
        r => new ItemLevelRow(r.I("#"), r.I("GP"), r.I("Gathering"), r.I("Perception"))).ToDictionary(l => l.Level);

    public static readonly IReadOnlyDictionary<uint, BaseParamRow> BaseParams = Load("BaseParam", r => new BaseParamRow(
        r.U("#"), r["Name"], r.I("OneHandWeaponPercent"), r.I("OffHandPercent"), r.I("HeadPercent"), r.I("ChestPercent"),
        r.I("HandsPercent"), r.I("LegsPercent"), r.I("FeetPercent"), r.I("EarringPercent"), r.I("NecklacePercent"),
        r.I("BraceletPercent"), r.I("RingPercent"), r.I("TwoHandWeaponPercent"),
        Enumerable.Range(0, 13).Select(i => r.I($"MeldParam[{i}]")).ToList())).ToDictionary(b => b.Id);

    public static readonly IReadOnlyList<MateriaRow> MateriaRows = Load("Materia", r => new MateriaRow(
        r.U("#"), r.U("BaseParam"),
        Enumerable.Range(0, 16).Select(i => r.U($"Item[{i}]")).ToList(),
        Enumerable.Range(0, 16).Select(i => r.I($"Value[{i}]")).ToList()));

    public static readonly IReadOnlyList<MateriaJoinRateRow> JoinRates = Load("MateriaJoinRateGatherCraft", r => new MateriaJoinRateRow(
        r.I("#"),
        Enumerable.Range(0, 4).Select(i => r.I($"NQOvermeldPercentSlot[{i}]")).ToList(),
        Enumerable.Range(0, 4).Select(i => r.I($"HQOvermeldPercentSlot[{i}]")).ToList()));

    public static readonly IReadOnlyList<GatheringPointRow> Points = Load("GatheringPoint", r => new GatheringPointRow(
        r.U("#"), r.U("GatheringPointBase"), new[] { r.U("GatheringPointBonus[0]"), r.U("GatheringPointBonus[1]") },
        r.U("TerritoryType"), r.I("Type")));

    public static readonly IReadOnlyList<GatheringPointBonusRow> Bonuses = Load("GatheringPointBonus", r => new GatheringPointBonusRow(
        r.U("#"), r.U("Condition"), r.I("ConditionValue"), r.U("BonusType"), r.I("BonusValue"), r.I("Unknown1"), r.I("Unknown2")));

    public static readonly IReadOnlyDictionary<uint, string> BonusTypeText = Load("GatheringPointBonusType",
        r => (Id: r.U("#"), Text: r["Text"])).ToDictionary(t => t.Id, t => t.Text);

    private static readonly IReadOnlyDictionary<uint, ItemFoodRow> FoodRows = Load("ItemFood", r => new ItemFoodRow(
        r.U("#"),
        Enumerable.Range(0, 3).Where(i => r.U($"BaseParam[{i}]") != 0).Select(i => new ItemFoodParam(
            r.U($"BaseParam[{i}]"), r.B($"IsRelative[{i}]"), r.I($"Value[{i}]"), r.I($"Max[{i}]"),
            r.I($"ValueHQ[{i}]"), r.I($"MaxHQ[{i}]"))).ToList())).ToDictionary(f => f.Id);

    private static readonly IReadOnlyDictionary<uint, uint> FoodOfAction = Load("ItemAction",
        r => (Id: r.U("#"), Food: r.U("Data[1]"))).ToDictionary(a => a.Id, a => a.Food);

    // Item ids used across the tests.
    public const uint GoldThumbPickaxe = 47171, GoldThumbSledgehammer = 47182;
    public const uint PickaxeOfStars = 51786, StarTechPickaxe = 49334, StarTechSledgehammer = 49345;
    public const uint CrestedHood = 47189, CrestedCoat = 47190, CrestedGloves = 47191, CrestedSlops = 47192, CrestedBoots = 47193;
    public const uint CrestedEarrings = 47198, CrestedNecklace = 47199, CrestedBracelet = 47200, CrestedRing = 47201;
    public const uint StarTechCoat = 49353, StarTechRing = 49364;
    public const uint GuerdonXi = 41762, GuileXi = 41763, GraspXi = 41764, GuerdonXii = 41775, GuileXii = 41776, GraspXii = 41777;
    public const uint GoldentailPie = 46254, CloudsailMeuniere = 44843, StuffedPeppers = 44103, NasiGoreng = 44078;

    /// <summary>
    /// The gatherer's own stats with nothing on, for the tests. GP 400 is an ASSUMPTION (the in-game
    /// figure is for the adapter to measure as live total minus gear); Gathering and Perception are
    /// taken as all gear.
    /// </summary>
    public static readonly GatherStats AssumedCharacterBase = new(0, 0, 400);

    public static OvermeldRates Rates => OvermeldRates.FromSheet(JoinRates);

    /// <summary>Guerdon, Guile and Grasp XI and XII: what Lv100 gear melds.</summary>
    public static IReadOnlyList<MateriaGrade> TopMateria => MateriaCatalog.FromSheet(MateriaRows, Items, minGrade: 11);

    public static MateriaGrade Materia(uint itemId) => MateriaCatalog.FromSheet(MateriaRows, Items).Single(m => m.ItemId == itemId);

    public static GearCandidate Piece(
        uint itemId, bool hq = true, bool owned = true, bool equipped = false,
        IReadOnlyList<MeldedMateria>? melds = null, ulong? instance = null)
    {
        var item = Items[itemId];
        ItemLevels.TryGetValue(item.LevelItem, out var level);
        return GearCatalog.Candidate(item, hq, level, BaseParams, melds, owned, equipped, instance)!;
    }

    public static FoodOption Food(uint itemId, bool hq = true, int owned = 5)
    {
        var item = Items[itemId];
        return FoodOption.FromSheet(itemId, item.Name, FoodRows[FoodOfAction[ActionOf(itemId)]], hq, owned);
    }

    public static IReadOnlyList<FoodOption> CurrentFoods(int owned = 5)
        => new[] { GoldentailPie, CloudsailMeuniere, StuffedPeppers, NasiGoreng }.Select(f => Food(f, owned: owned)).ToList();

    /// <summary>Plenty of every top materia: stock never decides.</summary>
    public static IReadOnlyDictionary<uint, int> PlentifulStock()
        => TopMateria.ToDictionary(m => m.ItemId, _ => 9999);

    public static MeldedMateria Melded(uint materiaItemId)
    {
        var m = Materia(materiaItemId);
        return new MeldedMateria((uint)m.Stat, m.Grade, m.Value, m.ItemId);
    }

    // The fixtures' ItemAction column is kept on Item; read it straight from the csv.
    private static uint ActionOf(uint itemId) => ItemActions[itemId];

    private static readonly IReadOnlyDictionary<uint, uint> ItemActions = Load("Item",
        r => (Id: r.U("#"), Action: r.U("ItemAction"))).ToDictionary(x => x.Id, x => x.Action);

    private static List<ItemParam> Params(Row r, string param, string value)
        => Enumerable.Range(0, 6)
            .Select(i => new ItemParam(r.U($"{param}[{i}]"), r.I($"{value}[{i}]")))
            .Where(p => p.BaseParam != 0)
            .ToList();

    private static List<T> Load<T>(string sheet, Func<Row, T> map)
    {
        var lines = File.ReadAllLines(Path.Combine(Dir, sheet + ".csv"));
        var header = Split(lines[0]);
        var index = header.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
        return lines.Skip(1).Where(l => l.Length > 0).Select(l => map(new Row(index, Split(l)))).ToList();
    }

    /// <summary>One csv line; fields may be quoted, quotes doubled inside.</summary>
    private static string[] Split(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private sealed class Row(IReadOnlyDictionary<string, int> index, string[] fields)
    {
        public string this[string column] => fields[index[column]];
        public int I(string column) => int.TryParse(this[column], out var v) ? v : 0;
        public uint U(string column) => uint.TryParse(this[column], out var v) ? v : 0;
        public bool B(string column) => this[column] == "True";
    }
}
