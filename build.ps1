param(
    [string]$Configuration = "Release",
    [string]$GameName = "GTFO",
    [string]$Profile = "Easy Mode DEV"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# DisplayName is the assembly/plugin-folder name (GTFO.EZ). It's kept separate from the
# Thunderstore manifest's "name" field, which is restricted to letters/numbers/underscores only
# and can't contain a period.
$displayName = "GTFO.EZ"
$manifest = Get-Content "$root\package\manifest.json" -Raw | ConvertFrom-Json
$packageName = $manifest.name
$version = $manifest.version_number

Write-Host "Building $displayName v$version (configuration: $Configuration, profile: $GameName - $Profile)..."
dotnet build "$root\src\GTFO.EZ\GTFO.EZ.csproj" -c $Configuration -p:GameName=$GameName -p:Profile=$Profile
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$bepInEx = Join-Path $env:AppData "Thunderstore Mod Manager\DataFolder\$GameName\profiles\$GameName - $Profile\BepInEx"
$pluginDir = Join-Path $bepInEx "plugins\$displayName"
$builtDll = Join-Path $pluginDir "$displayName.dll"
if (-not (Test-Path $builtDll)) { throw "Could not find built DLL at $builtDll. Check -Profile matches your mod manager profile." }

Get-ChildItem "$root\package" -Filter "*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force

Copy-Item $builtDll "$root\package\$displayName.dll" -Force

# GTFO.EZ.Core.dll (pure balance data/math, see src/GTFO.EZ.Core) is a real runtime dependency of
# GTFO.EZ.dll, not just a build-time reference - it has to ship in the same package or the mod
# throws a FileNotFoundException for every player the moment it loads.
Get-ChildItem $pluginDir -Filter "*.dll" | Where-Object { $_.Name -ne "$displayName.dll" } | ForEach-Object {
    Copy-Item $_.FullName "$root\package\$($_.Name)" -Force
}

$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$zipPath = Join-Path $distDir "$displayName-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path "$root\package\*" -DestinationPath $zipPath
Write-Host "Packaged: $zipPath (Thunderstore package name: $packageName)"
