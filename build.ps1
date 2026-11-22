<#
.SYNOPSIS
    Builds the Up Next plugin for Jellyfin 10.11 and 12, zips both, and adds them to manifest.json.

.DESCRIPTION
    Each Jellyfin version needs its own build (10.11 runs on .NET 9, 12 on .NET 10).
    Both come from the same code and the same release:

      Jellyfin 10.11  ->  version 1.5.0.0   up-next_1.5.0.0.zip    targetAbi 10.11.0.0
      Jellyfin 12     ->  version 1.5.0.12  up-next_1.5.0.12.zip   targetAbi 12.0.0.0

    Jellyfin offers any version whose targetAbi isn't newer than the server, newest
    version first. The ".12" ending makes the 12 build the newer one, so 12 servers pick
    it, while 10.11 servers never see it.

.EXAMPLE
    ./build.ps1 -Version 1.5.0.0 -Changelog "What changed"

    Then publish it:
    gh release create v1.5.0.0 dist/up-next_1.5.0.0.zip dist/up-next_1.5.0.12.zip --title "Up Next 1.5.0.0" --notes "What changed"
    git add manifest.json; git commit -m "Release 1.5.0.0"; git push
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Changelog = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "Jellyfin.Plugin.UpNext/Jellyfin.Plugin.UpNext.csproj"
$repo = "PraiseGodForAllEternity/jellyfin-plugin-upnext"

$parts = $Version.Split(".")
if ($parts.Count -ne 4 -or $parts[3] -ne "0") { throw "Use a version like 1.5.0.0 (the last part is used for the Jellyfin 12 build)." }
$base = ($parts[0..2] -join ".")

$targets = @(
    @{ Jellyfin = "10.11"; Version = "$base.0";  TargetAbi = "10.11.0.0" },
    @{ Jellyfin = "12";    Version = "$base.12"; TargetAbi = "12.0.0.0" }
)

New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null
$entries = @()
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

foreach ($t in $targets) {
    $out = Join-Path $root "dist/build-$($t.Jellyfin)"
    $zip = Join-Path $root "dist/up-next_$($t.Version).zip"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    dotnet publish $project -c Release -o $out "-p:JellyfinTarget=$($t.Jellyfin)" `
        "-p:Version=$($t.Version)" "-p:AssemblyVersion=$($t.Version)" "-p:FileVersion=$($t.Version)"
    if ($LASTEXITCODE -ne 0) { throw "Build for Jellyfin $($t.Jellyfin) failed" }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "Jellyfin.Plugin.UpNext.dll") -DestinationPath $zip
    $md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

    $entries += [pscustomobject]@{
        version   = $t.Version
        changelog = $Changelog
        targetAbi = $t.TargetAbi
        sourceUrl = "https://github.com/$repo/releases/download/v$Version/up-next_$($t.Version).zip"
        checksum  = $md5
        timestamp = $timestamp
    }
    Write-Host "Built $zip for Jellyfin $($t.Jellyfin) (MD5 $md5)"
}

$manifestPath = Join-Path $root "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$plugin = @($manifest)[0]

# Newest first (the 12 build ahead of the 10.11 one), replacing entries with the same versions.
$newVersions = $entries | ForEach-Object { $_.version }
$plugin.versions = @($entries | Sort-Object { [version]$_.version } -Descending) +
    @($plugin.versions | Where-Object { $newVersions -notcontains $_.version })

$json = ConvertTo-Json @($plugin) -Depth 10
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "manifest.json updated"
