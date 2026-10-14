[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This intentionally changes installation state and isolated test startup values.
# It is not a local developer smoke test and must never run on a working desktop.
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:CI -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') {
    throw 'Installer smoke testing is restricted to an ephemeral GitHub-hosted Windows Actions runner with GITHUB_ACTIONS=true and CI=true.'
}
if (-not [Environment]::Is64BitProcess -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Use 64-bit PowerShell on the hosted Windows runner.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or
    [IO.Path]::GetFileName($installer) -notmatch '^DesktopRug-\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?-Setup-x64\.exe$') {
    throw 'The installer must be a versioned DesktopRug-<version>-Setup-x64.exe file.'
}
if ($ExpectedVersion -and [IO.Path]::GetFileName($installer) -cne "DesktopRug-$ExpectedVersion-Setup-x64.exe") {
    throw 'Installer filename does not match ExpectedVersion.'
}

$installerAppId = '{D02DE8D7-4909-4C4C-A835-B31FEBDDD47D}_is1'
$uninstallSubkey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\$installerAppId"
$startupSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$installDirectory = [IO.Path]::GetFullPath((Join-Path $localData 'Programs\DesktopRug'))
$dataDirectory = [IO.Path]::GetFullPath((Join-Path $localData 'DesktopRug'))
$installedExecutable = Join-Path $installDirectory 'DesktopRug.exe'
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'Desktop Rug\Desktop Rug.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'Desktop Rug.lnk'
$ownedStartupCommand = '"' + $installedExecutable + '" --background'
$portableStartupCommand = '"' + (Join-Path $env:RUNNER_TEMP 'Unrelated Portable Rug\DesktopRug.exe') + '" --background'
$checks = [ordered]@{}

function Get-StartupRecord {
    $rootKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $runKey = $rootKey.OpenSubKey($startupSubkey)
        try {
            if ($null -eq $runKey -or $runKey.GetValueNames() -notcontains 'DesktopRug') { return $null }
            return [pscustomobject]@{ Value = $runKey.GetValue('DesktopRug'); Kind = $runKey.GetValueKind('DesktopRug') }
        } finally { if ($null -ne $runKey) { $runKey.Dispose() } }
    } finally { $rootKey.Dispose() }
}

function Set-TestStartup([string]$Command) {
    $rootKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $runKey = $rootKey.CreateSubKey($startupSubkey)
        try { $runKey.SetValue('DesktopRug', $Command, [Microsoft.Win32.RegistryValueKind]::String) }
        finally { $runKey.Dispose() }
    } finally { $rootKey.Dispose() }
}

function Get-InstallationRecord([Microsoft.Win32.RegistryHive]$Hive = [Microsoft.Win32.RegistryHive]::CurrentUser) {
    $rootKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey($Hive, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $installationKey = $rootKey.OpenSubKey($uninstallSubkey)
        try {
            if ($null -eq $installationKey) { return $null }
            return [pscustomobject]@{
                Version = $installationKey.GetValue('DisplayVersion')
                Location = $installationKey.GetValue('InstallLocation')
            }
        } finally { if ($null -ne $installationKey) { $installationKey.Dispose() } }
    } finally { $rootKey.Dispose() }
}

function Assert-ApplicationStopped {
    if (@(Get-Process -Name 'DesktopRug' -ErrorAction SilentlyContinue).Count -ne 0) {
        throw 'Desktop Rug is running. The smoke script will not stop or kill any application.'
    }
}

# Refuse a reused machine before creating sentinels or touching any registry value.
Assert-ApplicationStopped
try {
    $existingAppMutex = [Threading.Mutex]::OpenExisting('Local\DesktopRug.Instance')
    $existingAppMutex.Dispose()
    throw 'Desktop Rug has a live single-instance mutex; refusing installer smoke testing.'
} catch [Threading.WaitHandleCannotBeOpenedException] { }
if ((Test-Path -LiteralPath $installDirectory) -or (Test-Path -LiteralPath $dataDirectory) -or
    (Test-Path -LiteralPath $startMenuShortcut) -or (Test-Path -LiteralPath $desktopShortcut) -or
    $null -ne (Get-StartupRecord) -or $null -ne (Get-InstallationRecord) -or
    $null -ne (Get-InstallationRecord -Hive LocalMachine)) {
    throw 'The runner already contains Desktop Rug files, settings, shortcuts, startup, or installation metadata. Use a fresh hosted runner.'
}

