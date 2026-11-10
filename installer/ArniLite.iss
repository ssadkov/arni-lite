; Per-user Setup for the net48 Arni Lite build.
; Keep AppVersion in step with src/ArniLite.App/ArniLite.App.csproj.

#define AppVersion "0.1.0"
#define SourceDir "..\src\ArniLite.App\bin\Release\net48"

[Setup]
AppId={{9A38DA0B-ECAE-473E-A798-85B32D3D0F0B}
AppName=Arni Lite
AppVersion={#AppVersion}
AppVerName=Arni Lite {#AppVersion}
AppPublisher=Arnion
AppPublisherURL=https://github.com/ssadkov/arni-lite
AppSupportURL=https://github.com/ssadkov/arni-lite/issues
AppUpdatesURL=https://github.com/ssadkov/arni-lite/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Arnion
VersionInfoDescription=Arni Lite Setup
VersionInfoProductName=Arni Lite
DefaultDirName={localappdata}\Programs\Arni Lite
DefaultGroupName=Arni Lite
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ArniLiteSetup-{#AppVersion}
SetupIconFile=..\src\ArniLite.App\ArniLite.ico
UninstallDisplayIcon={app}\ArniLite.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
AppMutex=Local\ArniLite.SingleInstance
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.Launch=Запустить Arni Lite
english.Launch=Launch Arni Lite
russian.NeedNet48=Для Arni Lite нужен .NET Framework 4.8 или новее. Установите его с сайта Microsoft и запустите Setup ещё раз.%n%nhttps://dotnet.microsoft.com/download/dotnet-framework/net48
english.NeedNet48=Arni Lite needs .NET Framework 4.8 or newer. Install it from Microsoft, then run Setup again.%n%nhttps://dotnet.microsoft.com/download/dotnet-framework/net48

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Arni Lite"; Filename: "{app}\ArniLite.exe"
Name: "{autodesktop}\Arni Lite"; Filename: "{app}\ArniLite.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ArniLite.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  Release: Cardinal;
begin
  Release := 0;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040) then
  begin
    Result := True;
  end
  else
  begin
    MsgBox(ExpandConstant('{cm:NeedNet48}'), mbCriticalError, MB_OK);
    Result := False;
  end;
end;
