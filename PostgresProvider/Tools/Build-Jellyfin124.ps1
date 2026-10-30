[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    [xml]$properties = Get-Content -Raw Directory.Build.props
    $version = [string]$properties.Project.PropertyGroup.Version
    $jellyfinVersion = [string]$properties.Project.PropertyGroup.JellyfinVersion.InnerText
    $abi = $jellyfinVersion + '.0'
    if ($jellyfinVersion -ne '12.4.0' -or $version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'This packaging script requires the Jellyfin 12.4 target and a four-part plugin version.'
    }

    if (-not $SkipTests) {
        & $Dotnet test Jellyfin.Database.Providers.Postgres.sln -c Release --disable-build-servers `
            --logger 'trx;LogFileName=jellyfin124.trx' --results-directory artifacts/test-results
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no package was produced.' }
    }

    $publish = Join-Path $repository 'artifacts/publish'
    & $Dotnet publish Jellyfin.Database.Providers.Postgres/Jellyfin.Database.Providers.Postgres.csproj `
        -c Release -p:IncludeJellyfinRuntime=false -o $publish --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # Package an explicit allowlist. The server supplies Jellyfin, EF Core and
    # ASP.NET assemblies; shipping those alongside the plugin can break loading.
    $files = @(
        'Jellyfin.Database.Providers.Postgres.dll',
        'Npgsql.dll',
        'Npgsql.EntityFrameworkCore.PostgreSQL.dll'
    )
    $assembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publish $files[0]))
    if ($assembly.Version.ToString() -ne $version) { throw 'The published assembly version does not match the package.' }
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Missing runtime file: $file" }
    }

    $manifest = [ordered]@{
        category = 'Database'
        changelog = 'Catalog-enabled Jellyfin 12.4 build. Retains local grouped-ID materialization, query splitting, rating-filter improvements, migration history and data.'
        description = 'PostgreSQL provider for Jellyfin 12.4. Built from BORNIOS source with local compatibility changes.'
        guid = 'a2b5f3e8-4c1d-4f7a-9e6b-8d0c2f1a3b5e'
        name = 'PostgreSQL Database Provider'
        overview = 'PostgreSQL backend for Jellyfin 12.4'
        owner = 'BORNIOS / prestonr83'
        targetAbi = $abi
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
        version = $version
        status = 'Active'
        autoUpdate = $true
        assemblies = @('Jellyfin.Database.Providers.Postgres.dll')
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $publish 'meta.json'),($manifest | ConvertTo-Json -Depth 5),$utf8)

    $archive = Join-Path $repository "artifacts/Jellyfin.Database.Providers.Postgres-$version-jellyfin12.4.zip"
    $packageFiles = @($files + 'meta.json' | ForEach-Object { Join-Path $publish $_ })
    Compress-Archive -LiteralPath $packageFiles -DestinationPath $archive -CompressionLevel Optimal -Force
    $sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archive + '.sha256'),"$sha256  $([IO.Path]::GetFileName($archive))`n",$utf8)

    $info = [ordered]@{
        version = $version
        jellyfinVersion = $jellyfinVersion
        targetAbi = $abi
        archive = [IO.Path]::GetFileName($archive)
        sha256 = $sha256
        upstreamCommit = (git rev-parse HEAD)
        files = @($packageFiles | ForEach-Object {
            @{ name = [IO.Path]::GetFileName($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    }
    [IO.File]::WriteAllText((Join-Path $repository 'artifacts/package-info.json'),($info | ConvertTo-Json -Depth 8),$utf8)
    Write-Output $archive
    Write-Output "SHA256: $sha256"
} finally {
    Pop-Location
}
