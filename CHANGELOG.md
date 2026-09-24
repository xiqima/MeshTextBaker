# Changelog — MeshTextBaker

## 1.0.1

* UV Editor: rotated corner handles follow the cursor instead of the zone's local axes.
* UV Editor: zones can be moved and resized outside the 0–1 UV square (only the overlap is baked).
* UV Editor: Duplicate Zone replaces Create Sub-Zone. Overflow-link arrows are no longer drawn.
* Per-zone Bake checkbox. A disabled zone is removed from the next bake queue. API: `TextZone.bakeEnabled`, `SetZoneBakeEnabled`, `GetBakeQueue`.
* A zone left out of the queue does not require a renderer. A surface with no zones still reports the missing-renderer error.
* Duplicate Zone also clears the page-parent link, and is available from the inspector and the UV canvas context menu.
* Scene view zone overlay follows zone rotation.
* Zone header: the bake checkbox has no label and sits just left of the duplicate icon.



## 1.0.0

* Initial release

