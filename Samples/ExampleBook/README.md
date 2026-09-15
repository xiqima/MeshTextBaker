# MeshTextBaker Example Book sample

A flippable localized 3D book demo: `MTB Example - book` scene, `MeshTextBook`
prefab, `MeshTextAsset - Dracula` (English + Russian), page-flip animations and
book UI. Built-in render pipeline (Standard materials).

## Run it

1. Import TMP Essential Resources (once per project):

   ```text
   Window → TextMeshPro → Import TMP Essential Resources
   ```

   Books bake with TMP's default font — without this step the pages stay blank.
2. Import this sample: **Package Manager → Mesh Text Baker → Samples → Example Book → Import**.
3. Open `scenes/MTB Example - book` and press Play.

## Notes

- Render pipeline: the sample uses Built-in Standard materials. In URP/HDRP
  projects run **Window → Rendering → Render Pipeline Converter** (Built-in to
  SRP) after importing the sample.
- Input: the demo UI uses the built-in Input Manager. If your project is set to
  Input System only (the default in new Unity 6 projects), set **Project Settings
  → Player → Active Input Handling → Both** and restart the editor.
- Non-Latin text needs a TMP font with matching coverage (or a fallback chain) —
  assign one as Default Font or a zone font override.

## Demo text sources (public domain)

- English: Bram Stoker, *Dracula* (1897).
- Russian: Sandrova's 1912 translation of *Dracula* (public domain).

Version: 1.0.0

## Support

MeshTextBaker is free and open-source (MIT). If this sample helped you,
consider supporting the author: **[♥ Support on Boosty](https://www.boosty.to/xiqima)**.
