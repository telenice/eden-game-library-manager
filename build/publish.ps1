<#
.SYNOPSIS
    Builds a self-contained Windows release of Eden Game Library Manager.

.DESCRIPTION
    Runs the unit tests, publishes a single-file self-contained executable for each runtime,
    optionally signs it with signtool, and writes a portable ZIP plus a SHA-256 checksum to
    artifacts\. Works in Windows PowerShell 5.1 and PowerShell 7+. Requires the .NET 10 SDK.

.PARAMETER Runtime
    One or more Windows runtime identifiers: win-x64 (default) and/or win-arm64.

.PARAMETER Configuration
    Build configuration (default Release).

.PARAMETER SkipTests
    Skip running the unit tests before publishing.

.PARAMETER CertificateThumbprint
    SHA-1 thumbprint of a code-signing certificate in your certificate store. When given, the
    executable is signed with signtool (Windows SDK) and timestamped.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used when signing.

.EXAMPLE
    .\build\publish.ps1

.EXAMPLE
    .\build\publish.ps1 -Runtime win-x64,win-arm64 -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [string[]]$Runtime = @('win-x64'),
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    # Overrides <Version> from Directory.Build.props (the release workflow passes the tag's version).
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\EdenGameLibraryManager.App\EdenGameLibraryManager.App.csproj'
$solution = Join-Path $root 'EdenGameLibraryManager.sln'
$artifacts = Join-Path $root 'artifacts'
if ($Version) {
    $version = $Version
} else {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}

function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    throw 'signtool.exe was not found. Install the Windows SDK or omit -CertificateThumbprint.'
}

# "-Runtime win-x64,win-arm64" arrives as one string when invoked through -File (e.g. publish.cmd).
$Runtime = @($Runtime | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($rid in $Runtime) {
    if ($rid -notin @('win-x64', 'win-arm64')) { throw "Unsupported runtime '$rid'. Use win-x64 and/or win-arm64." }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download.'
}

Write-Host "Eden Game Library Manager $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '==> Running unit tests' -ForegroundColor Cyan
    Invoke-Checked 'dotnet' @('test', $solution, '-c', $Configuration, '--nologo')
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

foreach ($rid in $Runtime) {
    $name = "EdenGameLibraryManager-$version-$rid"
    $out = Join-Path $artifacts $rid
    $publish = Join-Path $out 'publish'
    $stage = Join-Path $out $name
    $zip = Join-Path $artifacts "$name.zip"

    Write-Host "==> Publishing $rid" -ForegroundColor Cyan
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Invoke-Checked 'dotnet' @('publish', $project, '-c', $Configuration, '-r', $rid, '-o', $publish,
        "-p:Version=$version", '-p:DebugType=embedded', '--nologo')

    $exe = Join-Path $publish 'EdenGameLibraryManager.exe'
    if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }

    if ($CertificateThumbprint) {
        Write-Host '==> Signing executable' -ForegroundColor Cyan
        $signtool = Find-SignTool
        Invoke-Checked $signtool @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/tr', $TimestampUrl, '/td', 'SHA256', $exe)
        Invoke-Checked $signtool @('verify', '/pa', $exe)
    }

    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item $exe $stage
    # End-user guide; the repository README is for developers.
    Copy-Item (Join-Path $root 'docs\package\README.txt') $stage

    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii

    $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "==> Created $zip ($sizeMb MB)" -ForegroundColor Green
    Write-Host "    SHA-256 $hash"
}