$smokeId = [Guid]::NewGuid().ToString('N')
$runnerTemporary = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
$logDirectory = [IO.Path]::GetFullPath((Join-Path $runnerTemporary "DesktopRug-installer-$smokeId"))
if (-not $logDirectory.StartsWith($runnerTemporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid smoke log directory.' }
[void](New-Item -ItemType Directory -Path $logDirectory)
[void](New-Item -ItemType Directory -Path $dataDirectory)
$settingsSentinel = Join-Path $dataDirectory 'settings.json'
[ordered]@{ Version = 1; Language = 'Persian'; Pattern = 'Tabriz'; InstallerSmokeSentinel = $smokeId } |
    ConvertTo-Json | Set-Content -LiteralPath $settingsSentinel -Encoding utf8
$sentinelDigest = (Get-FileHash -LiteralPath $settingsSentinel -Algorithm SHA256).Hash
$untrackedFile = Join-Path $installDirectory 'keep-user-file.txt'
$passed = $false

function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Installer check failed: $Name" }
    $checks[$Name] = $true
}

function Assert-Sentinel([string]$Phase) {
    Assert-Check (Test-Path -LiteralPath $settingsSentinel -PathType Leaf) "$Phase preserves settings file"
    Assert-Check ((Get-FileHash -LiteralPath $settingsSentinel -Algorithm SHA256).Hash -ceq $sentinelDigest) "$Phase preserves settings bytes"
}

function Invoke-Package([string]$Executable, [string]$Phase, [switch]$ExpectFailure) {
    Assert-ApplicationStopped
    $log = Join-Path $logDirectory "$Phase.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS', '/SP-', "/LOG=`"$log`"")
    # The setup/uninstaller is the only process launched. /VERYSILENT plus the package's
    # skipifsilent launch flag must leave DesktopRug.exe stopped. Never use taskkill.
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    try {
        if ($ExpectFailure) { Assert-Check ($process.ExitCode -ne 0) "$Phase refuses a running application" }
        else { Assert-Check ($process.ExitCode -eq 0) "$Phase exits successfully without reboot" }
    } finally { $process.Dispose() }
    Assert-ApplicationStopped
}

