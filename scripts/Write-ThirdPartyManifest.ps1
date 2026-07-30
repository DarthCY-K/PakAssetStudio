param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $PublishDirectory).Path
$output = Join-Path $root 'THIRD-PARTY-MANIFEST.txt'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('PAK Asset Studio third-party runtime manifest')
$lines.Add("Generated (UTC): $([DateTime]::UtcNow.ToString('O'))")
$lines.Add('')

function Invoke-VersionCommand {
    param(
        [string] $Name,
        [string] $FilePath,
        [string[]] $Arguments
    )

    try {
        $value = (& $FilePath @Arguments 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw "version command exited with code $LASTEXITCODE" }
        if ([string]::IsNullOrWhiteSpace($value)) { throw 'version command produced no output' }
    }
    catch {
        throw "$Name version query failed: $($_.Exception.Message)"
    }

    return $value
}

$repakPath = Join-Path $root 'Tools/repak/repak.exe'
$pythonPath = Join-Path $root 'Tools/python/python.exe'
$umodelPath = Join-Path $root 'Tools/umodel/umodel_64.exe'
$assimpPath = Join-Path $root 'Tools/assimp/assimp-vc143-mt.dll'

$repakVersion = Invoke-VersionCommand 'repak' $repakPath @('--version')
$pythonVersion = Invoke-VersionCommand 'Python' $pythonPath @('--version')
$lines.Add("repak version: $repakVersion")
$lines.Add("Python version: $pythonVersion")

try {
    $umodelOutput = Invoke-VersionCommand 'UModel' $umodelPath @('-version')
    $umodelMatch = [regex]::Match(
        $umodelOutput,
        '(?im)^Compiled\s+(?<date>.+?)\s+\(build\s+(?<build>[0-9]+)\)\s*$'
    )
    if ($umodelMatch.Success) {
        $compiledDate = ($umodelMatch.Groups['date'].Value -replace '\s+', ' ').Trim()
        $umodelVersion = "build $($umodelMatch.Groups['build'].Value) (compiled $compiledDate)"
    }
    else {
        $umodelHash = (Get-FileHash $umodelPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $umodelVersion = "unreported (artifact sha256:$umodelHash)"
    }
}
catch {
    $umodelHash = (Get-FileHash $umodelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $umodelVersion = "unreported (artifact sha256:$umodelHash)"
}
$lines.Add("UModel version: $umodelVersion")

$assimpVersionCode = 'import ctypes, sys; lib = ctypes.CDLL(sys.argv[1]); print(lib.aiGetVersionMajor(), lib.aiGetVersionMinor(), lib.aiGetVersionPatch(), sep=chr(46))'
$assimpVersion = Invoke-VersionCommand 'Assimp' $pythonPath @('-c', $assimpVersionCode, $assimpPath)
if ($assimpVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Assimp version query returned an unexpected value: $assimpVersion"
}
$lines.Add("Assimp version: $assimpVersion")
$lines.Add('')
$lines.Add('SHA-256:')

$files = [System.Collections.Generic.List[string]]::new()
$files.AddRange([string[]] @(
    'Tools/repak/repak.exe',
    'Tools/umodel/umodel_64.exe',
    'Tools/umodel/SDL2_64.dll',
    'Tools/assimp/assimp-vc143-mt.dll',
    'Tools/convert_gltf_to_fbx.py',
    'Tools/merge_gltf.py',
    'Prerequisites/vc_redist.x64.exe'
))
Get-ChildItem (Join-Path $root 'Tools/python') -File -Recurse | ForEach-Object {
    # Path.GetRelativePath is unavailable in Windows PowerShell 5.1.
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    $files.Add($relative)
}
foreach ($relative in ($files | Sort-Object -Unique)) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path -PathType Leaf)) {
        throw "Missing third-party publish artifact: $relative"
    }
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add("$hash  $relative")
}

$lines | Set-Content $output -Encoding UTF8
Write-Host "Wrote $output"
