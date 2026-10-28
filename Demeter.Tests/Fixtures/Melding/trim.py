"""Trims xivapi/ffxiv-datamining csv/en sheets to the rows/columns the melding tests use.
Keeps the original header names and row ids so every value can be checked against the source."""
import csv, sys, os
src, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
def load(n):
    with open(f"{src}/{n}.csv", newline='', encoding='utf-8') as f:
        r = csv.reader(f); h = next(r); return h, list(r)
def write(n, h, rows, cols=None):
    idx = [h.index(c) for c in cols] if cols else list(range(len(h)))
    with open(f"{out}/{n}.csv", 'w', newline='', encoding='utf-8') as f:
        w = csv.writer(f, lineterminator='\n')
        w.writerow([h[i] for i in idx])
        for r in rows: w.writerow([r[i] for i in idx])

# Full small sheets.
for n in ("GatheringPointBonus", "GatheringCondition", "GatheringPointBonusType", "MateriaJoinRateGatherCraft"):
    h, rows = load(n); write(n, h, rows)

h, rows = load("GatheringPoint")
gp = [r for r in rows if r[h.index("GatheringPointBonus[0]")] != '0' or r[h.index("GatheringPointBonus[1]")] != '0']
write("GatheringPoint", h, gp, ["#", "GatheringPointBase", "GatheringPointBonus[0]", "GatheringPointBonus[1]", "TerritoryType", "Type"])

h, rows = load("Materia")
write("Materia", h, [r for r in rows if r[-1] in ("10", "72", "73")])

h, rows = load("BaseParam")
pct = ["OneHandWeaponPercent","OffHandPercent","HeadPercent","ChestPercent","HandsPercent","WaistPercent","LegsPercent","FeetPercent","EarringPercent","NecklacePercent","BraceletPercent","RingPercent","TwoHandWeaponPercent"]
write("BaseParam", h, [r for r in rows if r[0] in ("10", "72", "73")], ["#", "Name"] + pct + [f"MeldParam[{i}]" for i in range(13)])

ch, crows = load("ClassJobCategory")
dol = {r[0] for r in crows if 'True' in (r[ch.index('MIN')], r[ch.index('BTN')], r[ch.index('FSH')])}

ih, irows = load("Item"); ix = {c: i for i, c in enumerate(ih)}
mat_items = set()
mh, mrows = load("Materia")
for r in mrows:
    if r[-1] in ("10", "72", "73"):
        mat_items |= {x for x in r[1:17] if x != '0'}
foods = {"36054","36061","37283","38930","44078","44099","44103","44843","46254"}
gear = [r for r in irows if r[ix['ClassJobCategory']] in dol and r[ix['EquipSlotCategory']] != '0' and int(r[ix['LevelItem']] or 0) >= 690]
keep = gear + [r for r in irows if r[0] in mat_items or r[0] in foods]
keep.sort(key=lambda r: int(r[0]))
icols = ["#", "Name", "LevelItem", "LevelEquip", "ClassJobCategory", "EquipSlotCategory"] + \
    [f"BaseParam[{i}]" for i in range(6)] + [f"BaseParamValue[{i}]" for i in range(6)] + \
    [f"BaseParamSpecial[{i}]" for i in range(6)] + [f"BaseParamValueSpecial[{i}]" for i in range(6)] + \
    ["BaseParamModifier", "MateriaSlotCount", "IsAdvancedMeldingPermitted", "CanBeHq", "IsUnique", "ItemAction", "ItemUICategory"]
write("Item", ih, keep, icols)
used_cjc = {r[ix['ClassJobCategory']] for r in keep}
write("ClassJobCategory", ch, [r for r in crows if r[0] in used_cjc], ["#", "Name", "MIN", "BTN", "FSH"])

lh, lrows = load("ItemLevel")
levels = {r[ix['LevelItem']] for r in gear}
write("ItemLevel", lh, [r for r in lrows if r[0] in levels], ["#", "GP", "Gathering", "Perception"])

ah, arows = load("ItemAction")
acts = {r[ix['ItemAction']] for r in keep if r[0] in foods}
akeep = [r for r in arows if r[0] in acts]
write("ItemAction", ah, akeep, ["#", "Action", "Data[0]", "Data[1]", "Data[2]"])
fh, frows = load("ItemFood")
fids = {r[ah.index('Data[1]')] for r in akeep}
write("ItemFood", fh, [r for r in frows if r[0] in fids])
print(len(gear), "gear rows;", len(keep), "item rows;", "levels", sorted(levels, key=int))
