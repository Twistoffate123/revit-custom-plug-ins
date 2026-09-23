# Floor Pattern Flattener

Revit ribbon add-in (Revit **2023–2027**) that makes floor finish patterns appear **plan-flat** in 3D views — and on sheets that place those 3D views — **without** creating a separate Floor, filled regions, or exported flatten files.

## Problem

In 3D views, model surface patterns on Floors are drawn on the actual geometry. When the camera is perspective or the floor thickness/edges are visible, hatch lines look skewed or “standing up” with the slab. Designers often want the finish hatch to read like a **plan pattern sitting flat** on the floor top, especially when a 3D view is placed on a sheet.

## What this plugin does

1. Adds a **Custom Tools** ribbon tab with panel **Floor Patterns**:
   - **Flatten Floor Patterns**
   - **Clear Flattened Patterns**
2. On Flatten, selected `Floor` elements are stored in a per-document session store.
3. An `IDirectContext3DServer` draws hatch **line segments in world space** on a **horizontal plane** at the floor’s top elevation, clipped to the floor’s **plan outline** (XY projection of the top face boundary).
4. Best-effort: hides native model surface patterns on those floors in the active view (`OverrideGraphicSettings` surface pattern visibility) so you do not see double hatch.
5. Clear removes floors from the store, restores overrides when possible, and refreshes the view.

### What it does **not** do

- Does **not** create or duplicate Floor elements.
- Does **not** require filled regions or DWG/CAD flatten exports.
- Does **not** permanently rewrite floor geometry or materials.
- Is **not** an Autodesk-certified / Autodesk-supported product — it is a community-style add-in using public Revit API surfaces (`DirectContext3D`).

## Supported Revit versions

| Year | Target framework | Notes |
|------|------------------|--------|
| 2023 | `net48` | .NET Framework 4.8 |
| 2024 | `net48` | .NET Framework 4.8 |
| 2025 | `net8.0-windows` | Revit 2025 moved to .NET 8 |
| 2026 | `net8.0-windows` | .NET 8 for 2026.0–2026.4 |
| 2027 | `net10.0-windows` | Revit 2027 targets .NET 10 |

> **Mid-cycle runtime note:** Autodesk has announced .NET 10 updates for some 2025.5 / 2026.5 builds. If you are on those updates and `net8.0-windows` fails to load, retarget the matching year project to `net10.0-windows` and rebuild. This repo defaults 2025/2026 to `net8.0-windows` and 2027 to `net10.0-windows`.

## Solution layout

```
revit-custom-plug-ins/
  README.md
  LICENSE                 (MIT)
  .gitignore
  FloorPatternFlattener/
    FloorPatternFlattener.sln
    Directory.Build.props
    Directory.Build.props.user.example
    src/
      FloorPatternFlattener.Core/     # shared C# (linked into each year project)
        App.cs
        Commands/
        Geometry/
        Overlay/
        Ribbon/
        Session/
      FloorPatternFlattener.2023/
      FloorPatternFlattener.2024/
      FloorPatternFlattener.2025/
      FloorPatternFlattener.2026/
      FloorPatternFlattener.2027/
    addin/
      FloorPatternFlattener.addin
    install/
      Install-Addin.ps1
```

Shared logic lives under `FloorPatternFlattener.Core` and is **compiled into each year-specific DLL** via linked sources (one assembly name: `FloorPatternFlattener.dll` per year output).

### Key types

| Type | Role |
|------|------|
| `App` | `IExternalApplication` — ribbon + DirectContext3D registration |
| `CommandFlattenFloorPatterns` | Select floors → session store → optional hide native patterns |
| `CommandClearFlattenedPatterns` | Remove overlays / restore overrides |
| `FloorPatternOverlayServer` | `IDirectContext3DServer` — `CanExecute` for `View3D`, `RenderScene` line buffers |
| `FlattenSession` | `Document` hash → `HashSet<ElementId>` |
| `FloorHatchBuilder` | Top outline + hatch segments (0° / 90° or material angle) |
| `PolygonClipper` | Segment vs polygon clip for hatch |
| `RibbonBuilder` | Creates **Custom Tools** tab / panel / buttons |
| `FloorPatternVisibility` | Best-effort surface pattern invisibility overrides |

## Build (Windows + Visual Studio 2022)

Revit API DLLs are **not** in this repository. Install Revit (or the Revit SDK) for each year you need, then point HintPaths at:

