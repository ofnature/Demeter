# Demeter

Unattended gathering for Final Fantasy XIV (Dalamud, API 15), with fishing on the way. Demeter
gathers for you and for the other plugins in the suite: Hephaestus's material shortfalls,
Odysseus's quest gathers and custom deliveries, and your own gather lists.

- **Gather lists.** Name your lists and give each item a target count. A run gathers whatever is
  short, zone by zone, so each zone costs one teleport. Timed nodes are waited for and caught in
  their window. Completed rows can drop off by themselves, gear is repaired when it wears down,
  and afterwards Demeter can take you home (Return, inn, estate or apartment).
- **Travel.** Demeter uses Lifestream to teleport to the attuned aetheryte nearest the node, then
  hands the node's position to Ariadne, which walks or flies there (flying wherever the zone's aether
  currents are done). Demeter lands and works the node from the ground, and walks the circuit of
  node spawns. If something attacks on the way, Daedalus fights it and Demeter waits.
- **Collectables.** Demeter works a collectable to the collectability asked for and banks it. It
  never banks one below that band and never spends integrity pushing past it.
- **Fishing.** Fish go on the gather lists like anything else. Demeter picks the hole, stands
  where people actually fished from, puts on the bait, judges every bite by its timing and tug,
  moochs, catches predators for Fisher's Intuition, and waits out weather and time windows.
  Collectable fish are kept only at the collectability asked for. `/demeter fish` looks any fish up.
- **For other plugins.** Every gate is listed in [For other plugins](#for-other-plugins) below.

- **Materia (in progress).** `Services/Melding/` works out the best gatherer gear, melds and food:
  which piece to wear in each slot, what goes in every socket (overmelds included, with their odds
  and the materia they use on average), which food to eat, so Gathering, Perception and GP reach the
  game's highest thresholds — read from the sheets, never typed in — as cheaply as the bags allow,
  and why. Not wired to the game yet: reading the gear and driving the meld window come next. Its
  search follows Daedalus's meld optimizer; some numbers follow Teamcraft (MIT); the tests run on
  xivapi/ffxiv-datamining sheet rows. Details in `NOTICE.md`.

Companion to Daedalus and Odysseus. AGPL-3.0; see `NOTICE.md` for third-party data.

## Needs

- **Ariadne** for walking and flying.
- **Lifestream** for teleports and going home.
- A gearset saved for Miner and for Botanist.
- **Daedalus** is optional. With it, anything that attacks on the way gets fought.

## Commands

| Command | |
|---|---|
| `/demeter` | Open the main window. |
| `/demeter lists` | The gather lists. |
| `/demeter stop` | Stop whatever Demeter is doing. An open node window is closed first. |

## Moving from Odysseus

The gatherer used to live in Odysseus. On first load, Demeter reads your gather lists, where a
finished run takes you and the repair threshold from Odysseus's config, as long
as Demeter has no lists of its own yet. It does this once. Odysseus's copy is not touched.

---

## For other plugins

Every gate is guarded: nothing Demeter throws reaches the caller, and a refusal is a `false` with
the reason in Demeter's log (`pluginConfigs/Demeter/demeter.log`). Gates are extend-only: a
published gate never changes its signature or meaning. JSON is camelCase in both directions (names
are matched case-blind on the way in).

### General

| Gate | Type | Meaning |
|---|---|---|
| `Demeter.IsBusy` | `Func<bool>` | Demeter is driving the character: a list run (yours or a plugin's), a single item or fishing, including the node or cast it is finishing after a stop, and a trip home about to be sent after a finished run. Daedalus's busy bridge polls it. |
| `Demeter.RequestStopAfterRun` | `Func<bool>` | Finish the node visit or cast in hand, then stop and stay stopped: no next spot, list item or cast, no trip home. On the way to a node or hole it stops at once. `true` when Demeter was busy and will stop (or already is stopping); `false` when idle, with nothing left armed. Asking again changes nothing. A fresh start clears it. Wait for `Demeter.IsBusy` to read false. |
| `Demeter.GetVersion` | `Func<int>` | The IPC version, currently `2`. It goes up when gates are added. |
| `Demeter.Ui.ToggleLists` | `Action` | Show or hide the gather lists window. |

### List level: a caller's own named list

| Gate | Type | Meaning |
|---|---|---|
| `Demeter.Gather.CanGather(uint itemId)` | `Func<uint,bool>` | This character could gather the item unattended right now: a node exists, the class is unlocked and high enough, and it is not a fish. |
| `Demeter.Gather.Start(string json)` | `Func<string,bool>` | Gather into the caller's own list: one per caller, named after it, shown beside yours and reused every time, with completed rows removed. Runs only that list. All-or-nothing; `true` means it began. Refused while anything else is running. |
| `Demeter.Gather.SetList(string json)` | `Func<string,bool>` | The same request and the same validation, but it only writes the caller's list. Nothing starts; you press Gather. Allowed while something else runs. |
| `Demeter.Gather.Stop()` | `Action` | Stops a run that came through `Start`. A run you started yourself is left alone. |
| `Demeter.Gather.IsRunning` | `Func<bool>` | True only for a run that came through `Start`. |
| `Demeter.Gather.GetStatusJson()` | `Func<string>` | `{state, external, status, currentItemId, remaining, outcomes:[{itemId, target, held, reached, note}]}`. A note or status containing "full" means the bags are full. |

The request:

```json
{ "requestedBy": "Hephaestus", "returnHome": false,
  "items": [ { "itemId": 5111, "targetCount": 30 }, { "itemId": 5106, "quantity": 5 } ] }
```

`targetCount` is a bag count. `quantity` means that many more than the bags hold now. Only what is
still owed goes on the list. `returnHome` says whether the player's "Afterwards" setting applies
when the run ends.

### Item level: one item, for a caller that drives its own flow

| Gate | Type | Meaning |
|---|---|---|
| `Demeter.Item.CanGather(uint itemId, uint territoryHint)` | `Func<uint,uint,bool>` | A node with coordinates yields it (the hint prefers that zone), and Demeter is switched on. |
| `Demeter.Item.WhyNot(uint itemId, uint territoryHint)` | `Func<uint,uint,string>` | Why `CanGather` said no, for the caller's log. |
| `Demeter.Item.DescribeJson(uint itemId)` | `Func<uint,string>` | `{zoneId, where, timer, waitSeconds, upForSeconds}`. `waitSeconds` and `upForSeconds` are `null` unless only timed nodes yield it. |
| `Demeter.Item.Start(uint itemId, int count, int collectability, uint territoryHint)` | `Func<uint,int,int,uint,bool>` | Gather `count` more at `collectability` or better (0 for a plain item). Demeter ticks it itself. Refused while a list runs. |
| `Demeter.Item.Stop()` | `Action` | Stop it. An open node window is closed first, so `busy` stays true until it has. |
| `Demeter.Item.GetStatusJson()` | `Func<string>` | `{busy, faulted, atNode, status}`. `faulted` with `status` gives the reason. |

---

## Building

```
dotnet build Demeter/Demeter.csproj
dotnet test Demeter.Tests
```

The Debug build lands in `Demeter/bin/Debug/Demeter.dll`. Add that path to Dalamud's dev plugin
locations.
