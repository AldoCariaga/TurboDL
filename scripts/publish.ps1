<#
.SYNOPSIS
    Builds the shippable TurboDL: dist\TurboDL-Setup-<version>.exe, plus dist\TurboDL.App.exe and
    dist\TurboDL.NativeHost.exe.

.DESCRIPTION
    Publishes the programs in Release as self-contained, single-file win-x64 exes. Each exe carries the .NET
    runtime and its native libraries (WPF's, SQLite's), so nothing else needs to be installed. The debug symbols are
    embedded in the exes, so crash logs keep their line numbers.

    The app and the native host are built first. TurboDL Setup is built last with those two exes inside it, so
    TurboDL-Setup-<version>.exe is the one file to hand out. The loose exes stay in dist\ too.

    Every exe gets the version from src\Directory.Build.props plus the git short hash, e.g. 0.6.0+7af5209
    (0.6.0+7af5209.dirty when the working tree has uncommitted changes). It's the exes' product version.

    Running it again rebuilds dist\ from scratch: everything in it is deleted first. Close any TurboDL that runs
    from dist\ before publishing.

.PARAMETER NoCompression
    Don't compress the bundled files. The exes come out about twice as big, and start a little faster.

.EXAMPLE
    .\scripts\publish.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoCompression
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'
$staging = Join-Path $repo 'obj\publish'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is not on PATH. Install the .NET 10 SDK.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git is not on PATH; it is needed for the version stamp.' }

# The version's build metadata: the commit, and whether the tree differs from it.
$revision = (& git -C $repo rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not $revision) { throw "git couldn't read the current commit in $repo." }
$changes = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0) { throw "git status failed in $repo." }
if ($changes) { $revision += '.dirty' }

$compress = if ($NoCompression) { 'false' } else { 'true' }
$started = Get-Date

# Publishes one project into obj\publish\<name> and returns its exe. Single-file should leave the exe alone; anything
# else would have to ship next to it, so that stops the build.
function Publish-Exe([string]$Name, [string[]]$ExtraProperties = @()) {
    $out = Join-Path $staging $Name
    Write-Host "Publishing $Name..."
    & dotnet publish (Join-Path $repo "src\$Name\$Name.csproj") `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $out `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$compress `
        -p:DebugType=embedded `
        -p:SourceRevisionId=$revision `
        @ExtraProperties | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Name (exit code $LASTEXITCODE)." }

    $extra = @(Get-ChildItem $out -Recurse -File | Where-Object Name -ne "$Name.exe")
    if ($extra.Count -gt 0) {
        throw "$Name published more than its exe: $(($extra | ForEach-Object { $_.FullName.Substring($out.Length + 1) }) -join ', ')"
    }
    return Join-Path $out "$Name.exe"
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

$app = Publish-Exe 'TurboDL.App'
$nativeHost = Publish-Exe 'TurboDL.NativeHost'

# Setup embeds the two exes from one folder.
$payload = Join-Path $staging 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
Copy-Item $app, $nativeHost $payload
$setup = Publish-Exe 'TurboDL.Setup' @("-p:SetupPayloadDir=$payload")

$version = ((Get-Item $setup).VersionInfo.ProductVersion -split '\+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+') { throw "Couldn't read the version from $setup (got '$version')." }

if (Test-Path $dist) {
    try {
        Remove-Item $dist -Recurse -Force
    }
    catch {
        throw "Couldn't clear $dist. Is TurboDL running from it? ($($_.Exception.Message))"
    }
}
New-Item -ItemType Directory -Path $dist | Out-Null
Copy-Item $app, $nativeHost $dist
Copy-Item $setup (Join-Path $dist "TurboDL-Setup-$version.exe")
Remove-Item $staging -Recurse -Force

Write-Host ''
Write-Host "Published to $dist in $([int]((Get-Date) - $started).TotalSeconds) s:"
Get-ChildItem $dist -File | ForEach-Object {
    [pscustomobject]@{
        File        = $_.Name
        'Size (MB)' = [math]::Round($_.Length / 1MB, 1)
        Version     = $_.VersionInfo.ProductVersion
    }
} | Format-Table -AutoSize | Out-String | Write-Host
