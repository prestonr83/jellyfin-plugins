param(
    [string]$Dotnet = 'dotnet',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$solution = Join-Path $projectRoot 'ParentalGuide.slnx'
$project = Join-Path $projectRoot 'Jellyfin.Plugin.ParentalGuide/Jellyfin.Plugin.ParentalGuide.csproj'
$metadata = Join-Path $projectRoot 'meta.json'
$version = (Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).version

if (-not $NoRestore) {
    & $Dotnet restore $solution --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
}

$assetFile = Join-Path $projectRoot 'Jellyfin.Plugin.ParentalGuide/obj/project.assets.json'
$assets = Get-Content -LiteralPath $assetFile -Raw | ConvertFrom-Json
if ($assets.libraries.PSObject.Properties.Name -notcontains 'Jellyfin.Controller/12.0.0') {
    throw 'Restore the default Jellyfin 12.0.0 baseline before packaging. A build against a newer API cannot declare the 12.0 ABI floor.'
}

& $Dotnet test $solution --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; package was not created.' }

& $Dotnet build $project --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

$outputRoot = Join-Path $projectRoot 'artifacts'
$packageDirectory = Join-Path $outputRoot ('Parental Guide_' + $version)
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'Jellyfin.Plugin.ParentalGuide/bin/Release/net10.0/Jellyfin.Plugin.ParentalGuide.dll') -Destination $packageDirectory -Force
Copy-Item -LiteralPath $metadata -Destination $packageDirectory -Force
$archive = Join-Path $outputRoot ('ParentalGuide_' + $version + '_jellyfin12.zip')
# Package only our assembly and descriptor; Jellyfin provides all framework/server assemblies.
Compress-Archive -LiteralPath (Join-Path $packageDirectory 'Jellyfin.Plugin.ParentalGuide.dll'), (Join-Path $packageDirectory 'meta.json') -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value ($checksum + '  ' + [IO.Path]::GetFileName($archive)) -Encoding ascii
Write-Output ('Package: ' + $archive)
Write-Output ('SHA256: ' + $checksum)
