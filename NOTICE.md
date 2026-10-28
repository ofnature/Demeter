# Notices and third-party data

Demeter is AGPL-3.0 (see `COPYING`). It gathers with data and lessons from several projects; this
file records what comes from where, and on what terms. Keep it current as sources are added.

## Data shipped with attribution

### GatherBuddy / GatherBuddy Reborn — Apache License 2.0

- `Ottermandias/GatherBuddy` — Apache 2.0
- `FFXIV-CombatReborn/GatherBuddyReborn` — Apache 2.0

`Assets/gatherbuddy-world_locations.json.gz` is GatherBuddy's `GatherBuddy.CustomInfo.world_locations.json`
(the node coordinates), **shipped verbatim**, only gzipped. Apache 2.0 permits redistribution,
including of the data, provided a copy of the licence travels with it, the original notices are
retained, and modified files are marked as changed. Every Demeter release carries
`licenses/Apache-2.0.txt` beside the data. The file itself is unmodified; Demeter reads it and
picks the nodes for an item at runtime.

Apache 2.0 is one-way compatible with GPL-3, so it can be combined into this project. The reverse is
not true, and nothing here is contributed back under Apache terms.

The data moved here from Odysseus (`ofnature/Odysseus`) with the gatherer, on 2026-10-22.

`Assets/fish-gbr.json.gz` is GatherBuddy Reborn's fish catch data — bait and mooch chains, hooksets
and tugs, weather and time windows, predators, lures, spearfishing and ocean details — from
`GatherBuddy.GameData/Data/Fish/Data*.cs` at the commit recorded inside the file. **It is modified**,
as Apache 2.0 §4(b) requires us to say: the C# data calls were read as syntax (never compiled or
run) and written out as JSON; every id was resolved against the game's own sheets — names taken
from the game, `Bait(id)` split into bait or mooch, a duplicate entry (44336) settled by the item's
real name, the full bait-to-fish road computed. The values themselves are GatherBuddy's. The
converter is `Tools/FishImport`.

`Assets/fishing-holes.json.gz` is derived from GatherBuddy Reborn's community catch records
(`GatherBuddy/CustomInfo/fish_records.json`, same commit). **Modified**: per fishing hole, the
recorded positions are grouped into distinct stand points (within 2.5 yalms) with the facing they
were cast at and how often each was used, and bite times are reduced to minimum/maximum per fish
and bait, with and without Chum. Records the recorder itself marks invalid are left out of the bite
times. No individual record is shipped.

### Carbuncle Plushy fish tracker (`icykoneko/ff14-fish-tracker-app`) — MIT

Used as an independent second source to **check** the fish data above, at build time only
(`Tools/FishImport` reads its `private/fishData.yaml`). Nothing from it is shipped; where the two
disagree the check writes a report for a person to judge, and the shipped data stays GatherBuddy's.

## Data read at runtime from the user's own installed plugins

### AutoHook (`PunishXIV/AutoHook`) — BSD-3-Clause

`BaitCatalog` reads bait, mooch chains and hook timings from `Data/FishData/fish_list.json` inside
the user's own AutoHook installation, at runtime. Nothing is copied into Demeter and nothing is
shipped. If AutoHook is not installed, the fishing data says so rather than falling back to a copy.

(Odysseus's notice named `PunishedPineapple/AutoHook`; that repository no longer exists, and the
plugin lives at `PunishXIV/AutoHook` under BSD-3-Clause.)

## Code ported

### GatherBuddy Reborn (`FFXIV-CombatReborn/GatherBuddyReborn`) — Apache-2.0

`Services/Game/GameFisherWorld.cs` follows GatherBuddy Reborn's fishing parser and recorder
(`GatherBuddy/FishTimer/Parser/FishingParser.cs`, `SeFunctions/EventFramework.cs`,
`SeFunctions/SeTugType.cs`, `SeFunctions/CurrentBait.cs`): the hook on `AgentCatch.UpdateCatch`
through ClientStructs' own function pointer (collectables reported 500,000 above the item id), the
fishing state off ClientStructs' `FishingEventHandler`, the bait switch as game command 701/4, and
the tug read from the static address its `SeTugType` signature finds (36/37/38 = light/medium/heavy).
Rewritten for Demeter, not copied; Apache-2.0 permits it with this notice.

