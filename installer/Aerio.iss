#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif

#ifndef AppSourceDir
  #define AppSourceDir "..\release\Aerio-win-x64"
#endif

#ifndef WebView2InstallerPath
  #define WebView2InstallerPath "..\release\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#endif

[Setup]
AppId={{A7A78157-8D18-4AAE-A13A-E93C8CF77E33}
AppName=Aerio
AppVersion={#AppVersion}
AppPublisher=vanie2300
AppPublisherURL=https://github.com/vanie2300/Aerio
AppSupportURL=https://github.com/vanie2300/Aerio/issues
AppUpdatesURL=https://github.com/vanie2300/Aerio/releases
DefaultDirName={autopf}\Aerio
DefaultGroupName=Aerio
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\release
OutputBaseFilename=Aerio-Setup-win-x64
SetupIconFile=..\UI\Assets\Aerio.ico
UninstallDisplayIcon={app}\Aerio.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Messages]
WelcomeLabel2=This installer includes the Microsoft Edge WebView2 Runtime and installs it if needed.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebView2InstallerPath}"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\Aerio"; Filename: "{app}\Aerio.exe"
Name: "{autodesktop}\Aerio"; Filename: "{app}\Aerio.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 Runtime..."; Flags: runhidden waituntilterminated
Filename: "{app}\Aerio.exe"; Description: "Launch Aerio"; Flags: postinstall nowait skipifsilent