```text
C:\Program Files\Autodesk\Revit 20XX\RevitAPI.dll
C:\Program Files\Autodesk\Revit 20XX\RevitAPIUI.dll
```

### Steps

1. Install **Visual Studio 2022** (17.8+ for .NET 8; newer for .NET 10) with workloads:
   - .NET desktop development
   - .NET Framework 4.8 targeting pack (for 2023/2024)
2. Clone this repo.
3. Optional: copy `FloorPatternFlattener/Directory.Build.props.user.example` → `Directory.Build.props.user` and edit `Revit20XXPath` if your install is non-default.
4. Open `FloorPatternFlattener/FloorPatternFlattener.sln`.
5. Select configuration **Release | x64** (or Debug).
6. Build the year project(s) you need. Example CLI:

```powershell
cd FloorPatternFlattener
dotnet build .\src\FloorPatternFlattener.2024\FloorPatternFlattener.2024.csproj -c Release -p:Platform=x64
dotnet build .\src\FloorPatternFlattener.2025\FloorPatternFlattener.2025.csproj -c Release -p:Platform=x64
```

`CopyLocal` is `false` for Revit references — Autodesk DLLs are never copied into `bin`.

## Install

### Option A — script

```powershell
cd FloorPatternFlattener\install
.\Install-Addin.ps1 -Configuration Release
# or copy DLL beside the .addin:
.\Install-Addin.ps1 -Configuration Release -CopyDll
# single year:
.\Install-Addin.ps1 -Years 2025 -CopyDll
```

This writes:

```text
%AppData%\Autodesk\Revit\Addins\20XX\FloorPatternFlattener.addin
```

### Option B — manual

1. Build the year project.
2. Copy `addin/FloorPatternFlattener.addin` to `%AppData%\Autodesk\Revit\Addins\20XX\`.
3. Edit `<Assembly>` to the **full path** of that year’s `FloorPatternFlattener.dll`
   (under `src\FloorPatternFlattener.20XX\bin\Release\<tfm>\`).
4. Restart Revit.

## Usage

1. Open a model in a supported Revit year.
2. Select one or more **Floor** elements (or run the command and pick when prompted).
3. **Custom Tools → Floor Patterns → Flatten Floor Patterns**.
4. Open / refresh a **3D view** — hatch should appear plan-flat on the floor top.
5. Place that 3D view on a sheet; when DirectContext3D is available for sheet printing/display, the overlay participates with that view.
6. **Clear Flattened Patterns** removes the overlay (selection-limited if flattened floors are selected; otherwise all for the document).

Plan views already show native patterns flat; this tool targets **3D** (and sheets of those 3D views).

## Pattern source

- Prefers the floor type’s top-layer **material** surface fill pattern (`FillPatternElement` / `FillGrid` offset & angle) when readable.
- Otherwise defaults to a **~300 mm** (internal: converted to feet) **orthogonal grid** (0° and 90°) in dark gray.
- Color uses material surface pattern color when obtainable.

## Limitations

- Overlay is **DirectContext3D** graphics, not native Revit category graphics. Print / export / PDF / DWG fidelity can vary by Revit year and print pipeline; always verify deliverables.
- Complex **multi-slope** floors: the builder picks the highest upward planar face (or averages edge Z if not horizontal). Extremely faceted tops may need future refinement.
- Session store is **in-memory** (per Revit session / document hash) — not saved with the RVT. Re-run Flatten after reopen if needed.
- Hiding native surface patterns depends on `OverrideGraphicSettings.SetSurfaceForegroundPatternVisible` / background equivalents; behavior is best-effort across years.
- Not a substitute for official Autodesk API certification or Autodesk support.
- Build and run on **Windows** with matching Revit installed; this Linux workspace only holds sources.

## Development notes

- Hatch math (`FloorHatchBuilder` + `PolygonClipper`) is complete and unit-agnostic (Revit internal feet).
- `RenderScene` builds `VertexBuffer` / `IndexBuffer` / `EffectInstance` `LineList` primitives via DirectContext3D.
- Register once in `App.OnStartup`; commands call `EnsureRegistered` as a fallback.

## License

MIT — see [LICENSE](LICENSE).

## Disclaimer

This software is provided as-is. Use in production models at your own risk. Validate visual output and print sets before issuing drawings.
