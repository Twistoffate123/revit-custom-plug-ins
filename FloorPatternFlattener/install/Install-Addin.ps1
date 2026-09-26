<#
.SYNOPSIS
  Copies FloorPatternFlattener .addin + built DLL into per-year Revit Addins folders.

.DESCRIPTION
  For each Revit year 2023-2027 that has a built DLL, writes:
    %AppData%\Autodesk\Revit\Addins\<year>\FloorPatternFlattener.addin
  with Assembly pointing at the built DLL (left in the build output folder),
  OR copies the DLL next to the .addin when -CopyDll is specified.

.PARAMETER Configuration
  Debug or Release (default: Release)

.PARAMETER Years
  Optional subset, e.g. -Years 2024,2025

.PARAMETER CopyDll
  If set, copies FloorPatternFlattener.dll into the Addins year folder and points
  the .addin Assembly at that local copy.

.EXAMPLE
  .\Install-Addin.ps1 -Configuration Release
  .\Install-Addin.ps1 -Years 2025 -CopyDll
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [int[]] $Years = @(2023, 2024, 2025, 2026, 2027),

    [switch] $CopyDll
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$AddinTemplate = Join-Path $Root "addin\FloorPatternFlattener.addin"

if (-not (Test-Path $AddinTemplate)) {
    throw "Missing add-in template: $AddinTemplate"
}

$TfmMap = @{
    2023 = "net48"
    2024 = "net48"
    2025 = "net8.0-windows"
    2026 = "net8.0-windows"
    2027 = "net10.0-windows"
}

$AppDataAddins = Join-Path $env:AppData "Autodesk\Revit\Addins"
$installed = @()
$skipped = @()

foreach ($year in $Years) {
    $tfm = $TfmMap[$year]
    if (-not $tfm) {
        $skipped += "Year ${year}: unknown TFM"
        continue
    }

    # SDK projects put output under bin\x64\<cfg>\<tfm> when built with -p:Platform=x64, else bin\<cfg>\<tfm>.
    $projDir = Join-Path $Root "src\FloorPatternFlattener.$year"
    $candidates = @(
        (Join-Path $projDir "bin\x64\$Configuration\$tfm\FloorPatternFlattener.dll"),
        (Join-Path $projDir "bin\$Configuration\$tfm\FloorPatternFlattener.dll")
    )
    $projOut = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $projOut) {
        $skipped += "Year ${year}: DLL not found at $($candidates -join ' or ') (build the project first)"
        continue
    }

    $destDir = Join-Path $AppDataAddins "$year"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    if ($CopyDll) {
        $dllDest = Join-Path $destDir "FloorPatternFlattener.dll"
        Copy-Item -Force -Path $projOut $dllDest
        # Copy PDB if present
        $pdb = [IO.Path]::ChangeExtension($projOut, ".pdb")
        if (Test-Path $pdb) {
            Copy-Item -Force -Path $pdb (Join-Path $destDir "FloorPatternFlattener.pdb")
        }
        $assemblyPath = $dllDest
    }
    else {
        $assemblyPath = (Resolve-Path $projOut).Path
    }

    $xml = Get-Content -Raw -Path $AddinTemplate
    $xml = [regex]::Replace($xml, "<Assembly>.*?</Assembly>", "<Assembly>$assemblyPath</Assembly>")
    $addinDest = Join-Path $destDir "FloorPatternFlattener.addin"
    Set-Content -Path $addinDest -Value $xml -Encoding UTF8

    $installed += "Revit $year -> $addinDest (Assembly=$assemblyPath)"
}

Write-Host ""
Write-Host "Installed:" -ForegroundColor Green
$installed | ForEach-Object { Write-Host "  $_" }
if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "Skipped:" -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "  $_" }
}
Write-Host ""
Write-Host "Restart Revit to load the add-in." -ForegroundColor Cyan
