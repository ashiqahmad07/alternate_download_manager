; ADM's per-user installer, built by scripts\build-installer.ps1 with Inno Setup 6:
;   ISCC /DAppVersion=1.2.3 /DSource=<folder publish.ps1 made> /DTools=<folder of yt-dlp, deno and ffmpeg> /O<output folder> installer\ADM.iss
; scripts\test-installer.ps1 installs, upgrades and uninstalls what it makes.

#ifndef AppVersion
  #error Pass the version: /DAppVersion=1.2.3
#endif
#ifndef Source
  #error Pass the published folder: /DSource=<folder>
#endif
#ifndef Tools
  #error Pass the video tools' folder, which scripts\fetch-video-tools.ps1 fills: /DTools=<folder>
#endif

[Setup]
AppId={{4A719596-AE3E-4AF6-A276-515B2060EBF0}
AppName=Alternate Download Manager
AppVersion={#AppVersion}
AppVerName=Alternate Download Manager {#AppVersion}
AppPublisher=ashiqahmad07
VersionInfoVersion={#AppVersion}
; For the current user only, so no administrator rights: %LOCALAPPDATA%\Programs\ADM.
PrivilegesRequired=lowest
DefaultDirName={autopf}\ADM
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 8 runs on Windows 10 1607 and later; on anything older, setup says so instead of installing an ADM that won't start.
MinVersion=10.0.14393
; A running download mustn't be killed, so setup and the uninstaller wait for ADM to be closed.
AppMutex=ADM.SingleInstance
; A native host a browser is running is closed, as the browser starts it again when it needs it.
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
SetupIconFile=..\src\ADM.App\Assets\ADM.ico
UninstallDisplayIcon={app}\ADM.exe
UninstallDisplayName=Alternate Download Manager
OutputBaseFilename=ADM-Setup-{#AppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[InstallDelete]
; An update's extension replaces the old one whole, so no old script is left beside the new.
Type: filesandordirs; Name: "{app}\extension"
Type: filesandordirs; Name: "{app}\extension-firefox"

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The video tools a site's video needs, where ADM's own "Set up video downloads" puts them, so nobody is asked to fetch
; them. Only where none is there yet: ADM keeps its own yt-dlp up to date, and setup's mustn't replace a newer one.
Source: "{#Tools}\*"; DestDir: "{localappdata}\ADM\tools"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\Alternate Download Manager"; Filename: "{app}\ADM.exe"
Name: "{autoprograms}\Add ADM to your browser"; Filename: "{app}\Browser setup.html"

[Registry]
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.adm.host"; ValueType: string; ValueData: "{app}\com.adm.host.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.adm.host"; ValueType: string; ValueData: "{app}\com.adm.host.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\com.adm.host"; ValueType: string; ValueData: "{app}\com.adm.host.firefox.json"; Flags: uninsdeletekey

[UninstallDelete]
; A language file someone added goes with the ones ADM brought.
Type: filesandordirs; Name: "{app}\Languages"
; The video tools ADM fetched on first use (yt-dlp, Deno, ffmpeg) are programs, not the user's data, so they go too.
Type: filesandordirs; Name: "{localappdata}\ADM\tools"
Type: dirifempty; Name: "{app}"

[Run]
Filename: "{app}\ADM.exe"; Description: "Start Alternate Download Manager"; Flags: nowait postinstall skipifsilent
; The extension is added by hand until it is in the browsers' stores; this page walks through it with the folder to pick.
Filename: "{app}\Browser setup.html"; Description: "Show how to add ADM to your browser"; Flags: shellexec nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

{ Start with Windows names this copy of ADM in the Run value "ADM". Once it is gone, Windows would try to start
  a missing program at every sign-in. A value naming another copy, such as a build from source, is left. }
procedure RemoveStartupEntry();
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, 'ADM', Command) and
     (Pos(Lowercase(ExpandConstant('{app}\ADM.exe')), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, RunKey, 'ADM');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveStartupEntry();
  { Asked only when someone is there to answer: a silent uninstall keeps the list. }
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also delete ADM''s download list, settings and logs?' + #13#10#13#10 +
              'Files you downloaded are kept either way.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\ADM'), True, True, True);
end;
