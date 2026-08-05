param(
    [string] $Version = 'dev',
    [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [string] $RepakBinary = '',
    [string] $AssimpDylib = '',
    [string] $AssimpVersion = '6.0.5',
    [switch] $SkipTools
)

# macOS 发布（本机路径，产出 arm64 .app；universal 走 codemagic.yaml）
# 用法：
#   dev 快速验证（无 repak mac 二进制）：.\scripts\Publish-mac.ps1 -Version dev -SkipTools
#   正式发布：.\scripts\Publish-mac.ps1 -Version X.Y.Z -RepakBinary <CI 产物或本机 repak> [-AssimpDylib <libassimp.dylib>]
# 说明：repak macOS 二进制需 macOS CI（codemagic.yaml）构建；Assimp dylib 默认取
#       artifacts/mac-tools/assimp/libassimp.dylib（官方 release macos-arm64 zip 解压）。

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$semanticVersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version must be "dev" or a semantic version such as 0.7.0 or 0.7.0-rc.1.'
}

$isDevelopmentBuild = $Version -ceq 'dev'
if (-not $isDevelopmentBuild) {
    $versionMatch = [regex]::Match($Version, $semanticVersionPattern)
    if (-not $versionMatch.Success) {
        throw "Invalid version '$Version'. Use SemVer without a leading 'v', for example 0.7.0 or 0.7.0-rc.1."
    }
}

if (-not $SkipTools -and [string]::IsNullOrWhiteSpace($RepakBinary)) {
    throw 'RepakBinary is required for a full release (macOS CI artifact). Use -SkipTools for a dev validation build.'
}

Push-Location $repository
try {
    $releaseDirectory = Join-Path $repository 'artifacts/release'
    New-Item $releaseDirectory -ItemType Directory -Force | Out-Null

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

    $publish = Join-Path $repository 'artifacts/publish/osx-arm64'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    $publishArguments = @(
        'publish',
        '.\PakAssetStudio.Avalonia\PakAssetStudio.Avalonia.csproj',
        '-c', $Configuration,
        '-r', 'osx-arm64',
        '--self-contained', 'true',
        '-o', $publish
    )
    if (-not $isDevelopmentBuild) {
        $publishArguments += "-p:MinVerVersionOverride=$Version"
        $publishArguments += '-p:IncludeSourceRevisionInInformationalVersion=false'
    }

    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    if (-not $SkipTools) {
        if ([string]::IsNullOrWhiteSpace($AssimpDylib)) {
            $defaultAssimp = Join-Path $repository 'artifacts/mac-tools/assimp/libassimp.dylib'
            if (Test-Path $defaultAssimp -PathType Leaf) {
                $AssimpDylib = $defaultAssimp
            }
            else {
                throw 'AssimpDylib not found. Download the official macos-arm64 release zip and extract it, e.g.: tools/python/runtime/python.exe -c "import zipfile; zipfile.ZipFile(''artifacts/mac-tools/assimp-macos.zip'').extractall(''artifacts/mac-tools/assimp'')"'
            }
        }
    }

    $packArguments = @(
        'scripts/mac_pack.py',
        '--publish-dir', $publish,
        '--version', $Version,
        '--arch', 'arm64',
        '--output', $releaseDirectory,
        '--assimp-version', $AssimpVersion
    )
    if (-not $SkipTools) {
        $packArguments += '--repak', $RepakBinary
        $packArguments += '--assimp', $AssimpDylib
    }
    else {
        $packArguments += '--skip-tools'
    }

    .\tools\python\runtime\python.exe @packArguments
    if ($LASTEXITCODE -ne 0) { throw 'mac_pack.py failed' }
}
finally {
    Pop-Location
}
