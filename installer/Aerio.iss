; Aerio Windows installer (Inno Setup 6.3+).
;
; Build modes:
;   Online (default)  : framework-dependent app payload. The .NET 10 Desktop
;                       Runtime and WebView2 Runtime are detected and downloaded
;                       during setup only if missing.
;   Offline           : define the "OfflineInstaller" preprocessor symbol to
;                       bundle a self-contained app payload plus the offline
;                       WebView2 Runtime installer.
;
; Example:
;   ISCC.exe /DAppVersion=1.4.0 /DAppSourceDir=..\release\Aerio-win-x64 /O..\release installer\Aerio.iss
;   ISCC.exe /DAppVersion=1.4.0 /DAppSourceDir=..\release\Aerio-win-x64-offline /DWebView2InstallerPath=..\release\MicrosoftEdgeWebView2RuntimeInstallerX64.exe /DOfflineInstaller /O..\release installer\Aerio.iss

#ifndef AppVersion
  #define AppVersion "1.4.0"
#endif

#ifdef OfflineInstaller
  #define OutputName "Aerio-Setup-offline-win-x64"
  #ifndef AppSourceDir
    #define AppSourceDir "..\release\Aerio-win-x64-offline"
  #endif
  #ifndef WebView2InstallerPath
    #define WebView2InstallerPath "..\release\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
  #endif
#else
  #define OutputName "Aerio-Setup-win-x64"
  #ifndef AppSourceDir
    #define AppSourceDir "..\release\Aerio-win-x64"
  #endif
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
OutputBaseFilename={#OutputName}
SetupIconFile=..\UI\Assets\Aerio.ico
UninstallDisplayIcon={app}\Aerio.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Messages]
#ifdef OfflineInstaller
WelcomeLabel2=This installer includes the Microsoft Edge WebView2 Runtime and installs it if needed.
#else
WelcomeLabel2=Required components (.NET 10 Desktop Runtime and WebView2 Runtime) are checked automatically and downloaded during setup only if missing. An internet connection may be required.
#endif

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef OfflineInstaller
Source: "{#WebView2InstallerPath}"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{autoprograms}\Aerio"; Filename: "{app}\Aerio.exe"
Name: "{autodesktop}\Aerio"; Filename: "{app}\Aerio.exe"; Tasks: desktopicon

[Run]
#ifndef OfflineInstaller
Filename: "{tmp}\windowsdesktop-runtime-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the .NET 10 Desktop Runtime..."; Flags: runhidden waituntilterminated; Check: NeedsDotNetDesktop
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 Runtime..."; Flags: runhidden waituntilterminated; Check: NeedsWebView2
#else
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 Runtime..."; Flags: runhidden waituntilterminated; Check: NeedsWebView2
#endif
Filename: "{app}\Aerio.exe"; Description: "Launch Aerio"; Flags: postinstall nowait skipifsilent

[Code]
const
  WebView2KeyHKLM = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2KeyHKCU = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2RuntimeInstalled: Boolean;
var
  pv: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, WebView2KeyHKLM, 'pv', pv) or
     RegQueryStringValue(HKCU, WebView2KeyHKCU, 'pv', pv)) and
    (pv <> '') and (pv <> '0.0.0.0');
end;

function DotNetDesktop10Installed: Boolean;
var
  base: String;
  findRec: TFindRec;
begin
  // Primary: documented registry detection for the machine-wide .NET Desktop
  // Runtime (value "10.0" under the Microsoft.WindowsDesktop.App shared framework).
  if RegValueExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', '10.0') then begin
    Result := True;
    Exit;
  end;

  // Fallback: look for any 10.x folder in the default machine-wide install
  // location ({commonpf} resolves to the 64-bit Program Files in this install mode).
  Result := False;
  base := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(base) and FindFirst(base + '\10.*', findRec) then begin
    try
      repeat
        if (findRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or not FindNext(findRec);
    finally
      FindClose(findRec);
    end;
  end;
end;

function NeedsWebView2: Boolean;
begin
  Result := not WebView2RuntimeInstalled;
end;

function NeedsDotNetDesktop: Boolean;
begin
  Result := not DotNetDesktop10Installed;
end;

#ifndef OfflineInstaller
var
  DownloadPage: TDownloadWizardPage;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  requireDotNet, requireWebView2: Boolean;
begin
  Result := True;
  if CurPageID = wpReady then begin
    requireDotNet := NeedsDotNetDesktop;
    requireWebView2 := NeedsWebView2;
      if requireDotNet or requireWebView2 then begin
      DownloadPage.Clear;
      if requireDotNet then
        DownloadPage.Add('https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe', 'windowsdesktop-runtime-win-x64.exe', '');
      if requireWebView2 then
        DownloadPage.Add('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe', '');
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
        except
          if DownloadPage.AbortedByUser then
            Log('Runtime download aborted by user.')
          else
            SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
          Result := False;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;
#endif
