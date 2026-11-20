<#
.SYNOPSIS
    Builds the Up Next plugin, zips it, and adds the version to manifest.json.

.EXAMPLE
    ./build.ps1 -Version 1.0.0.0 -Changelog "First release"

    Then publish it:
    gh release create v1.0.0.0 dist/up-next_1.0.0.0.zip --title "Up Next 1.0.0.0" --notes "First release"
    git add manifest.json; git commit -m "Release 1.0.0.0"; git push
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Changelog = "",
    [string]$TargetAbi = "10.11.0.0"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "Jellyfin.Plugin.UpNext/Jellyfin.Plugin.UpNext.csproj"
$out = Join-Path $root "dist/build"
$zip = Join-Path $root "dist/up-next_$Version.zip"
$repo = "PraiseGodForAllEternity/jellyfin-plugin-upnext"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $root "dist") | Out-Null

dotnet publish $project -c Release -o $out -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "Jellyfin.Plugin.UpNext.dll") -DestinationPath $zip

$md5 = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()

$manifestPath = Join-Path $root "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$plugin = @($manifest)[0]

$entry = [pscustomobject]@{
    version   = $Version
    changelog = $Changelog
    targetAbi = $TargetAbi
    sourceUrl = "https://github.com/$repo/releases/download/v$Version/up-next_$Version.zip"
    checksum  = $md5
    timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
}

# Newest first, replacing an existing entry with the same version.
$plugin.versions = @($entry) + @($plugin.versions | Where-Object { $_.version -ne $Version })

$json = ConvertTo-Json @($plugin) -Depth 10
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Built $zip"
Write-Host "MD5 $md5"
