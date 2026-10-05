; Compile only with a locally installed Inno Setup compiler. The release script supplies
; AppVersion, SourceDir, and OutputDir after it validates the self-contained payload.
#ifndef AppVersion
  #error AppVersion must be supplied by scripts\Publish-WindowsRelease.ps1.
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by scripts\Publish-WindowsRelease.ps1.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by scripts\Publish-WindowsRelease.ps1.
#endif

[Setup]
AppId={{8DFF6D6D-6678-4455-9B24-CEB32A1D854A}
AppName=YourSafe
AppVersion={#AppVersion}
AppPublisher=REPLACE_WITH_PUBLISHER
DefaultDirName={localappdata}\Programs\YourSafe
DefaultGroupName=YourSafe
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=YourSafe-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=YourSafe
UsePreviousAppDir=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\YourSafe"; Filename: "{app}\YourSafe.exe"

[Run]
Filename: "{app}\YourSafe.exe"; Description: "Launch YourSafe"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU32; Subkey: "Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill"; ValueType: string; ValueName: ""; ValueData: "{app}\yoursafe-native-chrome.json"
Root: HKCU32; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill"; ValueType: string; ValueName: ""; ValueData: "{app}\yoursafe-native-chrome.json"
Root: HKCU32; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill"; ValueType: string; ValueName: ""; ValueData: "{app}\yoursafe-native-edge.json"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if RegQueryStringValue(HKCU32, 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill', '', Value) and
      (CompareText(Value, ExpandConstant('{app}\yoursafe-native-chrome.json')) = 0) then
    begin
      RegDeleteValue(HKCU32, 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill', '');
      RegDeleteKeyIfEmpty(HKCU32, 'Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.yoursafe.autofill');
    end;
    if RegQueryStringValue(HKCU32, 'Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill', '', Value) and
      (CompareText(Value, ExpandConstant('{app}\yoursafe-native-chrome.json')) = 0) then
    begin
      RegDeleteValue(HKCU32, 'Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill', '');
      RegDeleteKeyIfEmpty(HKCU32, 'Software\Google\Chrome\NativeMessagingHosts\com.yoursafe.autofill');
    end;
    if RegQueryStringValue(HKCU32, 'Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill', '', Value) and
      (CompareText(Value, ExpandConstant('{app}\yoursafe-native-edge.json')) = 0) then
    begin
      RegDeleteValue(HKCU32, 'Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill', '');
      RegDeleteKeyIfEmpty(HKCU32, 'Software\Microsoft\Edge\NativeMessagingHosts\com.yoursafe.autofill');
    end;
  end;
end;

[UninstallDelete]
; Application binaries are removed by the uninstaller. Vault data intentionally lives in
; %LocalAppData%\PasswordTool, outside {app}, and is never removed by this installer.
