param(
    [string] $Version = 'dev',
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$semanticVersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version must be "dev" or a semantic version such as 0.5.0 or 0.5.0-rc.1.'
}

$isDevelopmentBuild = $Version -ceq 'dev'
if (-not $isDevelopmentBuild) {
    $versionMatch = [regex]::Match($Version, $semanticVersionPattern)
    if (-not $versionMatch.Success) {
        throw "Invalid version '$Version'. Use SemVer without a leading 'v', for example 0.5.0 or 0.5.0-rc.1."
    }

    foreach ($component in $versionMatch.Groups[1..3]) {
        $numericComponent = 0
        if (-not [int]::TryParse($component.Value, [ref] $numericComponent) -or $numericComponent -gt 65534) {
            throw "Invalid version '$Version'. Major, minor, and patch must each be between 0 and 65534."
        }
    }
}

Push-Location $repository
try {
    $releaseDirectory = Join-Path $repository 'artifacts/release'
    New-Item $releaseDirectory -ItemType Directory -Force | Out-Null
    $archive = Join-Path $releaseDirectory "PakAssetStudio-v$Version-win-x64.zip"
    $checksum = "$archive.sha256"
    foreach ($staleArtifact in @($archive, $checksum)) {
        if (Test-Path $staleArtifact -PathType Leaf) {
            Remove-Item $staleArtifact -Force
        }
    }

    if (-not $SkipTests) {
        dotnet test .\PakAssetStudio.slnx -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }

        $previousAssimpTestDll = $env:ASSIMP_TEST_DLL
        try {
            $env:ASSIMP_TEST_DLL = Join-Path $repository 'tools/assimp/Release/assimp-vc143-mt.dll'
            .\tools\python\runtime\python.exe -m unittest discover -s .\tools\tests -v
            if ($LASTEXITCODE -ne 0) { throw 'Python tests failed' }
        }
        finally {
            $env:ASSIMP_TEST_DLL = $previousAssimpTestDll
        }
    }

    $publish = Join-Path $repository 'artifacts/publish/win-x64'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    $publishArguments = @(
        'publish',
        '.\PakAssetStudio\PakAssetStudio.csproj',
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-o', $publish
    )
    if (-not $isDevelopmentBuild) {
        $publishArguments += "-p:MinVerVersionOverride=$Version"
        $publishArguments += '-p:IncludeSourceRevisionInInformationalVersion=false'
    }

    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    & .\scripts\Write-ThirdPartyManifest.ps1 -PublishDirectory $publish
    if ($isDevelopmentBuild) {
        & .\scripts\Test-PublishLayout.ps1 -PublishDirectory $publish
    }
    else {
        & .\scripts\Test-PublishLayout.ps1 -PublishDirectory $publish -ExpectedVersion $Version
    }

    try {
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
        $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path $archive -Leaf)" |
            Set-Content $checksum -Encoding ascii
    }
    catch {
        Remove-Item $archive, $checksum -Force -ErrorAction SilentlyContinue
        throw
    }

    Write-Host "Published: $archive"
    Write-Host "SHA-256:  $hash"
}
finally {
    Pop-Location
}
