; ScreenSub AI - Inno Setup Script
; Windows Realtime Subtitle Translator Beta Installer
; Target: Windows 10 / 11 x64

#define MyAppName "ScreenSub AI"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0-beta.2"
#endif
#define MyAppPublisher "ScreenSub AI Team"
#define MyAppURL "https://github.com/ScreenSubAI"
#define MyAppExeName "ScreenSub.Desktop.exe"
#ifndef SelfContainedBuild
  #define SelfContainedBuild "true"
#endif

[Setup]
AppId={{C8C3F2D9-4EAF-469B-BDD7-67467536B1D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\docs\THIRD_PARTY_LICENSES.txt
OutputDir=..\dist
OutputBaseFilename=ScreenSubAI_v{#MyAppVersion}_Setup_x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\ScreenSubAI\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*__pycache__*,*.pyc,*.pdb"
Source: "..\workers\paddle-ocr\requirements.txt"; DestDir: "{app}\workers\paddle-ocr"; Flags: ignoreversion
Source: "..\workers\translation\requirements.txt"; DestDir: "{app}\workers\translation"; Flags: ignoreversion
Source: "..\docs\THIRD_PARTY_LICENSES.txt"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\INSTALL.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\MODEL_GUIDE.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\installer\setup_python_env.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}\Verify Python AI Environment"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -File ""{app}\setup_python_env.ps1"" -VerifyOnly"
Name: "{autoprograms}\{#MyAppName}\Setup Python AI Dependencies"; Filename: "powershell.exe"; Parameters: "-NoExit -ExecutionPolicy Bypass -File ""{app}\setup_python_env.ps1"""
Name: "{autoprograms}\{#MyAppName}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function HasPythonVersionInRegistry(RootKey: Integer; Ver: string): Boolean;
var
  InstallPath: string;
begin
  Result := False;
  if RegQueryStringValue(RootKey, 'Software\Python\PythonCore\' + Ver + '\InstallPath', 'ExecutablePath', InstallPath) then
  begin
    if FileExists(InstallPath) then
    begin
      Result := True;
      Exit;
    end;
  end;
  if RegQueryStringValue(RootKey, 'Software\Python\PythonCore\' + Ver + '\InstallPath', '', InstallPath) then
  begin
    if FileExists(AddBackslash(InstallPath) + 'python.exe') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function HasCompatiblePython(CheckAppDir: Boolean): Boolean;
begin
  Result := FileExists(ExpandConstant('{src}\python\python.exe')) or
            (CheckAppDir and FileExists(ExpandConstant('{app}\python\python.exe'))) or
            FileExists('C:\PyThon\python.exe') or
            FileExists('C:\Program Files\Python312\python.exe') or
            FileExists('C:\Program Files\Python311\python.exe') or
            FileExists('C:\Program Files\Python310\python.exe') or
            FileExists(ExpandConstant('{localappdata}\Programs\Python\Python312\python.exe')) or
            FileExists(ExpandConstant('{localappdata}\Programs\Python\Python311\python.exe')) or
            FileExists(ExpandConstant('{localappdata}\Programs\Python\Python310\python.exe')) or
            HasPythonVersionInRegistry(HKEY_CURRENT_USER, '3.12') or
            HasPythonVersionInRegistry(HKEY_CURRENT_USER, '3.11') or
            HasPythonVersionInRegistry(HKEY_CURRENT_USER, '3.10') or
            HasPythonVersionInRegistry(HKEY_LOCAL_MACHINE, '3.12') or
            HasPythonVersionInRegistry(HKEY_LOCAL_MACHINE, '3.11') or
            HasPythonVersionInRegistry(HKEY_LOCAL_MACHINE, '3.10');
end;

function HasDotNet10DesktopRuntime(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKEY_LOCAL_MACHINE, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  PythonFound: Boolean;
  Msg: string;
begin
  PythonFound := HasCompatiblePython(False);
  Msg := '';

#if SelfContainedBuild == "false"
  if not HasDotNet10DesktopRuntime() then
  begin
    Msg := Msg + '- Thieu .NET 10 Desktop Runtime (x64). Ban can cai .NET 10 Desktop Runtime de chay ung dung.' + #13#10 + #13#10;
  end;
#endif

  if not PythonFound then
  begin
    Msg := Msg + 'ScreenSub AI can Python 3.10 - 3.12 (hoac runtime python\ dat canh app) de chay Offline Neural Translation & RapidOCR.' + #13#10 +
                 'Trinh cai dat KHONG tu dong tai model hoac thu vien tu Internet khi chua co su dong y cua ban.' + #13#10 +
                 'Sau khi cai dat xong, ban co the chay "setup_python_env.ps1" de kiem tra va cai thu vien neu muon dung Offline Mode.';
  end;

  if (Msg <> '') and (not WizardSilent()) then
  begin
    MsgBox(Msg, mbInformation, MB_OK);
  end;

  Result := True;
end;

// Preserve user models and configuration during uninstall
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // %LocalAppData%\ScreenSubAI\Models and %LocalAppData%\ScreenSubAI\Settings
    // are deliberately preserved so downloaded/verified local models are not lost.
  end;
end;
