# Floor Pattern Flattener

Revit ribbon add-in (Revit **2023–2027**) that adds a **permanent "flat" copy of a floor's surface pattern**,
as **plan-true real model lines**. The lines follow the floor's slope exactly but look
straight and undistorted in plan, the same pattern you would see in a plan view, and because they are real geometry they
**print, export and show in every view**.

## What it does

- **Custom Tools → Floor Patterns** ribbon panel:
  - **Flatten Floor Patterns**: flattens the selected floors (or asks you to pick some). A dialog asks for the
    style, **Lines** or **Surface**; the last choice is the default (saved in
    `%AppData%\FloorPatternFlattener\last-flatten-style.txt`). Running it on an already flattened floor switches
    it to the style you pick.
  - **Clear Flattened Patterns**: removes the flat pattern from the selected flattened floors (selecting a
    flat-pattern shape also works), or from **all** flattened floors in the document if none are selected (asks first).
  - **Refresh Flattened Patterns**: regenerates every flattened floor in its own style (Lines floors always,
    Surface floors only when out of date so a manual Align is kept), re-applies view overrides and
    removes orphaned shapes. Use it after an add-in upgrade, after the model was edited without the add-in,
    or when elements were skipped because of worksharing.
- **Permanent**: the flat pattern is saved in the RVT. Once a floor is flattened it stays flattened across
  save / close / reopen until you run **Clear**.
- **Auto-updating**: a Dynamic Model Update (IUpdater) regenerates the pattern when the floor changes
  (sketch, shape edits, slope, thickness, type, paint), when its floor type, material or
  material surface pattern changes, or when the fill pattern definition changes. Deleting the floor deletes its
  pattern. If someone deletes the pattern shape, it is recreated.
- **Live floor pattern stays visible**: the add-in no longer hides the floor's own surface pattern in any view
  (plans, ceiling plans, 3D, sections, and therefore sheets). Floors flattened by an older version had
  *Surface foreground/background pattern visible* switched off per view; **Flatten** or **Refresh** (and new
  views) switch it back on wherever both were off, leaving all other overrides alone. Because no element override
  is left on the floor, view filters can control the floor's pattern again. The old behaviour is still available
  via `FpfOptions.HideNativeSurfacePatterns = true`.
- **Sections / elevations / detail views**: the *Lines* pattern is zero-thickness lines on the top surface and
  the *Surface* is only 1.5 mm thick, so neither looks like a second floor. The generated shapes are also hidden in section, elevation and detail views by
  default (`FpfOptions.HideInSectionViews`).

## Surface style (removed)

The earlier *Surface* style was removed; only *Lines* is created. Floors that were flattened as Surface are
rebuilt as Lines the next time they are edited, updated or refreshed (the old `FPF Flat Pattern - Surface` shape is
deleted). **Flat Pattern Filters → Remove everywhere** also deletes the old "FPF - Flat Pattern Surface" filter.

## How it works

1. **Top faces.** The floor's solid geometry is scanned for every upward-facing face (`normal.Z > 0.05`), so each
   slope of a multi-slope (shape-edited) floor counts. Each face's edge loops (outer boundary + openings) are
   tessellated and projected to XY.
2. **Pattern.** The material shown on each face is resolved in this order: painted material, then
   `Face.MaterialElementId`, then the type's top compound layer (layer 0 down to the first core layer, first
   valid material), then the Floors category material. **Both** its surface **background** and **foreground**
   patterns are flattened (none / solid fills are skipped), each with its own pattern colour, background drawn
   first. **All `FillGrid`s** are read, honouring
   Angle, Origin, Offset, Shift and dash segments (`GetSegments`, dots become 1.5 mm dashes).
   - Model patterns are used at true size.
   - Drafting patterns (paper-space) are converted as if seen at **1:100** (`FpfOptions.DraftingPatternScale`)
     and listed in the report.
   - No pattern or solid fill: a **default 300 mm orthogonal grid** is used and listed in the report.
3. **Plan hatch, global alignment.** Hatch lines are generated **in plan**, aligned to the **project internal
   origin**, so patterns line up across neighbouring floors (the same way Revit aligns model patterns by default).
