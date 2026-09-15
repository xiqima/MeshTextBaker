# MeshTextBaker Example Note sample

A feature showcase: `MTB Example - note` scene with seven note prefabs,
each demonstrating a different MeshTextBaker capability:

1. **Welcome note (Baked)** — pre-baked textures shipped with the sample,
   visible immediately without pressing Play.
2. **Zone placement variability** — several zones on one mesh.
3. **Text Processing** — Markdown + TMP rich text.
4. **Inline Image and Zone Image** — images inside text and image-only zones.
5. **PBR** — emission / metallic / smoothness / height output.
6. **Overflow Behavior** — Shrink to fit and Continue to Sub Zones
   (see the other side of the note).

Built-in render pipeline (Standard materials).

## Run it

1. Import TMP Essential Resources (once per project):

   ```text
   Window → TextMeshPro → Import TMP Essential Resources
   ```

   Notes bake with TMP's default font — without this step the pages stay blank.
2. Import this sample: **Package Manager → Mesh Text Baker → Samples → Example Note → Import**.
3. Open `scenes/MTB Example - note` and press Play.

## Notes

- Render pipeline: the sample uses Built-in Standard materials. In URP/HDRP
  projects run **Window → Rendering → Render Pipeline Converter** (Built-in to
  SRP) after importing the sample.
- Input: the demo UI uses the built-in Input Manager. If your project is set to
  Input System only (the default in new Unity 6 projects), set **Project Settings
  → Player → Active Input Handling → Both** and restart the editor.
- Non-Latin text needs a TMP font with matching coverage (or a fallback chain) —
  assign one as Default Font or a zone font override.

## Support

MeshTextBaker is free and open-source (MIT). If this sample helped you,
consider supporting the author: **[♥ Support on Boosty](https://www.boosty.to/xiqima)**.
