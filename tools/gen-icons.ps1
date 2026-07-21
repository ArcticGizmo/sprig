#!/usr/bin/env pwsh
# Regenerates every icon asset (and the in-app vector data) from the source-of-truth SVG (sprig.svg).
#
#   src/Sprig.App/Assets/sprig.png     256x256 PNG    (general/window icon)
#   src/Sprig.App/Assets/sprig.ico     multi-res ICO  (window + .exe ApplicationIcon)
#   landing-icon.png                    512x512 PNG    (README header)
#   src/Sprig.App/Icons/SprigLogo.g.cs  vector data for the in-app logo (drawn via Skia)
#
# The rasters are produced by tools/IconGen (renders the SVG via System.Drawing, Windows-only).
#
# Run this after editing sprig.svg, then commit the regenerated assets.

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'IconGen'
dotnet run --project $proj -c Release
