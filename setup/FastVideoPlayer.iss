; Setup for fast video player (project CuePlayer).
;
; Build with:  setup\build.cmd
; That publishes the program self-contained (no .NET needed on the target PC) and compiles this script.
; The finished installer lands in ..\publish\.
;
; Layout on the target machine:
;   C:\Program Files\fast video player     program, .NET runtime, libVLC, OMT, the control page (ui\), licences
;   C:\ProgramData\CuePlayer               cue list + settings (show.json), log, WebView2 profile - writable for every user
;   Playlists (*.cuelist)                  wherever the user saves them (default: Documents)
;
; Not shipped: the NDI runtime (NDI Tools from ndi.video, like Kiosk3 LiveIn) - without it only OMT is sent.
; Not needed: the Visual C++ runtime (libomt only uses the Universal C runtime of Windows 10/11, libVLC brings its own; checked with dumpbin).

#define AppName "fast video player"
#define AppPublisher "Peter Aellig"
#define AppExeName "CuePlayer.exe"

; The version comes from the built program (CuePlayer.csproj), so the installer cannot claim a different one.
#define AppExePath "app\CuePlayer.exe"
#if !FileExists(AppExePath)
  #error Publish first. Use build.cmd, which publishes and then compiles this.
#endif
#define AppVersion GetFileVersion(AppExePath)

[Setup]
AppId={{E0E3A695-5B75-4A0E-A474-4B4D31EBD7F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\publish
OutputBaseFilename=FastVideoPlayer-Setup-{#AppVersion}
SetupIconFile=..\logo\fast_video_player.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no

[Files]
; The published program, self-contained (ui\, licence\, libvlc\, libomt.dll and the icon are part of it).
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; settings, cue list, log and the WebView2 profile: shared by all users of the PC, writable without administrator rights
Name: "{commonappdata}\CuePlayer"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Licences"; Filename: "{app}\licence"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

{ The control window is a WebView2: part of Windows 11, on Windows 10 it may be missing. }
function WebView2Installed: Boolean;
var
  V: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', V) and (V <> '') and (V <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client, 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

{ NDI output needs the NDI runtime (not shipped); the program looks in the same places. }
function NdiInstalled: Boolean;
begin
  Result :=
    FileExists(AddBackslash(GetEnv('NDI_RUNTIME_DIR_V6')) + 'Processing.NDI.Lib.x64.dll') or
    FileExists(AddBackslash(GetEnv('NDI_RUNTIME_DIR_V5')) + 'Processing.NDI.Lib.x64.dll') or
    FileExists(ExpandConstant('{commonpf64}\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll')) or
    FileExists(ExpandConstant('{commonpf64}\NDI\NDI 6 Tools\Runtime\Processing.NDI.Lib.x64.dll'));
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  if not WebView2Installed then
    Result := MsgBox(
      'The Microsoft Edge WebView2 runtime was not found on this PC.' + #13#10#13#10 +
      'fast video player shows its control window with it. It is part of Windows 11; on Windows 10 install it from' + #13#10 +
      'Microsoft ("WebView2 Runtime", Evergreen) before starting the program.' + #13#10#13#10 +
      'Continue anyway?',
      mbConfirmation, MB_YESNO) = IDYES;

  if Result and not NdiInstalled then
    MsgBox(
      'The NDI runtime was not found on this PC.' + #13#10#13#10 +
      'fast video player then sends OMT only. For NDI install NDI Tools (or the NDI Runtime) from ndi.video;' + #13#10 +
      'the program finds it at its next start.',
      mbInformation, MB_OK);
end;

{ An update replaces the program files: a running player has to let go of them first. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1000);
  end;
end;
