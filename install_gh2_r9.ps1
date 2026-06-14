# install_gh2_r9.ps1 — install OpenNest for Grasshopper 2 into Rhino 9 (WIP).
#
# WHY this and not Yak: a Grasshopper 2 component library shipped in a Yak package is loaded in a
# separate plug-in context that cannot reach Rhino's UI assemblies (Eto, Grasshopper2) -> Rhino's
# plug-in manager throws "application initialization failed". Native GH2 libraries live in GH2's own
# Components folder, where they load in Grasshopper 2's context (Eto/Grasshopper2 already present).
# This script deploys OpenNest the same way (the McNeel GH2 plug-in template does the same).
#
# Run it (it self-elevates for the Program Files copy):
#     powershell -ExecutionPolicy Bypass -File install_gh2_r9.ps1

$ErrorActionPreference = 'Stop'

# Self-elevate (the GH2 Components folder is under Program Files).
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Host "Requesting administrator rights..."
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`""
    return
}

$repo = $PSScriptRoot
$gh2  = Join-Path $repo "src\opennest_gh2\bin\Release\net8.0-windows"
$gh1  = Join-Path $repo "src\opennest_2\bin\Release\net7.0-windows"

# Locate the Rhino 9 (WIP or release) net8 Grasshopper 2 Components folder.
$comp = Get-ChildItem "$env:ProgramFiles" -Directory -Filter "Rhino 9*" -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName "Plug-ins\Grasshopper2\net8.0\Components" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $comp) { throw "Rhino 9 Grasshopper 2 net8 Components folder not found under Program Files." }

$dst = Join-Path $comp "OpenNest"
New-Item -ItemType Directory -Force $dst | Out-Null

# Remove the Yak package if present, so Rhino's plug-in manager no longer scans it (that's the popup).
$yak = Get-ChildItem "$env:ProgramFiles" -Directory -Filter "Rhino 9*" -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName "System\yak.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($yak) { try { & $yak uninstall opennest_gh2 2>&1 | Out-Null; Write-Host "Removed any Yak OpenNest_GH2 package." } catch {} }

# Copy the GH2 plug-in + the GH1 payload it loads + the native engines into the Components subfolder.
foreach ($f in "opennest_gh2.rhp","opennest_gh2.deps.json","opennest_gh2.runtimeconfig.json") {
    $p = Join-Path $gh2 $f; if (Test-Path $p) { Copy-Item $p $dst -Force }
}
foreach ($f in "opennest_2.gha","nest_physics.dll","nfp_nest.dll") {
    $p = Join-Path $gh1 $f; if (Test-Path $p) { Copy-Item $p $dst -Force }
}

Write-Host ""
Write-Host "Installed OpenNest (Grasshopper 2) to:"
Write-Host "  $dst"
Get-ChildItem $dst | Select-Object Name | Format-Table -AutoSize
Write-Host "Restart Rhino 9 and open Grasshopper 2 - no plug-in popup."
