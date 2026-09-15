# Localization integrations and fan overrides

MeshTextBaker does **not** replace a project's localization system. It remains a consumer of `ITextLocalizationProvider` and ships two optional adapters:

- `UnityLocalizationProvider` — exact-locale String Table lookup when `com.unity.localization` is installed.
- `ExternalOverrideLocalizationProvider` — a decorator that checks loose mod files first, then delegates to Unity Localization, I2, or another custom provider.

Embedded `MeshTextAsset` text remains the final authoring/fallback source.

## Automatic provider discovery (no per-surface wiring)

Every `MeshTextSurface` resolves its provider in this order:

1. code-assigned (`surface.localizationProvider = ...`);
2. inspector-assigned **Localization Provider** (explicit, wins over everything automatic);
3. the scene default: any registered `ITextLocalizationProvider`;
4. edit-mode scene search (editor preview only);
5. embedded `MeshTextAsset` fallback.

Built-in providers self-register as the scene default, so in practice you add **one**
provider GameObject per scene and leave every surface's **Localization Provider** empty:

- all surfaces (and `BookController`, through its surface) pick it up automatically;
- when the scene default appears, changes, or disappears, automatic surfaces resubscribe
  (already-baked `RuntimeBakeOnStart` surfaces rebake; book-owned surfaces defer to the book);
- editor previews and Test Bake find the scene provider without entering Play Mode.

Every provider in the scene registers automatically. When several are present, the
highest priority wins: External Override uses built-in base `100` (plus its **Default
Priority Offset**), so the decorator beats its own base provider; ties go to the most
recently registered. Tick **Ignore Scene Default** on a surface to pin that surface to
embedded text. The surface inspector shows the effective provider and offers
**Refresh Auto Provider** / **Pin to Field** / **Clear**.

Custom providers opt into discovery with one call:

```csharp
private void OnEnable()
{
    MeshTextBaker.MeshTextLocalizationRuntime.RegisterDefaultProvider(this);
}

private void OnDisable()
{
    MeshTextBaker.MeshTextLocalizationRuntime.UnregisterDefaultProvider(this);
}
```

(For `Awake`-time readers, registering in `Awake` as well is recommended — the built-in
providers do both.)

## Unity Localization setup

1. Install `com.unity.localization`.
2. Add **Unity Localization Provider** to an object.
3. Set its Default Table.
4. Leave surfaces on automatic discovery, or assign the provider to
   `MeshTextSurface → Localization Provider` explicitly.
5. Use `Table::Entry` to select an explicit table, or a plain key to use Default Table.

The integration is in an optional asmdef and does not make Unity Localization a hard package dependency.

## External Override setup

1. Add **External Override Localization Provider**.
2. Assign Unity Localization / I2 / custom provider as **Base Provider** (optional).
   A runtime base can also be injected from code via `provider.baseProvider = ...`.
3. Leave surfaces on automatic discovery (recommended), or assign the External Override
   provider to the relevant `MeshTextSurface` components explicitly.
4. Ship `<GameRoot>/Localization/<locale>`.

Lookup for each locale is:

1. external loose-file key;
2. base provider exact-locale key;
3. embedded MeshTextAsset exact-locale text;
4. parent locale (for example `ru-RU → ru`) in the same order;
5. English external/base/embedded;
6. legacy MeshTextAsset fallback.

Missing fan files never erase valid base text.

## Export a complete MTB baseline

Open:

```text
Tools → Mesh Text Baker → Localization Export
```

Choose source locale (`en`) and the game/project root. The tool scans every keyed `MeshTextAsset` and writes:

```text
Localization/en/...
Localization/en/README_TRANSLATORS.txt
```

Short one-line entries are grouped into flat JSON. Multiline or long entries become individual TXT files. Duplicate/missing keys and missing source-locale text are reported.

This exporter intentionally handles **MeshTextAsset content only**. Export UI and dialogue content with its owning system:

- Unity Localization CSV/XLIFF export;
- Dialogue System Localization Export/Import;
- I2 spreadsheet/CSV export;
- project-specific tooling.

A build pipeline can combine those exports into the same `Localization/en` directory without making MeshTextBaker own UI/dialogue localization.

