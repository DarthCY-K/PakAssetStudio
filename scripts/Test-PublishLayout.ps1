param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $PublishDirectory).Path
$required = @(
    'PakAssetStudio.exe',
    'Tools/repak/repak.exe',
    'Tools/repak/oo2core_9_win64.dll',
    'Tools/repak/LICENSE-MIT',
    'Tools/repak/LICENSE-APACHE',
    'Tools/repak/README.md',
    'Tools/umodel/umodel_64.exe',
    'Tools/umodel/SDL2_64.dll',
    'Tools/umodel/LICENSE.txt',
    'Tools/umodel/README.txt',
    'Tools/assimp/assimp-vc143-mt.dll',
    'Tools/assimp/LICENSE',
    'Tools/python/python.exe',
    'Tools/python/LICENSE.txt',
    'Tools/convert_gltf_to_fbx.py',
    'Tools/merge_gltf.py',
    'Prerequisites/vc_redist.x64.exe',
    'LICENSE',
    'README.md',
    'THIRD-PARTY-NOTICES',
    'THIRD-PARTY-MANIFEST.txt'
)

foreach ($relative in $required) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Missing publish artifact: $relative"
    }

    if ((Get-Item $path).Length -eq 0) {
        throw "Empty publish artifact: $relative"
    }
}

$manifest = Get-Content (Join-Path $root 'THIRD-PARTY-MANIFEST.txt') -Raw
foreach ($component in @('repak', 'Python', 'UModel', 'Assimp')) {
    if ($manifest -notmatch "(?m)^$component version:\s*\S.*$") {
        throw "THIRD-PARTY-MANIFEST.txt has no non-empty $component version."
    }
}

$executable = Get-Item (Join-Path $root 'PakAssetStudio.exe')
$productVersion = $executable.VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw 'PakAssetStudio.exe has an empty ProductVersion.'
}

$productVersion = $productVersion.Trim()
if ($PSBoundParameters.ContainsKey('ExpectedVersion')) {
    if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        throw 'ExpectedVersion cannot be empty when specified.'
    }

    if ($productVersion -cne $ExpectedVersion) {
        throw "PakAssetStudio.exe ProductVersion mismatch. Expected '$ExpectedVersion', found '$productVersion'."
    }
}

Write-Host "Verified publish layout and ProductVersion '$productVersion': $root"
