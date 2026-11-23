# Changelog

<!-- LATEST-START -->
## v0.1.10 — 2026-11-23

- A node is gathered out before it is left. Once enough is in the bag, a timed node goes on giving its item to the end (then its Dark Matter Cluster, crystals or anything else on it); an ordinary node's remaining gathers go on its best cluster, crystal or shard row, or on the item where it has none; a collectable node is worked until its integrity is spent. A timed node left with gathers on it stopped behind the game's "leave this node?" prompt
- That prompt, when a run is stopped mid-node, is answered: the box beside Yes is ticked and Yes pressed
- A gather for a class with no gearset makes one: the class's best tool from the Armoury Chest or the bags is put on, the game's Recommended Gear is put on with it, and that is saved as a new gearset (Miner, Botanist and Fisher). With no tool, no free gearset slot, or the game refusing, the run stops and says which

## v0.1.9 — 2026-11-22

- A node counts as showing from four levels below its own: a Botanist 52 works Rainbow Cotton Boll's level 55 nodes, which v0.1.8 locked until 55. A Botanist 49 still does not see them
- Items whose nodes wait on a folklore tome (the legendary nodes, 116 items) or a quest (the Rarefied collectables on "Inscrutable Tastes", and the Oddly Specific, Oddly Delicate and Connoisseur's items on theirs) are locked in the gather lists until the tome is read or the quest done, and the lock says which
- Fish lock the same way: the 296 that bite only once their region's Tome of Ichthyological Folklore is read, and any fish whose holes are all above the Fisher's level

## v0.1.8 — 2026-11-22

- An item whose nodes are all above the gatherer's level is locked in the gather lists: a lock in place of its dot, the name greyed, and on hover what it needs ("needs Botanist 55; this character is Botanist 49"). A run skips it, timed or not, and it unlocks by itself at that level. Nodes above your level do not show, so Rainbow Cotton Boll had a Botanist 49 going round Twinpools finding nothing
- A request from another plugin for such an item is turned down with the same reason instead of setting off

## v0.1.7 — 2026-11-22

- The Gather lists window can be minimized to an edge tab of its own, beside Demeter's: point at it to pop the lists out, click to pin, drag it to any edge, right-click to get the window back. Each tab keeps its own spot. "Add to Demeter" from a right-click menu leaves the lists in the tab when they live there

## v0.1.6 — 2026-11-22

- Timed nodes are farmed window after window until the target is reached: only windows in a row that give nothing count toward giving up, where every window counted and the run stopped after the third
- An item only timed nodes give is waited for in its real windows. Leftover rows in the game data with no zone added windows of their own, so Coerthan Tea Leaves, the clusters, Adamantite Ore, Old World Fig and Chysahl Greens were gone for when no node was up, found nowhere, and skipped
- Truth of Forests or Truth of Mountains is put on before a gather sets off, when it is learned and off (changing class takes it off): without it a timed node never shows. If it will not go on, the log says so and the gather carries on
- A timed node is worked with Blessed Harvest II or King's Yield II (or the first rank, as GP and levels allow), once a node, before its first gather
- Shards, crystals and clusters are gathered with The Twelve's Bounty on every gather the GP pays for, and The Giving Land whenever its recast is up (level 74); the two stack

## v0.1.5 — 2026-11-21

- Right-clicking a gatherable item in the inventory or the crafting log (a recipe's result or one of its materials) offers "Add to Demeter: (the list on screen)" and "Add to new Demeter list", which makes a list named after the item. The Gather lists window opens on the list changed. Items no node or fishing hole gives are not offered
- The main window has a list of modes down its left side: Gathering, as it was, and Ocean Fishing
- Ocean Fishing, first part: the panel counts down to the next sign-up and says when the boat leaves in your time, keeps the route (Indigo from Limsa; Ruby from Kugane once its aetheryte is attuned) and where to go afterwards, and shows the voyage under way: route, stop, what the boat is doing, and the spectral current. It says when Fisher or the quest "All the Fish in the Sea" is missing. Signing up and fishing the voyage are not in yet
- "Return" as the Afterwards choice now answers the game's "Return to your home point?" itself, instead of leaving the window up. Stop cancels it

## v0.1.4 — 2026-11-15

- An item is gathered in a zone this character can work: high enough for its nodes, with an aetheryte attuned to get there. Ice Crystal went to Yak T'el for its many spots, on a level 50 character with no way in, instead of the South Shroud
- When Ariadne finds no way to any spot in a zone, the gather moves to the next zone that has the item rather than stopping, and passes that zone over for the rest of the session
- The log names zones as well as numbering them: "territory 1189 (Yak T'el)"

## v0.1.3 — 2026-11-15

- A node the game opens and shuts again at once (stood just too far for it) is no longer taken for a Revisit and asked again every second: Demeter walks right up to it and asks once more, and if it still shuts, leaves it two minutes and goes on to the next

## v0.1.2 — 2026-11-15

- A long gather no longer stops part way with "worked all stops twice without filling the bag": only visits that add nothing count toward that limit, so a 400-count list keeps going while the nodes keep giving
- When Ariadne finds no way to any of an item's spots in a zone, the run stops after one lap and says so (Ariadne's mesh for the zone, not the nodes) instead of trying every spot twice

## v0.1.1 — 2026-11-15

- A log button in the main window's top row opens demeter.log's folder with the file selected, ready to send
- A node Ariadne cannot get within reach of (on a rock, above a lip) is tried from where it stopped instead of walked at for three minutes; one that will not open from there is set aside for the rest of the run and the next node gone for. A circuit stop Ariadne cannot quite reach counts as reached
- Cosmic missions fished the way a Gold Star was fished by hand: the GP spent on a mission comes back when it is complete, so once a mission's prize fish is known, Prize Catch goes on before every cast — never out of the GP an Identical Cast would need — with Big-game Fishing on top at two Angler's Art stacks. A mission whose first cast will not go in gets Collect turned on and one more cast; when that works the mission is remembered (and shipped) as needing Collect, and it goes on before the first cast from then on. Each special action is pressed at most once a cast, so one the game refuses holds nothing up

## v0.1.0 — 2026-11-14

### Gathering, moved out of Odysseus
- Demeter is Odysseus's gatherer as a plugin of its own. It has the same node runner, collectable rotation, timed-node windows, gather lists, presets, self-repair and "Afterwards" home trip. Your gather lists and settings are brought over from Odysseus's config on first load
- Travel: a Lifestream teleport to the attuned aetheryte nearest the node, then Ariadne takes it there, flying where the zone's currents are done. A city district with no crystal of its own (Limsa's Upper Decks, the halves of Gridania, Ul'dah's steps) is reached through the city's crystal and a Lifestream aethernet hop. Demeter lands to work the node and waits out a fight Daedalus is having. Ariadne is required; vnavmesh is not used
- An edge tab, stacked with Daedalus's, Charon's and Odysseus's
- Abilities: Demeter never presses a gathering action the character has not learned, and "What's missing" lists every Miner, Botanist and Fisher action still to learn with its level and unlocking quest (collectables need "Inscrutable Tastes")
- Fishing, first part: `/demeter fish` looks any fish up — the bait and mooch road, hookset and tug, predators, weather, time — and counts down to its next window at every hole that has it. The data is GatherBuddy Reborn's (2,616 fish), checked against the game and the fish tracker
- Fishing, second part: "Go there" on any hole in the Fish window puts Fisher on, travels there (Lifestream, then Ariadne), stands where people actually fished from (GatherBuddy Reborn's recorded stand points, most used first), faces the water and makes a test cast — reeled straight back in. A point that does not work is remembered and skipped from then on; one that works is tried first next time. Only a point Demeter reached the zone for and still could not stand on or cast from counts against it; a trip that could not get into the zone at all marks nothing (marks left by the earlier behaviour are cleared once)
- Fishing, third part: "Fish" on any hole catches as many of a fish as you ask. It puts the bait from the fish data on, casts, judges every bite by when it came and how it tugged against what bites there (GatherBuddy Reborn's recorded bite times), hooks what it wants and lets the rest go, moochs along the road to the target, catches the predators first when the fish needs Fisher's Intuition, and stops at the count or when the fish's window closes. Only learned actions are used. The cast that proves the stand point is kept as the first cast when the right bait is on. A wanted fish never recorded biting that bait at that hole gets every bite with its tug. The Fish window shows the bait on the line and how many are left
- Fish on the gather lists: add a fish like any item. Weather- and time-bound fish wait their window while the rest is gathered, and a fish whose window opens is gone for straight away. The best hole is chosen for you: in reach of your Fisher level, open now if any is, the most-fished otherwise
- Collectable fish: Collect goes on for a collectable ask, and each catch is kept only at the collectability asked for — so Odysseus's custom-delivery fish, and Hephaestus's and Odysseus's quest fish, are fished through the same gates as gathered items
- IPC for other plugins: `Demeter.Gather.*` for a caller's own named list (Hephaestus) and `Demeter.Item.*` for one item (Odysseus), plus `Demeter.IsBusy` for Daedalus. `Demeter.Gather.SetList` is new: it hands a list over without starting it
- Fishing where you stand, for Cosmic Exploration: "Fish here" in the Fish window, and `Demeter.Fish.CanFishHere`, `Start`, `Stop` and `GetStatusJson` for Selene (IPC version 3). It fishes with the bait that is on — a Cosmic mission's own, read where the game keeps it, counted in the Cosmopouch — and never changes it. Every bite is hooked unless the caller names the fish it wants. It ends at the caller's stop (after the cast in hand), a cast or time budget, the bait running out, or "the fish sense something amiss", each with its own code, and a first cast that does not go in says there is no water there. Each hook, cast and pause waits a time drawn fresh from a seeded range, with a longer breather every so many casts. The Fish window's bait readout counts the Cosmopouch too
- Cosmic points and GP: the points each fish earns on each mission are read from the game's "You earn a score of N" and kept in a table (a Debug build records them to `fish-scores.json`; a release ships the table). After landing a mission's prize fish — at least half the best known average — fishing here uses Identical Cast, then Triple Hook (or Double Hook) on its bite, as far as GP goes, and Thaliak's Favor at three Angler's Art stacks; with too little GP it fishes on with a plain Hook. Only learned actions. "All stellar mission objectives complete!" ends the session cleanly (code `missionComplete`) instead of as a cast that would not go in. The Fish window shows GP, how long until it is full, and the session's points; the status JSON carries points, mission and GP
- Missions scored by their largest fish ("Your score has changed to N"): every catch's length is kept per fish and mission, with what an ilm is worth (100 points on Botanifish Research). A bite whose tug only fish never landed longer than the score already stands is let go — the mission's biggest fish is always hooked — and after landing the biggest comes Identical Cast and Prize Catch, with no Double or Triple Hook. The Fish window shows the mission's score as the game keeps it
- `Demeter.Fish.StandAt` (IPC version 4): walk to the first of a caller's points that can be reached, in the current zone, and face the water — Ariadne and feet on the ground, never a teleport, no test cast, nothing remembered. Short ways are walked; a far one may be ridden or flown. The status JSON says `standing`, `stood` or `faulted` (`cannotReach`). The Fish window's "Mark spot" and "Stand at mark" try it by hand
- `Demeter.RequestStopAfterRun` (IPC version 2): finish the node or cast in hand, then stop and stay stopped, for a plugin about to move the character (Argus's unattended resend). `Demeter.IsBusy` now also covers a trip home about to be sent

<!-- LATEST-END -->
