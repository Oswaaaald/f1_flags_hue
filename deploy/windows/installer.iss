#ifndef BuildRoot
  #define BuildRoot "..\..\artifacts\win-x64\desktop"
#endif
#ifndef Arch
  #define Arch "x64compatible"
#endif
#ifndef AppVersion
  #error AppVersion must be passed by build.ps1
#endif
#ifndef Runtime
  #error Runtime must be passed by build.ps1
#endif
[Setup]
AppId=F1HueSync
AppName=F1 Hue Sync
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\F1Hue
DefaultGroupName=F1 Hue Sync
PrivilegesRequired=lowest
ArchitecturesAllowed={#Arch}
ArchitecturesInstallIn64BitMode={#Arch}
OutputDir=..\..\artifacts
OutputBaseFilename=F1Hue-{#AppVersion}-{#Runtime}-Setup
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\F1Hue.exe
CloseApplications=yes
[Files]
Source: "{#BuildRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\F1 Hue Sync"; Filename: "{app}\F1Hue.exe"
Name: "{autodesktop}\F1 Hue Sync"; Filename: "{app}\F1Hue.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le bureau"; Flags: unchecked
Name: "autostart"; Description: "Lancer F1 Hue à l’ouverture de session"; Flags: unchecked
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "F1Hue"; ValueData: """{app}\F1Hue.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue
[Run]
Filename: "{app}\F1Hue.exe"; Description: "Ouvrir F1 Hue Sync"; Flags: nowait postinstall skipifsilent
