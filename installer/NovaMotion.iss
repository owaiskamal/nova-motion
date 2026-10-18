; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 Novabit
;
; NOVA Motion installer (Inno Setup 6). Built by CI:
;   iscc /DAppVersion=0.2.0 installer\NovaMotion.iss
; Expects dist\ (dotnet publish output) and runtime\ (setup\Get-Runtime.ps1) to exist.
;
; Installs per user (no admin prompt) into %LOCALAPPDATA%\Programs\NOVA Motion, because the
; runtime folder must stay writable: TensorRT keeps its compiled engines there.

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{6C1E8A52-3B47-4E0F-9D6A-4E4F56414D31}
AppName=NOVA Motion
AppVersion={#AppVersion}
AppVerName=NOVA Motion {#AppVersion}
AppPublisher=Novabit
AppPublisherURL=https://github.com/owaiskamal/nova-motion
AppSupportURL=https://github.com/owaiskamal/nova-motion/issues
DefaultDirName={localappdata}\Programs\NOVA Motion
DefaultGroupName=NOVA Motion
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist-installer
OutputBaseFilename=NovaMotion-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\NovaMotion.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "..\runtime\*"; DestDir: "{app}\runtime"; Excludes: ".cache\*,trt-engines\*,*.log"; Flags: ignoreversion recursesubdirs
Source: "..\engine\*"; DestDir: "{app}\engine"; Flags: ignoreversion recursesubdirs
Source: "..\setup\*"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "..\setup-tensorrt.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\prepare-engines.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\benchmark.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\NOVA Motion"; Filename: "{app}\NovaMotion.exe"
Name: "{group}\Add NVIDIA TensorRT engine"; Filename: "{app}\setup-tensorrt.cmd"; WorkingDir: "{app}"
Name: "{group}\Uninstall NOVA Motion"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NOVA Motion"; Filename: "{app}\NovaMotion.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NovaMotion.exe"; Description: "Start NOVA Motion"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Engines TensorRT built on this PC, and the downloaded TensorRT pack
Type: filesandordirs; Name: "{app}\runtime"

[Registry]
; The app registers novamotion:// itself on start; remove it on uninstall
Root: HKCU; Subkey: "Software\Classes\novamotion"; Flags: uninsdeletekey dontcreatekey
