; Compile only: ISCC /DAppVersion=0.4.0 /DSourceDir=<absolute publish folder>
;               /DOutputDir=<absolute output folder> packaging\desktop-rug.iss
; The application owns opt-in startup and user data. Setup never launches it silently,
; enables startup, kills a running copy, or removes %LOCALAPPDATA%\DesktopRug.

#ifndef AppVersion
  #error AppVersion must be supplied with /DAppVersion
#endif
#ifndef SourceDir
  #error SourceDir must be supplied with /DSourceDir
#endif
#ifndef OutputDir
  #error OutputDir must be supplied with /DOutputDir
#endif
#if !FileExists(SourceDir + "\DesktopRug.exe")
  #error SourceDir must contain the published DesktopRug.exe
#endif

#define NumericVersion GetVersionNumbersString(SourceDir + "\DesktopRug.exe")

[Setup]
; Stable across releases: upgrades use the same per-user installation and uninstall log.
AppId={{D02DE8D7-4909-4C4C-A835-B31FEBDDD47D}
AppName=Desktop Rug
AppVersion={#AppVersion}
AppPublisher=Desktop Rug project
AppPublisherURL=https://github.com/vahidpourreza/DesktopRug
AppSupportURL=https://github.com/vahidpourreza/DesktopRug/issues
AppUpdatesURL=https://github.com/vahidpourreza/DesktopRug/releases
DefaultDirName={localappdata}\Programs\DesktopRug
DefaultGroupName=Desktop Rug
DisableProgramGroupPage=yes
AlwaysUsePersonalGroup=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile={#SourcePath}..\src\DesktopRug.App\Resources\rug.ico
UninstallDisplayIcon={app}\DesktopRug.exe
UninstallDisplayName=Desktop Rug
Uninstallable=yes
UninstallLogMode=append
UninstallLogging=yes
CreateUninstallRegKey=yes
; AppMutex is Inno Setup's documented check for BOTH setup and uninstall.
; Inno Setup 6 has no separate UninstallAppMutex directive.
AppMutex=Local\DesktopRug.Instance
SetupMutex=Local\DesktopRug.Setup
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AlwaysRestart=no
OutputDir={#OutputDir}
OutputBaseFilename=DesktopRug-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
VersionInfoVersion={#NumericVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductName=Desktop Rug
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\DesktopRug.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePath}..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Desktop Rug"; Filename: "{app}\DesktopRug.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Desktop Rug"; Filename: "{app}\DesktopRug.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DesktopRug.exe"; Description: "Launch Desktop Rug"; WorkingDir: "{app}"; Flags: postinstall unchecked skipifsilent nowait runasoriginaluser

[Code]
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function InitializeSetup: Boolean;
begin
  { Silent automation must fail instead of presenting a running-app retry dialog.
    Interactive installations retain the standard AppMutex prompt. }
  Result := not (WizardSilent and CheckForMutexes('Local\DesktopRug.Instance'));
  if not Result then
    Log('Desktop Rug is running. Setup was canceled without closing the application.');
end;

function InitializeUninstall: Boolean;
begin
  Result := not (UninstallSilent and CheckForMutexes('Local\DesktopRug.Instance'));
  if not Result then
    Log('Desktop Rug is running. Uninstall was canceled without closing the application.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Check again in case the app was opened while the wizard was waiting. }
  Result := '';
  if CheckForMutexes('Local\DesktopRug.Instance') then
    Result := 'Close Desktop Rug from its tray menu, then retry setup. Setup will not close it for you.';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentCommand: String;
  OwnedCommand: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  { Match the app's exact command shape. Never remove another portable copy's entry,
    custom arguments, or other startup values. HKCU64 matches the installed x64 app. }
  OwnedCommand := '"' + ExpandConstant('{app}\DesktopRug.exe') + '" --background';
  if RegQueryStringValue(HKCU64, StartupKey, 'DesktopRug', CurrentCommand) then
    if CurrentCommand = OwnedCommand then
      if RegDeleteValue(HKCU64, StartupKey, 'DesktopRug') then
        Log('Removed the sign-in entry belonging to this Desktop Rug installation.')
      else
        Log('The installation-owned sign-in entry could not be removed.');
end;

{ There is deliberately no Registry, InstallDelete, or UninstallDelete section.
  Normal uninstall-log ownership removes installed files/shortcuts only. Existing
  user settings, logs, custom images, and untracked files are never recursively deleted. }
