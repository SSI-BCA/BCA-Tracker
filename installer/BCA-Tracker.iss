; BCA-Tracker installer script for Inno Setup 6.x.
;
; This installer packages the SingleFile-published tracker executable
; together with the NetBird Windows MSI, installs both, and creates
; Start menu / desktop shortcuts.
;
; Build with:  ISCC.exe installer\BCA-Tracker.iss
; Output:      installer\dist\BCA-Tracker-Setup-<version>.exe
;
; Requirements:
;   1. Tracker must be published with PublishSingleFile=true first.
;      The build-installer.ps1 script handles this.
;   2. netbird_installer.msi must exist in installer\bundle\ - also
;      fetched by the build script.

#define AppName "BCA-Tracker"
#define AppPublisher "SSI-BCA"
#define AppURL "https://github.com/SSI-BCA/BCA-Tracker"
#define AppExeName "BCA-Tracker.exe"
; AppVersion is supplied on the ISCC command line via /DAppVersion=x.y.z.
; Falls back to a placeholder if not provided so the script still compiles.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{B7C8E9A0-3F4D-4E5A-9F2C-1D8B5E6A7F0C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; Source paths in [Files] are interpreted relative to SourceDir, which
; defaults to the directory containing the .iss file. We set it to the
; repo root one level up so we can reference "publish\..." and
; "installer\bundle\..." with sane paths.
SourceDir=..
; Per-machine install - needed because NetBird MSI installs a kernel
; driver which requires admin rights regardless.
PrivilegesRequired=admin
; Allow per-user fallback if the user can't elevate (then NetBird
; install will fail with a clear error, but the tracker still installs).
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=installer\dist
OutputBaseFilename=BCA-Tracker-Setup-{#AppVersion}
SetupIconFile=
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Allow the same setup to upgrade an older install in-place. Inno
; matches by AppId so the GUID above is the stable identity.
UsePreviousAppDir=yes
UsePreviousGroup=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Close the running tracker before upgrading so we can overwrite the exe.
CloseApplications=yes
RestartApplications=no
; Show the license / readme step only if the user has files for them.
DisableProgramGroupPage=yes
DisableReadyPage=no
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german";  MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "installnetbird"; Description: "Install NetBird (required for lobby hosting/joining)"; GroupDescription: "Dependencies:"; Flags: checkedonce

[Files]
; The tracker itself - SingleFile-published, so just one .exe.
Source: "publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Bundled NetBird installer. Stays in {app} so the tracker's own
; auto-install path (AppContext.BaseDirectory + "netbird_installer.msi")
; finds it if NetBird gets uninstalled later and the tracker needs to
; reinstall it on next launch.
Source: "installer\bundle\netbird_installer.msi"; DestDir: "{app}"; Flags: ignoreversion
; Optional: include LICENSE / README if present. Comment out if you
; don't have these yet.
; Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Install NetBird as a post-install step.
;
; Flags explained:
;   /i         install (vs /x uninstall)
;   /qb!       basic UI with progress bar, no cancel button, no prompts.
;              /qn (fully silent) sometimes fails with NetBird because
;              its installer wants to display the kernel-driver
;              consent prompt; /qb keeps things UI-light but still
;              lets necessary dialogs surface.
;   /norestart never auto-reboot (we'll just nag the user if needed)
;   /l*v       verbose log to %TEMP% so we can diagnose if it fails
;
; msiexec exit codes we accept as success:
;   0    - success
;   3010 - success but reboot required
Filename: "msiexec.exe"; \
  Parameters: "/i ""{app}\netbird_installer.msi"" /qb! /norestart /l*v ""{tmp}\netbird_install.log"""; \
  StatusMsg: "Installing NetBird (this may take a minute)..."; \
  Flags: waituntilterminated; \
  Tasks: installnetbird

; Launch the tracker after install if the user wants. The
; runasoriginaluser flag is important: the installer runs elevated (for
; NetBird), but the tracker itself only needs normal user rights, so
; we drop privileges back down for the launch. Without this flag,
; CreateProcess fails with "elevation required" (code 740) because
; our app.manifest doesn't request admin.
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Note: we don't auto-uninstall NetBird on tracker uninstall. Other
; apps may use NetBird, and reinstalling it later is heavy. Users
; can uninstall it manually from Add/Remove Programs if they want.

[Code]
// Show a friendly message if the user opts out of NetBird install,
// reminding them lobby features will be limited.
// Also verifies NetBird actually got installed when the task was
// selected, since a silent msiexec failure leaves the user with a
// non-functional tracker for lobby features.
procedure CurStepChanged(CurStep: TSetupStep);
var
  NetBirdExePath: string;
begin
  if CurStep <> ssPostInstall then Exit;

  if not WizardIsTaskSelected('installnetbird') then
  begin
    MsgBox(
      'NetBird was not installed. Lobby hosting and joining will not work ' +
      'until NetBird is installed. You can install it later by running ' +
      'netbird_installer.msi from the BCA-Tracker install folder.',
      mbInformation, MB_OK);
    Exit;
  end;

  // NetBird's MSI installs to "Program Files\NetBird" by default.
  // If we don't find it there, the msiexec call probably failed.
  NetBirdExePath := ExpandConstant('{commonpf}\NetBird\netbird.exe');
  if not FileExists(NetBirdExePath) then
  begin
    MsgBox(
      'NetBird install may have failed - netbird.exe was not found at ' +
      NetBirdExePath + #13#10 + #13#10 +
      'Lobby hosting and joining will not work until NetBird is installed. ' +
      'You can try installing it manually by running ' +
      '"netbird_installer.msi" from the BCA-Tracker install folder, or ' +
      'check %TEMP%\netbird_install.log for details.',
      mbError, MB_OK);
  end;
end;
