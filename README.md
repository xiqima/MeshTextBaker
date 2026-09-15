# MeshTextBaker

[![Support on Boosty](https://img.shields.io/badge/Support-Boosty-orange)](https://www.boosty.to/xiqima)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

<img width="250" height="250" alt="MTB logo 1" src="https://github.com/user-attachments/assets/dfecad84-ad22-4eb8-8ea5-7c740ac5d524" />

MeshTextBaker renders TextMeshPro text and images into textures used by 3D mesh materials. The package is intended for books, notes, letters, signs, labels, screens, and other mesh-based text surfaces.

The generated text is stored in material textures. A visible TMP object or Canvas is not required after the bake.


## Requirements

- Unity 2022.3 or newer
- TextMeshPro
- TMP Essential Resources
- Built-in Render Pipeline, URP, or HDRP

Unity Localization is optional.

## Installation

**Option A — OpenUPM (recommended).** Add the OpenUPM scoped registry once per project:

```text
Edit → Project Settings → Package Manager → Scoped Registries → +
Name: OpenUPM
URL: https://package.openupm.com
Scope(s): com.xiqima.meshtextbaker
```

Then `Window → Package Manager → Packages: My Registries → Mesh Text Baker → Install`.
CLI alternative: `openupm add com.xiqima.meshtextbaker`.

**Option B — Git URL.** `Package Manager → + → Add package from git URL…`:

```text
https://github.com/xiqima/MeshTextBaker.git
```

(Any released version can be pinned with `#1.0.0`, e.g. `…/MeshTextBaker.git#1.0.0`.)

**Option C — manual.** Download the repo as ZIP and use `Add package from disk…`.



After installing, if TMP resources are not present, import them through:

```text
Window → TextMeshPro → Import TMP Essential Resources
```

## Example scenes

The package ships two samples:

- **Example Book** — flippable 3D book with localized text (en/ru).
- **Example Note** — feature showcase: zones, rich text, markdown, images, PBR, overflow.

1. Import TMP Essential Resources (see above).
2. Import a sample: `Package Manager → Mesh Text Baker → Samples → Example Book → Import`.
3. Open the example scene and press Play.

## Main components

### MeshTextSurface

Stores bake settings, default values, text zones, RenderTexture pools, generated material instances, and book text settings.

### TextZone

Defines a rectangular UV region, target renderer, material slot, text source, layout, compositing, overflow, and optional PBR output.

Zone roles:

- `Main`
- `OverflowOnly`
- `PageSlot`
- `PageNumber`
- `Image`

### MeshTextAsset

Stores a key, fallback locale, authoring settings, and text entries for one or more locales.

### BookController

Controls a two-stack book rig with one two-sided flip sheet. It handles open/close state, page turns, pagination, bookmarks, page numbers, stack visibility, and optional stack-thickness controls.

## Basic setup

1. Add `MeshTextSurface` to a mesh object or a parent object.
2. Assign a TMP font in `Default Values`.
3. Open the UV Editor from the surface inspector.
4. Create and position a zone over the required UV area.
5. Create a text asset:

   ```text
   Assets → Create → Mesh Text Baker → Text Asset
   ```

6. Open the Text Editor, create a locale, and enter text.
7. Assign the TextAsset to the zone.
8. Set `Preview Locale` and run `Test Bake`.

## Book setup

A book rig uses:

```text
Book Root
├── PageStack_L
├── PageStack_R
└── FlipPivot
    └── FlipSheet
```

`FlipSheet` uses separate material slots for its front and back sides.

Create four `PageSlot` zones on one `MeshTextSurface`:

- left stack;
- right stack;
- flip front;
- flip back.

Assign the surface, renderers, flip pivot, and optional Animator to `BookController`. Assign a Book TextAsset or Book Localization Key in `Book Settings`.

## Text authoring

The Text Editor provides locale tabs, Markdown controls, TMP rich-text controls, custom material tags, preview, search, and text undo/redo.

Supported content includes:

- Markdown headings, lists, emphasis, code spans, and simple tables;
- TMP color, size, alignment, underline, strike, font, and spacing tags;
- inline images;
- inline font changes and external TTF/OTF files;
- emission, surface, height, blend, and opacity tags;
- book page breaks and bookmarks.

## Zones and UV editing

Zones can target the surface renderer or another MeshRenderer/SkinnedMeshRenderer. Multiple zones may share one material texture or target separate material slots.

The UV Editor provides zone creation, selection, movement, resizing, rotation, zoom, pan, snapping, match-size operations, overlap selection, and PageNumber child creation.

Zone order in the surface inspector is the bake order.

## Bake modes

- `Manual` — bake through inspector controls or API.
- `EditorBake` — rebake editor preview when Preview Locale changes.
- `RuntimeBakeOnStart` — bake during `Start()`.

## Output textures

The bake pipeline can generate:

- albedo;
- emission;
- metallic/smoothness mask;
- height/parallax data.

Material property names are detected for Built-in, URP, and HDRP. Explicit property names can be set for custom shaders.

## Localization

A surface can read embedded `MeshTextAsset` translations or use an `ITextLocalizationProvider`.
Provider discovery is automatic: add one provider anywhere in the scene and every surface with an
empty **Localization Provider** field uses it (explicit per-surface assignment still wins).

Optional providers include:

- `UnityLocalizationProvider` for Unity String Tables;
- `ExternalOverrideLocalizationProvider` for files under `<GameRoot>/Localization/<locale>`.

The localization export window writes keyed MeshTextAssets to a base-language folder:

```text
Tools → Mesh Text Baker → Localization Export
```

## Bake to Asset

`Bake to Asset` writes generated textures and persistent materials for content that does not need runtime rebaking.

## Runtime API

```csharp
var surface = GetComponent<MeshTextBaker.MeshTextSurface>();
surface.BakeForLocale("ru");
surface.ClearBake();

var book = GetComponent<MeshTextBaker.BookController>();
book.Open();
book.Close();
book.NextPage();
book.PreviousPage();
book.GoToSpread(2);
book.GoToBookmark("chapter2");
book.SetLocale("de");
```

## Documentation

See [TechnicalReference.md](Documentation~/TechnicalReference.md).

## License & authorship

© 2026 xiqima. All package content — code, scenes, models, materials,
textures, and documentation — is original work by the author, **except**
the demo novel text in the Example Book sample: Bram Stoker's *Dracula*
(1897, English) and Sandrova's 1912 Russian translation, both in the
public domain (see "Demo text sources" in `Samples/ExampleBook/README.md`
and `Third-Party Notices.txt`).

This package is released under the [MIT License](LICENSE).
