<#
.SYNOPSIS
    Builds the shippable TurboDL: dist\TurboDL.App.exe and dist\TurboDL.NativeHost.exe.

.DESCRIPTION
    Publishes both programs in Release as self-contained, single-file win-x64 exes. Each exe carries the .NET
    runtime and its native libraries (WPF's, SQLite's), so nothing else needs to be installed and dist\ holds just
    the two files. The debug symbols are embedded in the exes, so crash logs keep their line numbers.

    Both exes get the version from src\Directory.Build.props plus the git short hash, e.g. 0.6.0+7af5209
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
$projects = @(
    @{ Name = 'TurboDL.App'; Project = 'src\TurboDL.App\TurboDL.App.csproj' }
    @{ Name = 'TurboDL.NativeHost'; Project = 'src\TurboDL.NativeHost\TurboDL.NativeHost.csproj' }
)

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

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

foreach ($p in $projects) {
    $out = Join-Path $staging $p.Name
    Write-Host "Publishing $($p.Name)..."
    & dotnet publish (Join-Path $repo $p.Project) `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $out `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$compress `
        -p:DebugType=embedded `
        -p:SourceRevisionId=$revision
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($p.Name) (exit code $LASTEXITCODE)." }

    # Single-file should leave the exe alone. Anything else would have to ship next to it, so stop and say so.
    $extra = @(Get-ChildItem $out -Recurse -File | Where-Object Name -ne "$($p.Name).exe")
    if ($extra.Count -gt 0) {
        throw "$($p.Name) published more than its exe: $(($extra | ForEach-Object { $_.FullName.Substring($out.Length + 1) }) -join ', ')"
    }
}

if (Test-Path $dist) {
    try {
        Remove-Item $dist -Recurse -Force
    }
    catch {
        throw "Couldn't clear $dist. Is TurboDL running from it? ($($_.Exception.Message))"
    }
}
New-Item -ItemType Directory -Path $dist | Out-Null
foreach ($p in $projects) {
    Copy-Item (Join-Path $staging "$($p.Name)\$($p.Name).exe") $dist
}
Remove-Item $staging -Recurse -Force

Write-Host ''
Write-Host "Published to $dist in $([int]((Get-Date) - $started).TotalSeconds) s:"
Get-ChildItem $dist -File | ForEach-Object {
    [pscustomobject]@{
        File    = $_.Name
        'Size (MB)' = [math]::Round($_.Length / 1MB, 1)
        Version = $_.VersionInfo.ProductVersion
    }
} | Format-Table -AutoSize | Out-String | Write-Host
