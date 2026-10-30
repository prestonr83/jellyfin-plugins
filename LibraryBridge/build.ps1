param(
    [string]$Dotnet = 'dotnet',
    [string]$PackagesPath,
    [string]$JellyfinSourceRoot,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$solution = Join-Path $projectRoot 'LibraryBridge.slnx'
$project = Join-Path $projectRoot 'Jellyfin.Plugin.LibraryBridge/Jellyfin.Plugin.LibraryBridge.csproj'
$arguments = @('-m:1', '-nr:false', '-p:UseSharedCompilation=false')
if ($JellyfinSourceRoot) {
    $resolvedSource = (Resolve-Path -LiteralPath $JellyfinSourceRoot).Path
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedSource 'Jellyfin.Server.Implementations/Item/NextUpService.cs'))) {
        throw 'JellyfinSourceRoot must point to an unpacked Jellyfin 12.4 source tree.'
    }
    $arguments += '-p:JellyfinSourceRoot=' + $resolvedSource
}
if (-not $NoRestore) {
    $restoreArguments = @('restore', $solution, '--configfile', (Join-Path $projectRoot 'NuGet.Config'), '--disable-parallel', '--nologo') + $arguments
    if ($PackagesPath) { $restoreArguments += @('--packages', $PackagesPath) }
    & $Dotnet @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
}

$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'Jellyfin.Plugin.LibraryBridge/obj/project.assets.json') -Raw | ConvertFrom-Json
if ($assets.libraries.PSObject.Properties.Name -notcontains 'Jellyfin.Controller/12.4.0') {
    throw 'Restore against Jellyfin 12.4.0 before creating a package.'
}
& $Dotnet build $solution --configuration Release --no-restore --nologo @arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $Dotnet test $solution --configuration Release --no-restore --no-build --nologo @arguments
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; package was not created.' }

$metadata = Join-Path $projectRoot 'meta.json'
$version = (Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).version
$outputRoot = Join-Path $projectRoot 'artifacts'
$packageDirectory = Join-Path $outputRoot ('Library Bridge_' + $version)
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$assembly = Join-Path $projectRoot 'Jellyfin.Plugin.LibraryBridge/bin/Release/net10.0/Jellyfin.Plugin.LibraryBridge.dll'
Copy-Item -LiteralPath $assembly -Destination $packageDirectory -Force
Copy-Item -LiteralPath $metadata -Destination $packageDirectory -Force
$archive = Join-Path $outputRoot ('LibraryBridge_' + $version + '_jellyfin12.4.zip')
Compress-Archive -LiteralPath (Join-Path $packageDirectory 'Jellyfin.Plugin.LibraryBridge.dll'), (Join-Path $packageDirectory 'meta.json') -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value ($checksum + '  ' + [IO.Path]::GetFileName($archive)) -Encoding ascii
Write-Output ('Package: ' + $archive)
Write-Output ('SHA256: ' + $checksum)