### Odysseus (`ofnature/Odysseus`) — AGPL-3.0, the same author

The gatherer (`Services/Gathering/`), the travel lessons in `Services/Travel/Traveller.cs`, the game
layer in `Services/Game/GameBasics.cs`, the Lifestream client, the aetheryte
catalogue, and the window theme came from Odysseus. AGPL-3.0 into AGPL-3.0.

### Daedalus (`ofnature/Daedalus`, the Olympus repo) — GPL-3.0, the same author

The edge tab the main window minimizes to (`Windows/Dock/`) is ported from Daedalus's edge dock (via
Odysseus) — `DockHover`, `DockMath`, `DockLayout`/`DockLayoutShare` unchanged, the window drawn in
Demeter's colours. GPL-3.0 code combines with AGPL-3.0. The layout share tag (`EdgeDock.Layout.v2`)
is shared with Daedalus, Charon and Odysseus on purpose, so all their tabs stack on one edge.

### Daedalus (`ofnature/Daedalus`) — LGPL-3.0, the same author: the materia optimizer

`Services/Melding/` (the gatherer materia optimizer) is ported from Daedalus's combat meld optimizer
in `Daedalus/Services/Gear/` at commit `44bd566`: `StatCapMath.cs` (per-piece caps, ItemLevel × BaseParam
slot ‰, rounded half up) nearly as is, and the shape of `MeldSweepOptimizer.cs` (cap headroom above
base and kept melds, materia clipped at the cap, whole sets scored against one model) rewritten
around a threshold ranking and an exact search instead of `MeldDpsModel`'s DPS multiplier. Each file
names its Daedalus source in its header. Daedalus's repository carries `COPYING` and
`COPYING.LESSER`: LGPL-3.0, which may be conveyed under the GPL-3.0 and so combined into this
AGPL-3.0 project. (The edge dock above came from the same repository.)

### Teamcraft (`ffxiv-teamcraft/ffxiv-teamcraft`) — MIT

Ported with attribution, at commit `c022ca5`, from `apps/client/src/app/modules/gearsets/materia.service.ts`
and `apps/client/src/app/lazy-data/static-data.ts`: the cap divisor `BaseParam.MeldParam[Item.BaseParamModifier]`
(`StatCapMath`), the reading of MateriaJoinRateGatherCraft for grades XI and XII by parity (XI as
IX, XII as X; `OvermeldRates`), and the formula for how many materia to bring to land an overmeld
with a given confidence (`MateriaOptimizer.AttemptsAtConfidence`). Rewritten in C#, not copied. The
MIT notice is in `licenses/MIT-Teamcraft.txt`.

## Test data, not shipped

### xivapi/ffxiv-datamining — game data

`Demeter.Tests/Fixtures/Melding/*.csv` are rows of the game's own sheets (GatheringPoint,
GatheringPointBonus, Item, ItemFood, Materia, MateriaJoinRateGatherCraft, BaseParam, ItemLevel and a
few more) from `xivapi/ffxiv-datamining` at commit `44948e4` (2026-10-20), trimmed by the
`trim.py` beside them; `SOURCE.txt` there says exactly what was kept. The values are Square Enix's
game data — the same Demeter reads from the client at runtime — and the repository has no licence
file of its own. They are test inputs only and never ship with the plugin.

### Facts from the community

The community stat tiers in `GatheringTargets.CommunityTiers` (990 GP, 4570 Gathering for big fish,
5090 Perception) were supplied by Demeter's author as facts and are user-editable. Nothing was copied
from guides, calculator spreadsheets or their images.

## Handoffs, not data

Ariadne (pathing and movement), Lifestream (teleports and going home) and Daedalus (fighting what
attacks on the way) are driven over their published IPC where the user has them installed. Calling a
plugin's IPC is use, not derivation, and none of their code is included here.
