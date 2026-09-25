# Floor Pattern Flattener

Revit ribbon add-in (Revit **2023–2027**) that replaces a floor's native surface pattern with a **permanent,
plan-true "flat" pattern made of real model lines**. The lines follow the floor's slope exactly but look
straight and undistorted in plan, the same pattern you would see in a plan view, and because they are real geometry they
**print, export and show in every view**.

## What it does

- **Custom Tools → Floor Patterns** ribbon panel:
  - **Flatten Floor Patterns**: flattens the selected floors (or asks you to pick some).
  - **Clear Flattened Patterns**: removes the flat pattern from the selected flattened floors (selecting a
    flat-pattern shape also works), or from **all** flattened floors in the document if none are selected (asks first).
  - **Refresh Flattened Patterns**: force-regenerates every flattened floor, re-applies view overrides and
    removes orphaned shapes. Use it after an add-in upgrade, after the model was edited without the add-in,
    or when elements were skipped because of worksharing.
- **Permanent**: the flat pattern is saved in the RVT. Once a floor is flattened it stays flattened across
  save / close / reopen until you run **Clear**.
- **Auto-updating**: a Dynamic Model Update (IUpdater) regenerates the pattern when the floor changes
  (sketch, shape edits, slope, thickness, type, paint), when its floor type, material or
  material surface pattern changes, or when the fill pattern definition changes. Deleting the floor deletes its
  pattern. If someone deletes the pattern shape, it is recreated.
- **Native pattern hidden, nothing else touched**: in every non-template view that allows overrides (plans,
  ceiling plans, 3D, area plans, sections, elevations) only *Surface foreground/background pattern visible* is
  switched off for the flattened floor. All other overrides are kept. **Views created later get the same
  override automatically.** Clear turns only these two switches back on.
- **Sections / elevations / detail views**: the pattern is zero-thickness lines on the top surface, so it never
  looks like a second floor. The generated shapes are also hidden in section, elevation and detail views by
  default (`FpfOptions.HideInSectionViews`).

## How it works

1. **Top faces.** The floor's solid geometry is scanned for every upward-facing face (`normal.Z > 0.05`), so each
   slope of a multi-slope (shape-edited) floor counts. Each face's edge loops (outer boundary + openings) are
   tessellated and projected to XY.
2. **Pattern.** The material shown on each face is resolved in this order: painted material, then
   `Face.MaterialElementId`, then the type's top compound layer (layer 0 down to the first core layer, first
   valid material), then the Floors category material. Its **surface foreground** pattern is used (the
   background pattern if the foreground is none or solid). **All `FillGrid`s** are read, honouring
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
5. **Real geometry.** The lines are stored in a pinned **DirectShape** (category **Generic Models**) named
   `Flat Pattern - Floor <id>`, `Comments = "FPF Flat Pattern"`, with the floor's workset and phases
   (created/demolished). If a hatch has more than 25,000 segments it is split across several DirectShapes.
   The total is capped at **100,000 segments per floor**: past that the hatch is truncated and a warning is shown.
6. **Links (extensible storage).** Fixed-GUID schemas, vendor `FPF`, **public read/write** (files open
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

Create a view filter: **Categories = Generic Models**, rule **Comments equals `FPF Flat Pattern`**, then
override *Projection Lines* in V/G (or in a view template). The same filter can hide the flat patterns in
specific views.

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
- **Without the add-in** other users still see (and print) the flat pattern lines and the hidden native pattern,
  but the pattern does **not** update when they edit floors. When the model is next opened with the add-in, you are
  told how many floors are out of date. Run **Refresh**.
- The generated lines are Generic Models: hiding the **Floors** category, or hiding or isolating a single floor in a
  view, does not hide its pattern. Use the Comments filter.
- Pattern alignment follows the project internal origin. Pattern alignment changed manually in Revit (Align /
  surface pattern dragging) is not read.
- Floors inside **groups**, **design options** or **linked models** are not specially handled. The DirectShape is
  created in the host model outside any group or option.
- Upward faces hidden under other parts of the same floor (unusual shape edits) are also hatched.
- Revit applies the same view-range/visibility rules to the lines as to any Generic Model. They show in plan when the
  floor top is inside the view range.

## Uninstall / cleanup

Run **Clear Flattened Patterns** with nothing selected in every model (removes all shapes, storage data and
overrides), then delete `FloorPatternFlattener.addin` from `%AppData%\Autodesk\Revit\Addins\20XX\`. If the add-in is
already gone, the flat-pattern shapes can be selected with the Comments filter and deleted by hand. The
native pattern can then be turned back on per view via *Override Graphics in View → By Element → Surface Patterns*.

## License

MIT, see [LICENSE](LICENSE).

## Disclaimer

Provided as-is, not an Autodesk product. Validate visual output and print sets before issuing drawings.
