<#
.SYNOPSIS
    Registers TurboDL's native messaging host with Chrome for the current user (or removes it).

.DESCRIPTION
    Writes com.turbodl.host.json (the host manifest Chrome reads) and points
    HKCU\Software\Google\Chrome\NativeMessagingHosts\com.turbodl.host at it. Only the extension whose ID is in
    extension\EXTENSION_ID.txt may talk to the host.

    When TurboDL.App.exe isn't next to the host (as in a dev build, where each project has its own bin folder),
    host-settings.json is written next to the host so it can start the app.

    Running it again just rewrites the same values. -Uninstall removes what it added. -WhatIf shows every change
    without making it. Nothing needs admin rights.

    Then load the extension: chrome://extensions > Developer mode > Load unpacked > the extension folder.

.PARAMETER HostPath
    TurboDL.NativeHost.exe. Default: the Release build output, else the Debug one.

.PARAMETER AppPath
    TurboDL.App.exe. Default: next to the host, else the app's build output of the same configuration.

.PARAMETER ExtensionId
    The Chrome extension allowed to use the host. Default: the contents of extension\EXTENSION_ID.txt.

.PARAMETER ManifestPath
    Where to write com.turbodl.host.json. Default: next to the host.

.PARAMETER Build
    Run "dotnet build -c Release" on the solution first.

.PARAMETER StartWithWindows
    Also start TurboDL in the tray at sign-in (HKCU Run entry "TurboDL", the same one the app's setting uses).

.PARAMETER Uninstall
    Remove the registration, the manifest, host-settings.json and TurboDL's Run entry.

.PARAMETER RegistryRoot
    Registry key that stands for HKCU\Software. Change it only to try the script against a scratch key.

.EXAMPLE
    .\scripts\install.ps1 -Build -StartWithWindows

.EXAMPLE
    .\scripts\install.ps1 -WhatIf

.EXAMPLE
    .\scripts\install.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$HostPath,
    [string]$AppPath,
    [string]$ExtensionId,
    [string]$ManifestPath,
    [switch]$Build,
    [switch]$StartWithWindows,
    [switch]$Uninstall,
    [string]$RegistryRoot = 'HKCU:\Software'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$HostName = 'com.turbodl.host'
$RunValueName = 'TurboDL'
$HostSettingsName = 'host-settings.json'
$Root = Split-Path -Parent $PSScriptRoot
$HostKey = Join-Path $RegistryRoot "Google\Chrome\NativeMessagingHosts\$HostName"
$RunKey = Join-Path $RegistryRoot 'Microsoft\Windows\CurrentVersion\Run'
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Write-JsonFile([string]$Path, $Object) {
    if ($PSCmdlet.ShouldProcess($Path, 'Write file')) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        [System.IO.File]::WriteAllText($Path, ($Object | ConvertTo-Json -Depth 5), $Utf8NoBom)
    }
}

function Remove-FileIfPresent([string]$Path) {
    if ($Path -and (Test-Path -LiteralPath $Path -PathType Leaf) -and $PSCmdlet.ShouldProcess($Path, 'Delete file')) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function Get-DefaultValue([string]$Key) {
    if (-not (Test-Path -LiteralPath $Key)) { return $null }
    return (Get-Item -LiteralPath $Key).GetValue('')
}

function Find-BuildOutput([string]$Project, [string]$Framework, [string]$Exe, [string[]]$Configurations) {
    foreach ($config in $Configurations) {
        $candidate = Join-Path $Root "src\$Project\bin\$config\$Framework\$Exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return @{ Path = $candidate; Configuration = $config } }
    }
    return $null
}

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    $manifest = Get-DefaultValue $HostKey
    if (-not $manifest -and $ManifestPath) { $manifest = $ManifestPath }

    if ($manifest -and (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        # host-settings.json sits next to the host the manifest points at.
        try {
            $hostExe = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).path
            if ($hostExe) { Remove-FileIfPresent (Join-Path (Split-Path -Parent $hostExe) $HostSettingsName) }
        }
        catch {
            Write-Warning "Couldn't read $manifest ($($_.Exception.Message)); host-settings.json, if any, is left in place."
        }
        Remove-FileIfPresent $manifest
    }

    if ((Test-Path -LiteralPath $HostKey) -and $PSCmdlet.ShouldProcess($HostKey, 'Delete registry key')) {
        Remove-Item -LiteralPath $HostKey -Recurse -Force
    }

    if (Test-Path -LiteralPath $RunKey) {
        $run = (Get-Item -LiteralPath $RunKey).GetValue($RunValueName)
        if ($run -and $run -like '*TurboDL.App.exe*' -and $PSCmdlet.ShouldProcess("$RunKey\$RunValueName", 'Delete registry value')) {
            Remove-ItemProperty -LiteralPath $RunKey -Name $RunValueName -Force
        }
    }

    if (-not $WhatIfPreference) {
        Write-Host 'TurboDL native messaging host removed. Remove the extension itself from chrome://extensions.'
    }
    return
}

