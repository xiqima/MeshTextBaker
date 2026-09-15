# MeshTextBaker Technical Reference

## Contents

1. [Purpose](#purpose)
2. [Requirements](#requirements)
3. [Runtime architecture](#runtime-architecture)
4. [MeshTextSurface](#meshtextsurface)
5. [Bake settings](#bake-settings)
6. [Default values](#default-values)
7. [TextZone](#textzone)
8. [Zone roles](#zone-roles)
9. [UV Editor](#uv-editor)
10. [MeshTextAsset](#meshtextasset)
11. [Text Editor](#text-editor)
12. [Text processing](#text-processing)
13. [Markdown syntax](#markdown-syntax)
14. [Rich text](#rich-text)
15. [Custom tags](#custom-tags)
16. [Inline images](#inline-images)
17. [Inline fonts](#inline-fonts)
18. [Overflow](#overflow)
19. [Compositing](#compositing)
20. [PBR textures](#pbr-textures)
21. [Book data flow](#book-data-flow)
22. [Book rig](#book-rig)
23. [BookController](#bookcontroller)
24. [Page numbers](#page-numbers)
25. [Localization providers](#localization-providers)
26. [External localization overrides](#external-localization-overrides)
27. [Localization export](#localization-export)
28. [Bake to Asset](#bake-to-asset)
29. [Resource lifecycle](#resource-lifecycle)
30. [Public API](#public-api)
31. [Performance](#performance)
32. [Extension points](#extension-points)

---

## Purpose

MeshTextBaker converts text layout and image content into textures sampled by 3D mesh materials.

The package handles:

- TMP text layout in rectangular UV zones;
- localized text sources;
- multiple renderers and material slots;
- overflow chains;
- page-based book text;
- inline images and font changes;
- albedo, emission, mask, and height output;
- runtime material assignment;
- persistent editor export.

## Requirements

| Requirement | Value |
|---|---|
| Unity | 2022.3 or newer |
| Text | TextMeshPro |
| Render pipelines | Built-in, URP, HDRP |
| TMP resources | Essential Resources imported |

The runtime assembly references `Unity.TextMeshPro`. The Unity Localization integration is contained in a conditional assembly.

## Runtime architecture

The bake key is a renderer/material pair:

```text
(Renderer, materialIndex)
```

All zones targeting the same pair are rendered into one texture.

Bake sequence:

1. Select zones.
2. Resolve locale text.
3. Apply text processing.
4. Apply overflow or page slicing.
5. Group zones by target.
6. Initialize target textures from original material textures or fallback values.
7. Generate TMP layout and mesh data.
8. Draw zone content into target textures.
9. Create or update material instances.
10. Assign generated textures and pipeline properties.

TMP layout uses hidden non-rendering TextMeshPro components. Generated geometry is copied before drawing so later TMP updates do not alter an active bake.

Immediate rendering sets its own render target, viewport, projection, model-view matrix, screen parameters, and GL state. The previous graphics state is restored after each draw.

## MeshTextSurface

`MeshTextSurface` is the main component.

Responsibilities:

- serialized bake configuration;
- text-zone collection;
- renderer and mesh resolution;
- RenderTexture pooling;
- original/generated material tracking;
- locale-provider access;
- book text cache;
- page measurement API;
- bake and clear operations.

A surface may be attached to:

- an object with MeshRenderer and MeshFilter;
- an object with SkinnedMeshRenderer;
- an object without a renderer when every zone specifies a target renderer.

### Bake modes

| Mode | Operation |
|---|---|
| `Manual` | No automatic bake |
| `EditorBake` | Editor preview follows Preview Locale changes |
| `RuntimeBakeOnStart` | `BakeForLocale` is called from `Start()` |

### Material handling

The surface records the original shared material for each target. The original material's `_BaseColor`/`_Color` tint is multiplied into the albedo background during initialization; generated materials use white albedo tint so baked text colors are not multiplied a second time. Generated material instances use `DontSave` flags and are assigned to renderer slots. `ClearBake()` restores original materials.

Partial book bakes reuse target textures and material instances. Before page-specific properties are assigned, the generated material is reset from the original material.

## Bake settings

`BakeSettings` is serialized inside MeshTextSurface.

### Texture settings

| Field | Description |
|---|---|
| Resolution | Long side in pixels; per-target WxH follows the background texture aspect (square when no texture) |
| Texture Property Name | Albedo property override |
| Emission Property Name | Emission property override |
| Height Property Name | Height/parallax property override |
| Base Albedo Texture | Explicit source texture |
| Generate Mip Maps | RenderTexture mip generation |

### Automatic material properties

| Output | Built-in / URP | HDRP |
|---|---|---|
| Albedo | `_MainTex`, `_BaseMap` | `_BaseColorMap` |
| Emission | `_EmissionMap` | `_EmissiveColorMap` |
| Metallic/smoothness | `_MetallicGlossMap` | `_MaskMap` |
| Height | `_ParallaxMap` where available | `_HeightMap` |

### Libraries

`imageLibrary` maps names to Texture2D objects.

`fontLibrary` maps names to TMP_FontAsset objects.

### Book measurement

`measureWindow` limits the source substring considered during one page-measurement operation.

## Default values

The Default Values section contains:

| Field | Use |
|---|---|
| Default Font | Font used when `fontOverride` is null |
| Default Font Size Mode | Size mode used when zone Font Size is 0 |
| Default Font Size | Size used when zone Font Size is 0 |
| Default Text Color | Color used when zone uses the default color |
| Default Alignment | Alignment assigned to created zones |
| Default Rich Text | Rich Text state assigned to created zones |
| Default Markdown | Markdown state assigned to created zones |
| Default Use PBR | PBR state assigned to created zones |
| Default Blend Mode | Blend state assigned to created zones |
| Default Opacity | Opacity assigned to created zones |

Created overflow zones copy their parent style. PageNumber zones use Page Number Defaults unless their override is enabled.

## TextZone

A `TextZone` contains:

### Target

- ID;
- name;
- role;
- target renderer;
- material index;
- list order.

### Geometry

- UV rectangle;
- rotation;
- horizontal/vertical mirror (flip);
- padding.

### Source

- localization key;
- MeshTextAsset;
- overflow link;
- PageSlot parent link.

### Layout

- font override;
- size mode;
- size;
- shrink limits;
- line spacing;
- alignment;
- text color;
- Rich Text;
- Markdown.

### Image data

- Texture2D;
- Sprite;
- tint;
- preserve aspect.

### Compositing

- blend mode;
- opacity.

### PBR

- Use PBR;
- whole-zone emission and color;
- whole-zone mask values;
- whole-zone height values.

## Zone roles

### Main

Resolves its own text source.

### OverflowOnly

Receives remaining source text from a preceding zone.

### PageSlot

Receives a logical page from the book text stream.

### PageNumber

Receives a formatted page index from BookController.

### Image

Draws a standalone texture or sprite instead of text.

## UV Editor

The UV Editor displays the source albedo texture and zone overlays.

Functions:

- add/delete zones;
- move and resize zones;
- rotate zones;
- zoom and pan;
- enable grid snapping;
- temporarily snap with Ctrl;
- magnet edges to other zones;
- copy dimensions with Match Size;
- select overlapping zones;
- create overflow children;
- create PageNumber children.

Input:

| Input | Action |
|---|---|
| LMB | Select or drag |
| Corner handle | Resize |
| Rotation handle | Rotate |
| Mouse wheel | Zoom |
| MMB | Pan |
| Alt+LMB | Pan |
| Shift+LMB | Pan |
| Ctrl while editing | Temporary snapping |
| RMB | Overlap/context menu |

## MeshTextAsset

A MeshTextAsset contains:

- `key`;
- `fallbackLocale`;
- Rich Text authoring flag;
- Markdown authoring flag;
- `LocalizedTextEntry` list.

Each entry contains a locale code and text body.

Lookup order in `GetText(locale)`:

1. exact locale;
2. asset fallback locale;
3. first entry;
4. empty string.

`TryGetTextExact` performs exact-locale lookup without fallback.

The authoring flags describe the processing expected by the text. MeshTextSurface displays the flags and compares them with zone or book processing settings.

## Text Editor

The Text Editor opens by double-clicking a MeshTextAsset or through its inspector.

Functions:

- locale creation, selection, rename, and deletion;
- text editing;
- save and auto-save;
- text undo/redo;
- find navigation;
- preview;
- character and word counts;
- Markdown and Rich Text authoring controls;
- tag reference panel.

The text field is editable only when the asset contains a selected locale.

### Authoring modes

Markdown and Rich Text toggles are stored on MeshTextAsset.

Toolbar and context-menu controls are selected from the active modes.

Bold and Italic use:

| Active processing | Inserted source |
|---|---|
| Markdown enabled | `**text**`, `*text*` |
| Markdown disabled, Rich Text enabled | `<b>text</b>`, `<i>text</i>` |

When a processing mode is disabled while related markup exists, the editor provides three operations:

- keep source markup;
- remove related formatting markup from all locales;
- cancel.

Rich Text cleanup retains structural book tags and page breaks. Markdown cleanup converts table rows to tab-separated text and removes formatting markers.

## Text processing

### Normal zones

1. Resolve text from provider or MeshTextAsset.
2. Convert Markdown when enabled.
3. Convert custom scopes to layout markers.
4. Resolve inline font tags.
5. Apply overflow.
6. Resolve inline images.
7. Build TMP geometry.
8. Draw output passes.

### Book text

Book text is resolved and processed before pagination. Custom tags are represented by zero-width TMP link markers until final layout. Page slicing repairs tags at boundaries.

## Markdown syntax

Supported syntax:

```text
**bold**
*italic*
`code`
# Heading
## Heading
- item
* item
| Column A | Column B |
|---|---|
| Value A | Value B |
```

Bold and Italic markers are converted to TMP `<b>` and `<i>` tags.

Code spans are wrapped in `<noparse>`.

Tables use positioned no-wrap rows and calculated column starts.

## Rich text

Recognized TMP tags include:

```text
<b> <i> <u> <s>
<color> <#hex>
<size>
<align>
<alpha>
<mark>
<sub> <sup>
<space> <cspace> <voffset>
<font>
<br>
<noparse>
```

Tags are passed to TextMeshPro when zone Rich Text is enabled.

## Custom tags

### Emission

```text
<emission>text</emission>
<emission=#00ffcc>text</emission>
<emission=#00ffcc i=2>text</emission>
```

### Surface

```text
<surface>text</surface>
<surface m=1 s=0.9>text</surface>
<surface h=1>text</surface>
<surface m=1 s=0.9 h=0.5>text</surface>
```

Parameters:

- `m` — metallic;
- `s` — smoothness;
- `h` — signed height.

A surface tag without values does not modify PBR textures.

### Blend

```text
<blend=multiply>text</blend>
```

Modes:

- normal;
- multiply;
- screen;
- overlay;
- add;
- subtract.

### Opacity

```text
<opacity=0.5>text</opacity>
```

### Book structure

```text
<newpage>
<bookmark=chapter>
```

## Inline images

Sources:

```text
<image=alias>
<image=res:Path/Texture>
<image=file:images/file.png>
```

Options:

| Option | Description |
|---|---|
| `h` | Height percentage relative to font size |
| `w` | Width percentage relative to font size |
| `displace` | Reserve text-flow width |
| `under` | Draw below text |
| `pivot` | `bl`, `br`, `tl`, `tr`, `c` |
| `blend` | Blend mode |
| `opacity` | Opacity multiplier |

Sprite atlas rectangles and Texture2D sources are supported.

File images are cached by path and timestamp. File size and path traversal are validated.

## Inline fonts

Sources:

```text
<font=alias>text</font>
<font=res:Fonts/Asset>text</font>
<font=file:fonts/font.ttf>text</font>
```

External `.ttf` and `.otf` files are converted to dynamic TMP font assets and cached.

## Overflow

### ShrinkToFit

Searches between minimum and maximum font sizes.

### Clip

Uses the fitting portion of source text.

### Ellipsis

Uses the fitting portion and appends an ellipsis.

### ContinueToSubZones

Passes remaining source text to the linked zone.

Measurement uses a TopLeft layout reference so vertical anchor selection does not change the amount of text that fits. Both vertical zone bounds are checked. Final geometry uses the zone alignment.

## Compositing

### Normal

Text/image output is alpha-composited directly over the target.

### Other blend modes

Content is rendered into a temporary layer and combined through the blend composite shader.

### Opacity

Zone and inline opacity affect layer alpha. Inline opacity scopes are applied immediately before final TMP layout so pagination indices remain unchanged.

## PBR textures

### Emission texture

The texture is initialized to black. Whole-zone or inline emission coverage is rendered with configured color/intensity.

### Metallic/smoothness texture

HDRP uses `_MaskMap` channels. URP/Built-in uses `_MetallicGlossMap` where available. The source mask or scalar material values initialize the texture.

### Height texture

Height output uses a neutral midpoint and signed coverage. HDRP uses height properties. URP/Built-in may use parallax properties depending on the shader.

### Material validation

Pipeline-specific material validation is called after generated maps and keywords are assigned.

## Book data flow

Book source may come from:

- Book TextAsset;
- Book Localization Key through a provider.

Processing sequence:

1. Resolve locale text.
2. Convert Book Markdown.
3. Convert custom tags.
4. extract bookmarks;
5. cache processed source;
6. measure logical pages on demand;
7. assign pages to physical PageSlot zones.

`BookPaginator` caches page starts and page text for the current locale.

## Book rig

Minimum hierarchy:

```text
Book_Root
├── PageStack_L
├── PageStack_R
└── FlipPivot
    └── FlipSheet
```

Required references:

- `MeshTextSurface`;
- left stack renderer;
- right stack renderer;
- flip sheet renderer;
- flip pivot transform.

FlipSheet uses two material slots:

- slot 0 — front;
- slot 1 — back.

Create PageSlot zones for:

- left stack;
- right stack;
- flip front;
- flip back.

The two stack page surfaces should use equivalent zone sizes for consistent pagination. The flip pivot is placed on the spine rotation axis.

Optional features:

- cover Animator;
- Animator page-turn state;
- stack thickness through transform scale or blend shapes;
- flip-sheet lift blend shapes;
- page-edge tiling compensation.

## BookController

States:

- Closed;
- Opening;
- Open;
- Turning;
- Closing.

During a turn, BookController prebakes the required stack and flip sides, animates the flip sheet, then transfers the final page to the destination stack.

After a close operation, the left stack renderer is disabled, the right stack renderer is enabled, and dynamic thickness is set to the full right stack.

Navigation:

- next/previous page;
- spread index;
- bookmark;
- instant, single-flip, or multi-flip jumps.

Locale changes invalidate page and bookmark caches.

## Page numbers

A PageNumber zone references a parent PageSlot by ID and uses the same renderer/material target.

Surface Page Number Defaults:

- font;
- size mode;
- size;
- alignment;
- color;
- padding.

A PageNumber zone may override these values.

## Localization providers

`ITextLocalizationProvider` defines string lookup by key and locale.

`ITextLocalizationProviderV2` adds:

- exact-locale lookup;
- available locale list;
- readiness state;
- localization-change event;
- initialization.

Optional adapters:

- `UnityLocalizationProvider`;
- `ExternalOverrideLocalizationProvider`.

Provider discovery is automatic: built-in providers register a scene default
(`MeshTextLocalizationRuntime.RegisterDefaultProvider`, highest priority wins) and every
`MeshTextSurface` with no explicit provider uses it. Explicit code/inspector assignment wins;
`Ignore Scene Default` (surface) is the off switch.

Fallback resolution for a surface:

1. provider exact locale;
2. MeshTextAsset exact locale;
3. parent locale;
4. English provider;
5. English MeshTextAsset;
6. MeshTextAsset fallback.

## External localization overrides

External override root:

```text
<GameRoot>/Localization/<locale>/
```

Short strings use flat JSON. File paths form key prefixes:

```text
ui/menu.json + play → ui.menu.play
```

Long documents use `.txt` or `.md`; the relative path without extension is the key.

Resource search for images/fonts:

1. active locale folder;
2. language-parent folder;
3. executable root;
4. persistent/Streaming file source;
5. embedded library/Resources fallback.

The external provider decorates a base provider and does not own UI or dialogue localization.

## Localization export

Open:

```text
Tools → Mesh Text Baker → Localization Export
```

The exporter scans keyed MeshTextAssets for one source locale.

Output:

- grouped JSON for short entries;
- TXT for long entries;
- translator README;
- report with missing keys, missing locale text, and duplicates.

Other localization systems export their own content independently.

## Bake to Asset

The editor export creates:

- albedo PNG;
- optional emission PNG;
- optional mask PNG;
- optional height PNG;
- material asset.

Data textures use linear import settings. Generated runtime state is released after persistent materials are assigned.

## Resource lifecycle

RenderTexture pools are stored per renderer/material target.

`ClearBake()`:

- restores original materials;
- destroys generated materials;
- releases pooled textures;
- clears cached page content.

Shared TMP and composite resources are released on assembly reload and application shutdown.

## Public API

### MeshTextSurface

```csharp
surface.BakeForLocale("en");
surface.BakeZones(zoneTexts, zones, "en");
surface.ClearBake();
surface.ReleaseRenderTextures();
```

### BookController

```csharp
book.Open();
book.Close();
book.NextPage();
book.PreviousPage();
book.GoToSpread(3);
book.GoToBookmark("id");
book.SetLocale("ru");
```

### Localization

Assign a provider through the surface inspector or `localizationProvider` property.

## Performance

Main resource factors:

- texture resolution;
- number of renderer/material targets;
- enabled emission/mask/height outputs;
- mipmaps;
- number of blend/PBR variants;
- dynamic font atlases;
- number of active runtime-baked surfaces.

Static content can use Bake to Asset. Runtime books can release resources while closed.

## Extension points

- `ITextLocalizationProvider` and V2 provider interface;
- custom material property names;
- custom text sources through localization keys;
- custom book control through public API;
- additional editor tooling through MeshTextSurface and TextZone data.

Text-processing extensions should preserve source indices until pagination is complete and should avoid adding visible-width control markers before measurement.

---

*MeshTextBaker is free and open-source (MIT). Support development: [♥ Boosty](https://www.boosty.to/xiqima).*
