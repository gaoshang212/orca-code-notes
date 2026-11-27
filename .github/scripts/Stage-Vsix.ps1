param(
    [string]$Tag,
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../OrcaNotes'
$package = (Resolve-Path (Join-Path $project 'bin/Release/net472/OrcaNotes.vsix')).Path
[xml]$source = Get-Content -Raw -LiteralPath (Join-Path $project 'source.extension.vsixmanifest')
$identity = $source.PackageManifest.Metadata.Identity
$version = $identity.Version
if ($version -notmatch '^[0-9]+(\.[0-9]+){1,3}$' -or -not ($version -as [version])) {
    throw "Invalid VSIX version '$version'."
}
if ($Tag -and $Tag -cne "v$version") {
    throw "Tag '$Tag' does not match the manifest version. Expected 'v$version'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $manifest = [xml]::new()
    $manifest.Load($zip.GetEntry('extension.vsixmanifest').Open())
    $packaged = $manifest.PackageManifest.Metadata.Identity
    if ($packaged.Id -cne $identity.Id -or $packaged.Version -cne $version) {
        throw 'The packaged VSIX identity or version differs from the source manifest.'
    }
} finally {
    $zip.Dispose()
}

$asset = "OrcaNotes-$version.vsix"
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
Copy-Item -LiteralPath $package -Destination (Join-Path $OutputDirectory $asset)
if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "asset=$asset" -Encoding utf8 }
Write-Host "Prepared $asset"
