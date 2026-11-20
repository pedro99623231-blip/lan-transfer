; Instalador do LAN Transfer (Inno Setup 6.3+)
#define AppName "LAN Transfer"
#define AppVersion "5.0.0"
#define AppExe "LANTransfer.exe"

[Setup]
AppId={{6B2E7C51-4F0A-4D8B-9E21-7A3C5D1F0B42}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Pedro
DefaultDirName={autopf}\LAN Transfer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=LANTransfer-Setup-{#AppVersion}
SetupIconFile=WiFiTransfer\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
UsePreviousAppDir=no
UsePreviousGroup=no

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na Área de Trabalho"; GroupDescription: "Atalhos:"
Name: "contextmenu"; Description: "Adicionar ""Disponibilizar no LAN Transfer"" ao clicar com o botão direito em arquivos e pastas"; GroupDescription: "Atalhos:"
Name: "sendto"; Description: "Adicionar ""Disponibilizar na rede"" ao menu ""Enviar para"" do Explorer"; GroupDescription: "Atalhos:"

[InstallDelete]
; Remove a instalação com o nome antigo (Wi-Fi Transfer)
Type: filesandordirs; Name: "{autopf}\Wi-Fi Transfer"
Type: filesandordirs; Name: "{autoprograms}\Wi-Fi Transfer"
Type: files; Name: "{autodesktop}\Wi-Fi Transfer.lnk"
Type: files; Name: "{usersendto}\Wi-Fi Transfer*.lnk"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{usersendto}\{#AppName} (disponibilizar na rede)"; Filename: "{app}\{#AppExe}"; Tasks: sendto

[Registry]
; Menu do botão direito no Explorer (arquivos e pastas)
Root: HKCR; Subkey: "*\shell\LANTransfer"; ValueType: string; ValueName: ""; ValueData: "Disponibilizar no LAN Transfer"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCR; Subkey: "*\shell\LANTransfer"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKCR; Subkey: "*\shell\LANTransfer\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
Root: HKCR; Subkey: "Directory\shell\LANTransfer"; ValueType: string; ValueName: ""; ValueData: "Disponibilizar no LAN Transfer"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCR; Subkey: "Directory\shell\LANTransfer"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKCR; Subkey: "Directory\shell\LANTransfer\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: contextmenu
; "Iniciar com o Windows" é ligado pelo próprio app; aqui só garantimos a limpeza ao desinstalar
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "LAN Transfer"; Flags: dontcreatekey uninsdeletevalue

[Run]
; Libera o app no Firewall do Windows (TCP do servidor e UDP da descoberta), em qualquer perfil de rede
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Wi-Fi Transfer"""; Flags: runhidden; StatusMsg: "Configurando o Firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "DelFw"
