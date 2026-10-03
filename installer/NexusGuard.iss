; Instalador do NexusGuard (Inno Setup 6)
; Compilar: iscc installer\NexusGuard.iss  (depois de .\build.ps1 -SelfContained)

#define AppName        "NexusGuard"

; A versao pode vir do compilador (iscc /DAppVersion=1.2.3), para cada release
; levar a sua. Sem isso, assume a versao de desenvolvimento.
#ifndef AppVersion
  #define AppVersion   "1.0.0"
#endif
#define AppPublisher   "NexusGuard"
#define AppExe         "NexusGuard.exe"
#define SourceDir      "..\dist"

[Setup]
AppId={{B7E3A1C4-9F52-4D18-9A6E-2C4F7D8B1E90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\src\NexusGuard\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
LicenseFile=LICENSE.txt
; Sem ofertas de terceiros: o instalador traz apenas o NexusGuard.

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; Apanha quaisquer ficheiros extra de uma publicação não-single-file.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist; Excludes: "*.pdb,*.xml,{#AppExe},{#AppName}-Setup-*.exe"

[Dirs]
; Dados partilhados: quarentena, registos e relatórios.
Name: "{commonappdata}\{#AppName}"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\Quarantine"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\Logs"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\Reports"; Permissions: users-modify
Name: "{commonappdata}\{#AppName}\RegistryBackups"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar o {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; runasoriginaluser: a app arranca na conta de quem iniciou sessao, nao na do administrador.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  KeepDataPage: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  KeepDataPage := nil;
end;

// Na desinstalação, pergunta se mantém a quarentena e os relatórios.
function InitializeUninstall(): Boolean;
begin
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\{#AppName}');

    if DirExists(DataDir) then
    begin
      if MsgBox('Remover também a quarentena, os registros e os relatórios do NexusGuard?' + #13#10 +
                'Os arquivos em quarentena serão perdidos de forma definitiva.',
                mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;

// O NexusGuard precisa do .NET apenas quando publicado sem o runtime incluído.
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