# ---------------------------------------------------------------- install
if ($Build -and $PSCmdlet.ShouldProcess((Join-Path $Root 'TurboDL.slnx'), 'dotnet build -c Release')) {
    & dotnet build (Join-Path $Root 'TurboDL.slnx') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)." }
}

$configuration = $null
if ($HostPath) {
    $HostPath = (Resolve-Path -LiteralPath $HostPath).Path
}
else {
    $found = Find-BuildOutput 'TurboDL.NativeHost' 'net10.0' 'TurboDL.NativeHost.exe' @('Release', 'Debug')
    if (-not $found) { throw 'TurboDL.NativeHost.exe not found. Build first (dotnet build -c Release), pass -Build, or pass -HostPath.' }
    $HostPath = $found.Path
    $configuration = $found.Configuration
}
$hostDir = Split-Path -Parent $HostPath

if ($AppPath) {
    $AppPath = (Resolve-Path -LiteralPath $AppPath).Path
}
elseif (Test-Path -LiteralPath (Join-Path $hostDir 'TurboDL.App.exe') -PathType Leaf) {
    $AppPath = Join-Path $hostDir 'TurboDL.App.exe'
}
else {
    $configs = if ($configuration) { @($configuration) } else { @('Release', 'Debug') }
    $found = Find-BuildOutput 'TurboDL.App' 'net10.0-windows' 'TurboDL.App.exe' $configs
    if (-not $found) { throw 'TurboDL.App.exe not found next to the host or in the build output. Build it, or pass -AppPath.' }
    $AppPath = $found.Path
}

if (-not $ExtensionId) {
    $idFile = Join-Path $Root 'extension\EXTENSION_ID.txt'
    if (-not (Test-Path -LiteralPath $idFile)) { throw "No -ExtensionId given and $idFile doesn't exist." }
    $ExtensionId = (Get-Content -LiteralPath $idFile -Raw).Trim()
}
if ($ExtensionId -notmatch '^[a-p]{32}$') { throw "'$ExtensionId' isn't a Chrome extension ID (32 letters a-p)." }

if (-not $ManifestPath) { $ManifestPath = Join-Path $hostDir "$HostName.json" }
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)

Write-JsonFile $ManifestPath ([ordered]@{
    name            = $HostName
    description     = 'TurboDL download manager'
    path            = $HostPath
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
})

# The host finds the app next to itself; otherwise it needs telling where it is.
$hostSettings = Join-Path $hostDir $HostSettingsName
if ((Split-Path -Parent $AppPath) -ieq $hostDir) {
    Remove-FileIfPresent $hostSettings
}
else {
    Write-JsonFile $hostSettings ([ordered]@{ appPath = $AppPath })
}

if ($PSCmdlet.ShouldProcess($HostKey, "Set default value to $ManifestPath")) {
    New-Item -Path $HostKey -Force | Out-Null
    Set-Item -LiteralPath $HostKey -Value $ManifestPath
}

if ($StartWithWindows) {
    $command = "`"$AppPath`" --tray"
    if ($PSCmdlet.ShouldProcess("$RunKey\$RunValueName", "Set to $command")) {
        if (-not (Test-Path -LiteralPath $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
        Set-ItemProperty -LiteralPath $RunKey -Name $RunValueName -Value $command
    }
}

Write-Host "Host:      $HostPath"
Write-Host "App:       $AppPath"
Write-Host "Manifest:  $ManifestPath"
Write-Host "Registry:  $HostKey"
Write-Host "Extension: chrome-extension://$ExtensionId/"
if ($StartWithWindows) { Write-Host "Startup:   $RunKey\$RunValueName" }
if (-not $WhatIfPreference) {
    Write-Host 'Done. Load the extension from chrome://extensions (Developer mode > Load unpacked > extension folder).'
}
