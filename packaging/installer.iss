; Inno Setup script for the Windows installer. The workspace build script supplies the packaged app path and
; passes the application version with /DAppVersion=... The default below is kept equal to it by tools/set-version.py.

#ifndef AppVersion
  #define AppVersion "0.5.1"
#endif

#ifndef AppSourceDir
  #define AppSourceDir "..\dist\EtherlightingEvolved"
#endif

; tools/ci-inno.ps1 installs 6.7.4 for CI. Any 6.7 release is accepted, so a local build with another patch release
; does not need -AllowUnpinned for the compiler alone. A local test build may pass /DAnyCompiler to accept others.
#if !Defined(AnyCompiler) && DecodeVer(Ver, 2) != "6.7"
  #error Compile with Inno Setup 6.7 (CI uses 6.7.4), or build with -AllowUnpinned.
#endif

; The build script names this file so that package-verification.json records which release compiled the installer.
#ifdef CompilerRecord
  #expr WriteIni(CompilerRecord, "Compiler", "Release", DecodeVer(Ver, 4))
#endif

#define AppName "Etherlighting Evolved"
#define AppExe "EtherlightingEvolved.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
; The AppId identifies the app across upgrades. Never change it.
AppId={{B5958405-DA3C-4585-BFA8-5DBFF340C199}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Views2k
AppPublisherURL=https://github.com/Views2k/etherlighting-evolved
AppSupportURL=https://github.com/Views2k/etherlighting-evolved/issues
AppCopyright=Copyright (c) 2026 Views2k
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\etherlighting_evolved\assets\ee.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard.bmp,wizard-200.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-200.bmp
CloseApplications=no
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=EtherlightingEvolved-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

[Messages]
WelcomeLabel1=Etherlighting Evolved
WelcomeLabel2=Install lighting controls for your UniFi switch.%n%nUnofficial community project. Not affiliated with Ubiquiti.
FinishedHeadingLabel=Etherlighting Evolved
FinishedLabel=Installation complete. The app runs in the notification area to keep your lighting active when its window is closed.
SelectDirLabel3=Choose where to install the app.

[Tasks]
Name: "autostart"; Description: "Start the app when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[InstallDelete]
; An upgrade replaces the whole application, so no file from an earlier version stays behind. The app keeps no
; user data in {app}, and the uninstaller (unins000.*) is left alone. Setup has already asked a running copy to
; quit. Cancelling setup after this step leaves an incomplete app that needs a new install.
; The Python runtime of the 0.4 desktop program.
Type: filesandordirs; Name: "{app}\_internal"
Type: filesandordirs; Name: "{app}\engine"
Type: filesandordirs; Name: "{app}\preview"
Type: filesandordirs; Name: "{app}\src"
Type: filesandordirs; Name: "{app}\assets"
Type: filesandordirs; Name: "{app}\runtime"
Type: filesandordirs; Name: "{app}\licenses"
Type: filesandordirs; Name: "{app}\tools"
Type: files; Name: "{app}\*.dll"

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart
; Removed only when someone chose so: on the tasks page or with /TASKS. A silent upgrade keeps the current setting,
; which the app's notification area menu can also change.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: none; ValueName: "{#AppName}"; Flags: deletevalue; Tasks: not autostart; Check: TasksChosen

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open lighting controls"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppMutex = 'EtherlightingEvolved';

var
  TasksPrepared: Boolean;

function TasksOnCommandLine(): Boolean;
var
  Tail: String;
begin
  Tail := Uppercase(GetCmdTail);
  Result := (Pos('/TASKS=', Tail) > 0) or (Pos('/MERGETASKS=', Tail) > 0);
end;

function TasksChosen(): Boolean;
begin
  Result := (not WizardSilent) or TasksOnCommandLine();
end;

{ The tasks page starts from the current Start with Windows setting. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not TasksPrepared then
  begin
    TasksPrepared := True;
    if not TasksOnCommandLine() and RegValueExists(HKCU, '{#RunKey}', '{#AppName}') then
      WizardSelectTasks('autostart');
  end;
end;

procedure InitializeWizard();
begin
  WizardForm.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel1.Font.Name := 'Segoe UI';
  WizardForm.WelcomeLabel1.Font.Style := [fsBold];
  WizardForm.WelcomeLabel1.Font.Size := 20;
  WizardForm.FinishedHeadingLabel.Font.Name := 'Segoe UI';
  WizardForm.FinishedHeadingLabel.Font.Style := [fsBold];
  WizardForm.FinishedHeadingLabel.Font.Size := 20;
end;

{ Ask a running copy to quit before replacing its files. }
function QuitRunningCopy(): Boolean;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := True;
  if not (CheckForMutexes(AppMutex) or CheckForMutexes('EtherlightingEvolved.Desktop')) then
    Exit;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := False;
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := not (CheckForMutexes(AppMutex) or CheckForMutexes('EtherlightingEvolved.Desktop'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not QuitRunningCopy() then
    Result := 'EE is still running. Quit the desktop app from its notification area menu, then reopen setup.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := QuitRunningCopy();
  if not Result then
    MsgBox('EE is still running. Quit the desktop app from its notification area menu, then retry uninstalling.', mbError, MB_OK);
end;

{ EE removes its Tailscale Serve route each time it quits. This removes one left by an unclean stop. The tool exits
  with the route's HTTPS port when it cannot confirm the removal, and the uninstall continues either way. }
procedure RemovePrivateAccessRoute();
var
  Node, Script: String;
  ResultCode: Integer;
begin
  { An uninstall for all users runs elevated and cannot reliably read this user's EE settings. desktop/README.md
    gives the command to run instead. }
  if IsAdminInstallMode then
    Exit;
  Node := ExpandConstant('{app}\runtime\node.exe');
  Script := ExpandConstant('{app}\tools\private-access-cleanup.mjs');
  if (not FileExists(Node)) or (not FileExists(Script)) then
    Exit;
  if not Exec(Node, AddQuotes(Script), ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  if UninstallSilent or (ResultCode = 0) then
    Exit;
  if (ResultCode >= 1024) and (ResultCode <= 65535) then
    MsgBox(Format('EE could not confirm that its Tailscale private address was removed. If you use Tailscale on this computer, open PowerShell and run "tailscale serve status". If port %d forwards to http://127.0.0.1, remove it with "tailscale serve --https=%d off".', [ResultCode, ResultCode]), mbInformation, MB_OK)
  else
    MsgBox('EE could not confirm that its Tailscale private address was removed. If you use Tailscale on this computer, open PowerShell and run "tailscale serve status" to review its routes.', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemovePrivateAccessRoute();
    { The app's own "Start with Windows" switch can add this value even if the installer didn't. }
    RegDeleteValue(HKCU, '{#RunKey}', '{#AppName}');
  end;
end;
