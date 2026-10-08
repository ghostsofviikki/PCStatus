; PCStatus installer (Inno Setup 6). Build with build.ps1, which publishes the exes first.

#define AppName "PCStatus"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "PCStatus.exe"

; PawnIO is downloaded at install time (not bundled) and verified against this hash.
#define PawnIOUrl "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"
#define PawnIOSha256 "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032"

[Setup]
AppId={{1D75C28F-8DBD-4662-B072-4564B8CB2F30}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ghostsofviikki
AppPublisherURL=https://github.com/ghostsofviikki/PCStatus
AppSupportURL=https://github.com/ghostsofviikki/PCStatus/issues
LicenseFile=..\LICENSE
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; 64-bit Windows 10 1809+ / Windows 11, Intel/AMD (x64) or ARM64.
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=PCStatusSetup-{#AppVersion}
SetupIconFile=..\PCStatus.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "autostart"; Description: "Start PCStatus automatically when signing in"
Name: "pawnio"; Description: "Install the PawnIO driver for CPU temperature (downloaded from github.com/namazso)"; Check: not IsPawnIOInstalled
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\publish\win-arm64\{#AppExe}"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion
Source: "..\publish\win-x64\{#AppExe}"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--enable-autostart"; Tasks: autostart; Flags: runhidden waituntilterminated; StatusMsg: "Registering autostart..."
; With autostart, start through the task: it runs in the signed-in user's session, elevated without a UAC prompt.
; runascurrentuser: postinstall entries default to the unelevated original user, who gets "Access is denied" on /Run.
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#AppName}"""; Tasks: autostart; Description: "Launch {#AppName}"; Flags: postinstall runhidden skipifsilent runascurrentuser
Filename: "{app}\{#AppExe}"; Tasks: not autostart; Description: "Launch {#AppName}"; Flags: postinstall nowait shellexec skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{app}\{#AppExe}"; Parameters: "--disable-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  PawnIODownloaded: Boolean;

function IsPawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the PawnIO driver...', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#PawnIOUrl}', 'PawnIO_setup.exe', '{#PawnIOSha256}');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        PawnIODownloaded := True;
      except
        SuppressibleMsgBox('Could not download the PawnIO driver, so CPU temperature will not be shown.' + #13#10 +
          'PCStatus will still be installed. You can install PawnIO later from https://pawnio.eu.' + #13#10#13#10 +
          GetExceptionMessage, mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // Upgrade: stop a running instance so the exe can be replaced.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep = ssPostInstall) and PawnIODownloaded then
  begin
    WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver...';
    if not Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), '-install -silent', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      SuppressibleMsgBox('The PawnIO driver installation did not complete (code ' + IntToStr(Code) + '). ' +
        'CPU temperature will not be shown.', mbInformation, MB_OK, IDOK);
  end;
end;
