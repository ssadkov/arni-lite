; Per-user Setup for the net48 Arni Lite build.
; Keep AppVersion in step with the numeric part of the application version.

#ifndef AppVersion
#define AppVersion "0.1.2"
#endif
#ifndef AppNumericVersion
#define AppNumericVersion "0.1.2"
#endif
#ifndef Arch
#define Arch "x64"
#endif
#if Arch == "x86"
#define BuildDir "..\dist\publish\x86"
#define SetupSuffix "-x86"
#else
#define BuildDir "..\dist\publish\x64"
#define SetupSuffix "-x64"
#endif

[Setup]
AppId={{9A38DA0B-ECAE-473E-A798-85B32D3D0F0B}
AppName=Arni Lite
AppVersion={#AppVersion}
AppVerName=Arni Lite {#AppVersion}
AppPublisher=Arnion
AppPublisherURL=https://github.com/ssadkov/arni-lite
AppSupportURL=https://github.com/ssadkov/arni-lite/issues
AppUpdatesURL=https://github.com/ssadkov/arni-lite/releases
VersionInfoVersion={#AppNumericVersion}.0
VersionInfoCompany=Arnion
VersionInfoDescription=Arni Lite Setup
VersionInfoProductName=Arni Lite
DefaultDirName={localappdata}\Programs\Arni Lite
DefaultGroupName=Arni Lite
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=ArniLiteSetup-{#AppVersion}{#SetupSuffix}
SetupIconFile=..\src\ArniLite.App\ArniLite.ico
UninstallDisplayIcon={app}\ArniLite.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
#if Arch == "x86"
ArchitecturesAllowed=x86compatible
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
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
Source: "{#BuildDir}\*.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\en\*.resources.dll"; DestDir: "{app}\en"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#BuildDir}\kk\*.resources.dll"; DestDir: "{app}\kk"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\Arni Lite"; Filename: "{app}\ArniLite.exe"
Name: "{autodesktop}\Arni Lite"; Filename: "{app}\ArniLite.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ArniLite.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent; Check: not IsAppUpdate
Filename: "{app}\ArniLite.exe"; Flags: nowait; Check: IsAppUpdate

[Code]
function IsAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:ARNIUPDATE|0}') = '1';
end;

function InitializeSetup: Boolean;
var
  Release: Cardinal;
  WaitCount: Integer;
begin
  Result := False;
  if IsAppUpdate then
  begin
    { The application saves its draft, starts Setup and exits normally. Never kill an agent turn. }
    WaitCount := 0;
    while CheckForMutexes('Local\ArniLite.SingleInstance') and (WaitCount < 300) do
    begin
      Sleep(200);
      WaitCount := WaitCount + 1;
    end;
    if CheckForMutexes('Local\ArniLite.SingleInstance') then Exit;
  end;
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