## External folder format

```text
Game.exe
Localization/
  en/
    locale.json
    ui/main_menu.json
    notes/cabin_note.txt
    books/grimoire.txt
    images/seal.png
    fonts/book.ttf
  ru/
    locale.json
    ui/main_menu.json
    books/grimoire.txt
    images/seal.png
```

All files are UTF-8. Locale/resource paths reject absolute paths and `..` traversal.

### Optional locale metadata

```json
{
  "code": "ru",
  "displayName": "Русский",
  "fallback": "en",
  "author": "Fan Translation Team",
  "version": "1.0"
}
```

### Short strings

`Localization/ru/ui/main_menu.json`:

```json
{
  "play": "Играть",
  "continue": "Продолжить"
}
```

Keys:

```text
ui.main_menu.play
ui.main_menu.continue
```

JSON is flat; values must be strings. Standard escapes (`\n`, `\t`, `\uXXXX`) are supported. Root `strings.json` / `_all.json` uses entry keys verbatim.

### Long documents

```text
Localization/ru/books/grimoire.txt → books.grimoire
Localization/ru/notes/cabin.txt    → notes.cabin
```

TXT and MD are equivalent UTF-8 text. Long files are indexed and read/cached on first request. They may contain Markdown and all MeshTextBaker markup.

## Images and fonts

For locale `ru`, `<image=file:images/seal.png>` searches:

1. `Localization/ru/images/seal.png`
2. `GameRoot/images/seal.png`
3. previous persistentDataPath / StreamingAssets locations
4. Image Library alias `seal`

Alias tags also support file replacement without changing text:

```text
<image=seal> → locale/global images/seal.png → Image Library seal
<font=book>  → locale/global fonts/book.ttf → Font Library book
```

A mod may introduce a brand-new `file:` image/font even when no embedded original exists.

## Runtime behavior

External catalogs initialize once per requested locale. JSON is cached; long text loads lazily. There is no FileSystemWatcher or per-frame disk polling. Call `ReloadLocalization()` explicitly from a debug console if needed.

`ExternalOverrideLocalizationProvider` implements `ILocaleSelectionProvider`, exact-locale V2 lookup, locale discovery and change events so `MeshTextSurface` / `BookController` can refresh correctly.

## Using one build folder with Unity Localization + Dialogue System + MTB

A shared `Localization/` directory is an aggregation point, not one universal parser. Each owner exports and imports its own content:

```text
Game.exe
Localization/
  en/
    ui/                 # Unity Localization export chosen by the game
    dialogue/           # Dialogue System localization CSV
    books/              # MeshTextBaker export
    notes/              # MeshTextBaker export
    images/
    fonts/
  ru/
    ui/
    dialogue/
    books/
    notes/
    images/
    fonts/
```

Recommended build workflow:

1. Export keyed MeshTextAssets with **Tools → Mesh Text Baker → Localization Export**.
2. Export Unity String Tables through Unity Localization CSV/XLIFF tooling.
3. Export Dialogue System localization CSV through its Localization Export/Import foldout.
4. Copy all outputs under the build's `Localization/en` baseline.
5. A fan copies/creates `Localization/ru` and edits the relevant files.

Runtime ownership remains separate:

- `ExternalOverrideLocalizationProvider` loads MTB books/notes and their external images/fonts.
- Dialogue System loads its own localization CSV using its runtime localization importer.
- Unity Localization does not automatically consume arbitrary loose files beside the executable; the game needs a small project-level bootstrap/importer if UI String Tables must also be overridden from this folder.

Installing all three packages alone therefore does not automatically make every subsystem read the same loose files. The common folder works after the game wires each subsystem's importer in its bootstrap scene/build pipeline. Keeping that glue in the game (or optional Samples) prevents MeshTextBaker from becoming a competing localization framework.

## Notes inspired by established assets

Dialogue System and Unity Localization both emphasize explicit export/import workflows (usually CSV/XLIFF) and stable keys. MeshTextBaker follows the same boundary: it exports its own content and integrates with the project's chosen localization owner instead of claiming all game text.

---

*MeshTextBaker is free and open-source (MIT). Support development: [♥ Boosty](https://www.boosty.to/xiqima).*