4. **Clip + lift.** Lines are clipped to each face's XY footprint with the even-odd rule (openings stay
   open), split into dashes, then each endpoint is **projected vertically onto that face's plane**
   (z from the plane equation) and offset **0.5 mm along the face normal** (`FpfOptions.SurfaceOffsetFeet`) to
   avoid z-fighting. A straight line projected onto a plane stays straight, so it reads as a plan pattern
   while lying exactly on the slope. Segments shorter than Revit's `ShortCurveTolerance` are skipped.
   - **Non-planar top faces** (rare for floors): best effort. The face is triangulated and every upward
     triangle is treated as a small planar face. Lines stay continuous across triangles but are split at
     triangle edges. The report shows the count.
5. **Real geometry.** The lines are stored in pinned **DirectShapes** (category **Generic Models**) named
   `Flat Pattern - Floor <id> Foreground|Background`, `Comments = "FPF Flat Pattern"`, with the floor's workset
   and phases (created/demolished). Each layer gets its own shapes, its own type (`FPF Flat Pattern - Foreground`
   / `FPF Flat Pattern - Background`, also written to *Type Comments*) and its lines are drawn on a Generic Models
   subcategory `FPF Pattern Foreground` / `FPF Pattern Background` (plus ` RGB r-g-b` when the material's pattern
   colour is not black; the subcategory's line colour is set to that colour when it is created).
   If a layer has more than 25,000 segments it is split across several DirectShapes.
   The total is capped at **100,000 segments per floor**: past that the hatch is truncated and a warning is shown.
6. **Links (extensible storage).** Fixed-GUID schemas, vendor `TWISTOFFATE`, **public read/write** (files open
   cleanly on machines without the add-in; the data just sits there):
   - on the floor: `Flattened`, DirectShape UniqueIds, a SHA-256 **signature** of all inputs (top-face
     geometry, materials, pattern definitions, phases, workset, algorithm version), dependency UniqueIds, version;
   - on each DirectShape: source floor UniqueId, chunk index, version.
7. **Updater.** Registered in `OnStartup` as **optional** (opening a model without the add-in does not show the
   "missing third-party updater" warning). Triggers: floors (geometry, any change, addition, deletion), floor
   types, materials, fill patterns, **view addition**, DirectShape deletion. Loop safety: changes to
   DirectShapes are not triggers, and writing the floor's storage re-triggers the floor but the stored signature
   matches, so nothing happens. Existing DirectShapes are updated in place (`SetShape`), so element ids,
   section hiding and any per-view overrides you add stay put.

### Why Generic Models?

`DirectShape.IsValidCategoryId` accepts it in every version, it prints and exports like any model element,
and it keeps the lines out of **Floor schedules, areas and quantity take-offs** (using the Floors category would
double-count floors). Style the lines with a view filter (below).

## Styling the lines (colour / weight / pattern)

In the *Lines* style the flattened pattern is made of lines, so in V/G and filters it is driven by **Projection
Lines** (colour, weight, line pattern) and visibility; *Surface Patterns* overrides have nothing to act on. For
filters that act on the pattern itself, use the *Surface* style: rule **Type Comments equals
`FPF Flat Pattern - Surface`** (or the Comments rule) with a **Surface Patterns** override.

- **Both layers**: view filter, **Categories = Generic Models**, rule **Comments equals `FPF Flat Pattern`**.
- **One layer**: rule **Type Comments equals `FPF Flat Pattern - Foreground`** (or `- Background`), or
  *Type Name* where your Revit version offers it in filter rules.
- **Default colours / weights**: *Object Styles → Model Objects → Generic Models → FPF Pattern …* subcategories,
  or per view in V/G. Filters override these.

The same filters can hide the flat patterns in specific views.

## Supported Revit versions

| Year | Target framework | Notes |
|------|------------------|-------|
| 2023 | `net48` | .NET Framework 4.8 |
| 2024 | `net48` | .NET Framework 4.8 |
| 2025 | `net8.0-windows` | Revit 2025 moved to .NET 8 |
| 2026 | `net8.0-windows` | .NET 8 for 2026.0–2026.4 |
| 2027 | `net10.0-windows` | Revit 2027 targets .NET 10 |

> **Mid-cycle runtime note:** Autodesk has announced .NET 10 updates for some 2025.5 / 2026.5 builds. If you are
> on those and `net8.0-windows` fails to load, retarget that year's project to `net10.0-windows` and rebuild.

## Solution layout

```
FloorPatternFlattener/
  FloorPatternFlattener.sln
  Directory.Build.props              # Revit20XXPath defaults; imports Directory.Build.props.user if present
  src/
    FloorPatternFlattener.Core/      # shared C#, linked into each year project
      App.cs                         # ribbon, updater registration, stale check on open
      FpfOptions.cs                  # all tunables (offset, caps, drafting scale, section hiding...)
      Commands/                      # Flatten, Clear, Refresh (+ CommandSupport)
      Geometry/                      # PolygonClipper (even-odd + dashes), PatternSource, FloorHatchBuilder
      Services/                      # FlatPatternService, ViewGraphics, Editability, FpfReport
      Storage/FpfStorage.cs          # extensible storage schemas
      Updater/FlatPatternUpdater.cs  # IUpdater
      Ribbon/RibbonBuilder.cs
    FloorPatternFlattener.2023 … 2027/
  addin/FloorPatternFlattener.addin
  install/Install-Addin.ps1
```

## Build (Windows + Visual Studio 2022)

Revit API DLLs are **not** in this repository. Install Revit for each year you need; the projects reference
`C:\Program Files\Autodesk\Revit 20XX\RevitAPI.dll` / `RevitAPIUI.dll`. Copy
`Directory.Build.props.user.example` to `Directory.Build.props.user` to change paths.

```powershell
cd FloorPatternFlattener
dotnet build .\src\FloorPatternFlattener.2024\FloorPatternFlattener.2024.csproj -c Release -p:Platform=x64
dotnet build .\src\FloorPatternFlattener.2025\FloorPatternFlattener.2025.csproj -c Release -p:Platform=x64
```

## Install

```powershell
cd FloorPatternFlattener\install
.\Install-Addin.ps1 -Configuration Release            # all built years
.\Install-Addin.ps1 -Years 2025 -CopyDll               # one year, DLL copied beside the .addin
```

This writes `%AppData%\Autodesk\Revit\Addins\20XX\FloorPatternFlattener.addin`. Restart Revit.

## Usage

1. Select floors, then **Custom Tools → Floor Patterns → Flatten Floor Patterns**. A report lists what was
   created, anything that fell back to the default grid or drafting scale, and anything skipped.
2. Edit floors as usual. The pattern follows automatically.
3. **Clear Flattened Patterns** to go back to the native pattern. Every command is a single named, undoable
   transaction (Ctrl+Z works).

## Pattern placement, preview, scope, edit

**Flatten Floor Patterns** opens a dialog:

- **Angle** (degrees, anticlockwise), **Align to Edge...** (pick a straight model edge: slab edge, kerb, wall) and **Align to Line...** (pick a model/detail line or grid). The pattern turns to that direction and starts at the edge end nearest your click instead of the project origin. **Clear** goes back to 0 deg with no anchor, which is the exact match with the live floor pattern (the default).
- **Apply to**: only the selected floor(s), or all visible floors of the same type(s) in the active view.
- **Preview** builds the result for real inside a transaction group and shows it in the view. **Apply** keeps it as one undo step. **Discard** rolls everything back, so the model is untouched. Lines mode shows the line count and asks for confirmation when a single floor would get more than 10,000 lines.

Each floor stores its style, angle and anchor, so **Refresh**, **Update** and the automatic rebuild reuse them. Floors with no rotation and no anchor behave exactly as before and are not rebuilt by the upgrade.

**Edit Flattened Pattern** re-opens a flattened floor (select it or its flat pattern, or pick one) with its stored settings loaded. Preview, then Apply to replace the flat pattern, or Discard to keep the old one.

Notes:
- Lines are rotated and anchored exactly.
- Surface uses rotated copies of the fill patterns, named "FPF <pattern> [R30 #...]", on its own material. The anchor applies to model patterns only (drafting patterns are rotated only), and alignment is best effort.
- The edge or line must be in the current model, not in a link.
- The preview is a temporary model change shown while the dialog is open. It is not a moving on-screen overlay.

## Auto update and Update

- **Auto: On / Auto: Off** turns the automatic rebuild on or off. The setting is remembered between sessions in `%AppData%\FloorPatternFlattener\auto-update.txt`.
- When Auto is Off, nothing is rebuilt until you press **Update Flattened**. Update rebuilds the selected flattened floors, one picked floor, or all of them. It shows progress with Cancel (floors already done are kept) and a summary, all as one undo step.

## Filters and the sheet (host) file

The flat patterns are controlled only by element data: Comments = "FPF Flat Pattern" on every shape, the types "FPF Flat Pattern - Foreground" / "- Background" / "- Surface" (their Type Comments carry the same text) and the "FPF Pattern ..." subcategories. Nothing relies on view overrides inside the model file, and the live floor pattern is never hidden.

**Flat Pattern Filters** creates or updates the Generic Model filters "FPF - Flat Pattern" (Comments), "FPF - Flat Pattern Foreground", "FPF - Flat Pattern Background" and "FPF - Flat Pattern Surface" (Type Comments). It can add them to all view templates or to the active view, or remove them everywhere (the filters are deleted). They are added with no overrides; set lines, patterns, halftone or visibility in Visibility/Graphics > Filters.

To control everything from the sheet file that links the models:
1. Open the **sheet file** and run **Flat Pattern Filters** there (the add-in only needs to be loaded; nothing is flattened in that file).
2. In Visibility/Graphics > Revit Links, set each linked model to **By Host View** (or Custom with filters By Host View), so the host filters apply to the linked flat patterns.
3. Style the FPF filters in your view templates. Alternatively use Object Styles > Generic Models > the "FPF Pattern ..." subcategories, which also come through the link.
4. Sections: the model file hides the shapes in its own section views, but that does not carry through a link. In the sheet file, turn off "FPF - Flat Pattern" in section templates.

## Worksharing

- Commands check out (borrow) the floors and their pattern shapes first. Anything owned by someone else, or
  out of date with central, is **skipped and listed** in the final dialog. Reload Latest / request the elements,
  then run **Refresh**.
- Views are not borrowed in bulk. Views you cannot edit are skipped and counted. Views you can edit are borrowed
  by Revit when an override is set, until you synchronise.
- The updater never edits elements you cannot edit. It skips them and posts a Revit warning instead.
  Changing a shared material or fill pattern may borrow flattened floors that use it.

## Limitations / notes

- **Element count**: very large floors with fine patterns create many lines (split across several DirectShapes,
  capped at 100,000 segments per floor, then truncated with a warning). Heavy hatches slow views, so consider a
  coarser pattern.
- **Perspective 3D views** still foreshorten with distance. The pattern is flat on the surface, not screen-aligned.
- **Without the add-in** other users still see (and print) the flat pattern lines,
  but the pattern does **not** update when they edit floors. When the model is next opened with the add-in, you are
  told how many floors are out of date. Run **Refresh**.
- The generated lines are Generic Models: hiding the **Floors** category, or hiding or isolating a single floor in a
  view, does not hide its pattern. Use the Comments filter.
- *Lines* pattern alignment follows the project internal origin; *Surface* alignment is Revit's own (see
  [Lines or Surface](#lines-or-surface)). Pattern alignment changed manually in Revit (Align /
  surface pattern dragging) is not read.
- Floors inside **groups**, **design options** or **linked models** are not specially handled. The DirectShape is
  created in the host model outside any group or option.
- Upward faces hidden under other parts of the same floor (unusual shape edits) are also hatched.
- Revit applies the same view-range/visibility rules to the lines as to any Generic Model. They show in plan when the
  floor top is inside the view range.

## Uninstall / cleanup

Run **Clear Flattened Patterns** with nothing selected in every model (removes all shapes, storage data and
overrides), then delete any `FPF Flat Pattern - …` materials you no longer want (Clear leaves them; they are
harmless), then delete `FloorPatternFlattener.addin` from `%AppData%\Autodesk\Revit\Addins\20XX\`. If the add-in is
already gone, the flat-pattern shapes can be selected with the Comments filter and deleted by hand. The
native pattern can then be turned back on per view via *Override Graphics in View → By Element → Surface Patterns*.

## License

MIT, see [LICENSE](LICENSE).

## Disclaimer

Provided as-is, not an Autodesk product. Validate visual output and print sets before issuing drawings.
