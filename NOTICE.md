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

## Data read at runtime from the user's own installed plugins

### AutoHook (`PunishXIV/AutoHook`) — BSD-3-Clause

`BaitCatalog` reads bait, mooch chains and hook timings from `Data/FishData/fish_list.json` inside
the user's own AutoHook installation, at runtime. Nothing is copied into Demeter and nothing is
shipped. If AutoHook is not installed, the fishing data says so rather than falling back to a copy.

(Odysseus's notice named `PunishedPineapple/AutoHook`; that repository no longer exists, and the
plugin lives at `PunishXIV/AutoHook` under BSD-3-Clause.)

## Code ported

### Odysseus (`ofnature/Odysseus`) — AGPL-3.0, the same author

The gatherer (`Services/Gathering/`), the travel lessons in `Services/Travel/Traveller.cs`, the game
layer in `Services/Game/GameBasics.cs`, the vnavmesh/Ariadne and Lifestream clients, the aetheryte
catalogue, and the window theme came from Odysseus. AGPL-3.0 into AGPL-3.0.

### Daedalus (`ofnature/Daedalus`, the Olympus repo) — GPL-3.0, the same author

The edge tab the main window minimizes to (`Windows/Dock/`) is ported from Daedalus's edge dock (via
Odysseus) — `DockHover`, `DockMath`, `DockLayout`/`DockLayoutShare` unchanged, the window drawn in
Demeter's colours. GPL-3.0 code combines with AGPL-3.0. The layout share tag (`EdgeDock.Layout.v2`)
is shared with Daedalus, Charon and Odysseus on purpose, so all their tabs stack on one edge.

## Handoffs, not data

vnavmesh or Ariadne (pathing), Lifestream (teleports and going home) and Daedalus (fighting what
attacks on the way) are driven over their published IPC where the user has them installed. Calling a
plugin's IPC is use, not derivation, and none of their code is included here.
