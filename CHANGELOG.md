# Changelog

<!-- LATEST-START -->
## v0.0.1 — unreleased

### Gathering, moved out of Odysseus
- Demeter is Odysseus's gatherer as a plugin of its own. It has the same node runner, collectable rotation, timed-node windows, gather lists, presets, self-repair and "Afterwards" home trip. Your gather lists and settings are brought over from Odysseus's config on first load
- Travel: a Lifestream teleport to the attuned aetheryte nearest the node, then Ariadne takes it there, flying where the zone's currents are done. Demeter lands to work the node and waits out a fight Daedalus is having. Ariadne is required; vnavmesh is not used
- An edge tab, stacked with Daedalus's, Charon's and Odysseus's
- Abilities: Demeter never presses a gathering action the character has not learned, and "What's missing" lists every Miner, Botanist and Fisher action still to learn with its level and unlocking quest (collectables need "Inscrutable Tastes")
- Fishing, first part: `/demeter fish` looks any fish up — the bait and mooch road, hookset and tug, predators, weather, time — and counts down to its next window at every hole that has it. The data is GatherBuddy Reborn's (2,616 fish), checked against the game and the fish tracker
- Fishing, second part: "Go there" on any hole in the Fish window puts Fisher on, travels there (Lifestream, then Ariadne), stands where people actually fished from (GatherBuddy Reborn's recorded stand points, most used first), faces the water and makes a test cast — reeled straight back in. A point that does not work is remembered and skipped from then on; one that works is tried first next time
- IPC for other plugins: `Demeter.Gather.*` for a caller's own named list (Hephaestus) and `Demeter.Item.*` for one item (Odysseus), plus `Demeter.IsBusy` for Daedalus. `Demeter.Gather.SetList` is new: it hands a list over without starting it

<!-- LATEST-END -->
