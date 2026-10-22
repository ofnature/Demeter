# Changelog

<!-- LATEST-START -->
## v0.0.1 — unreleased

### Gathering, moved out of Odysseus
- Demeter is Odysseus's gatherer as a plugin of its own. It has the same node runner, collectable rotation, timed-node windows, gather lists, presets, self-repair and "Afterwards" home trip. Your gather lists and settings are brought over from Odysseus's config on first load
- Demeter does its own travel: a teleport to the attuned aetheryte nearest the node, then mount and fly where the zone's currents are done. It lands to work the node, waits out a fight Daedalus is having, and switches between ground and air when the path wedges
- An edge tab, stacked with Daedalus's, Charon's and Odysseus's
- IPC for other plugins: `Demeter.Gather.*` for a caller's own named list (Hephaestus) and `Demeter.Item.*` for one item (Odysseus), plus `Demeter.IsBusy` for Daedalus. `Demeter.Gather.SetList` is new: it hands a list over without starting it

<!-- LATEST-END -->