function Assert-Installed([string]$Phase) {
    Assert-Check (Test-Path -LiteralPath $installedExecutable -PathType Leaf) "$Phase installs the executable per user"
    Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory 'THIRD-PARTY-NOTICES.md') -PathType Leaf) "$Phase includes third-party notices"
    foreach ($notice in @('dotnet-license.txt', 'dotnet-third-party-notices.txt', 'skia-third-party-notices.txt', 'skiasharp-license.txt', 'windowsdesktop-license.txt')) {
        Assert-Check (Test-Path -LiteralPath (Join-Path $installDirectory "licenses\$notice") -PathType Leaf) "$Phase includes $notice"
    }
    $installation = Get-InstallationRecord
    Assert-Check ($null -ne $installation) "$Phase registers uninstall in HKCU"
    Assert-Check ([string]$installation.Location.TrimEnd('\') -ieq $installDirectory) "$Phase uses LocalAppData Programs"
    if ($ExpectedVersion) { Assert-Check ($installation.Version -ceq $ExpectedVersion) "$Phase registers the expected version" }
    Assert-Check ($null -eq (Get-InstallationRecord -Hive LocalMachine)) "$Phase creates no machine-wide uninstall entry"
    Assert-Check (Test-Path -LiteralPath $startMenuShortcut -PathType Leaf) "$Phase creates the Start Menu shortcut"
    Assert-Check (-not (Test-Path -LiteralPath $desktopShortcut)) "$Phase leaves the optional desktop shortcut unchecked"
    Assert-Sentinel $Phase
}

function Get-Uninstaller {
    $uninstallers = @(Get-ChildItem -LiteralPath $installDirectory -Filter 'unins*.exe' -File)
    if ($uninstallers.Count -ne 1) { throw 'Expected exactly one uninstaller under the validated application directory.' }
    return $uninstallers[0].FullName
}

try {
    # Actual named-object guards, without starting the application or acquiring input.
    $heldMutex = [Threading.Mutex]::new($false, 'Local\DesktopRug.Instance')
    try { Invoke-Package $installer 'blocked-install' -ExpectFailure }
    finally { $heldMutex.Dispose() }
    Assert-Check (-not (Test-Path -LiteralPath $installedExecutable)) 'Blocked install creates no executable'

    Invoke-Package $installer 'install'
    Assert-Installed 'Install'
    Assert-Check ($null -eq (Get-StartupRecord)) 'Install does not enable startup'
    Set-Content -LiteralPath $untrackedFile -Value $smokeId -NoNewline -Encoding utf8

    # Simulate an explicit app opt-in, then ensure an upgrade does not clear it.
    Set-TestStartup $ownedStartupCommand
    Invoke-Package $installer 'upgrade'
    Assert-Installed 'Upgrade'
    Assert-Check ((Get-StartupRecord).Value -ceq $ownedStartupCommand) 'Upgrade preserves opted-in startup'
    Assert-Check ((Get-Content -LiteralPath $untrackedFile -Raw) -ceq $smokeId) 'Upgrade preserves untracked installation files'
    $uninstaller = Get-Uninstaller
    $heldMutex = [Threading.Mutex]::new($false, 'Local\DesktopRug.Instance')
    try { Invoke-Package $uninstaller 'blocked-uninstall' -ExpectFailure }
    finally { $heldMutex.Dispose() }
    Assert-Check (Test-Path -LiteralPath $installedExecutable) 'Blocked uninstall leaves executable intact'
    Assert-Check ((Get-StartupRecord).Value -ceq $ownedStartupCommand) 'Blocked uninstall leaves startup intact'

    Invoke-Package $uninstaller 'uninstall-owned-startup'
    Assert-Check (-not (Test-Path -LiteralPath $installedExecutable)) 'Uninstall removes installed executable'
    Assert-Check ($null -eq (Get-InstallationRecord)) 'Uninstall removes its HKCU uninstall entry'
    Assert-Check ($null -eq (Get-StartupRecord)) 'Uninstall removes only its matching startup command'
    Assert-Check (-not (Test-Path -LiteralPath $startMenuShortcut)) 'Uninstall removes its Start Menu shortcut'
    Assert-Check ((Get-Content -LiteralPath $untrackedFile -Raw) -ceq $smokeId) 'Uninstall preserves untracked installation files'
    Assert-Sentinel 'Uninstall'

    # Another portable copy may own the shared value name. Installation and uninstall
    # must preserve that distinct command rather than deleting by value name alone.
    Set-TestStartup $portableStartupCommand
    Invoke-Package $installer 'reinstall'
    Assert-Installed 'Reinstall'
    Assert-Check ((Get-StartupRecord).Value -ceq $portableStartupCommand) 'Reinstall preserves unrelated portable startup'
    Invoke-Package (Get-Uninstaller) 'uninstall-portable-startup'
    Assert-Check ((Get-StartupRecord).Value -ceq $portableStartupCommand) 'Uninstall preserves unrelated portable startup'
    Assert-Check (-not (Test-Path -LiteralPath $installedExecutable)) 'Second uninstall removes installed executable'
    Assert-Sentinel 'Second uninstall'
    $passed = $true
} finally {
    # Remove only commands this smoke run deliberately wrote. Never delete the Run key,
    # other values, settings, or directories. The ephemeral runner disposes test files.
    $startup = Get-StartupRecord
    if ($null -ne $startup -and ($startup.Value -ceq $ownedStartupCommand -or $startup.Value -ceq $portableStartupCommand)) {
        $rootKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $runKey = $rootKey.OpenSubKey($startupSubkey, $true)
            try { if ($null -ne $runKey) { $runKey.DeleteValue('DesktopRug', $false) } }
            finally { if ($null -ne $runKey) { $runKey.Dispose() } }
        } finally { $rootKey.Dispose() }
    }
    [ordered]@{ passed = $passed; checks = $checks } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $logDirectory 'installer-smoke.json') -Encoding utf8
    Write-Host "Installer smoke evidence: $logDirectory"
}

if (-not $passed) { throw 'Installer smoke did not complete.' }
Write-Host "Installer smoke passed: $($checks.Count) checks; the application was never launched."
