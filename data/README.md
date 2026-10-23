These English catalog snapshots are from `Nereziel/cs2-WeaponPaints`,
`website/data/`, commit `fa8936f3959310acf94de410bc5bd0015f34ff24`.

The JSON files are copied into build/publish output and embedded in
`WeaponPaints.dll`. Server files in `plugins/WeaponPaints/data/` take priority;
missing or unreadable catalogs fall back to the bundled English versions.

Keep these five files together when updating the snapshot: skins, gloves,
agents, music, and collectibles. The web module's `cache/` files use a different
schema and cannot replace these catalogs.
