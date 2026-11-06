; Aerio Windows installer (Inno Setup 6.3+).
;
; Build modes:
;   Online (default)  : framework-dependent app payload. The .NET 10 Desktop
;                       Runtime and WebView2 Runtime are detected and downloaded
;                       during setup only if missing. Downloaded runtime
;                       installers are Authenticode-verified (Microsoft
;                       signature) before they are executed.
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

  if RegValueExists(HKCU, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', '10.0') then begin
    Result := True;
    Exit;
  end;

  // Check machine-wide and per-user installations, including dotnet-install scripts.
  Result := False;
  base := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(base) then begin
    if FindFirst(base + '\10.*', findRec) then begin
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

  if Result then
    Exit;

  base := ExpandConstant('{userprofile}\.dotnet\shared\Microsoft.WindowsDesktop.App');
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

{ Verifies that a downloaded runtime installer carries a valid Authenticode
  signature from Microsoft before it is ever executed. This mirrors the
  signature check the CI workflow performs on the offline-bundled WebView2
  installer, and closes the gap where a compromised network path (TLS
  interception proxy, hijacked redirect) could substitute a binary that setup
  would otherwise run hidden with admin rights.

  Returns True only when:
    - the file's Authenticode signature status is "valid", and
    - the signer certificate subject contains "Microsoft Corporation".
  Any other outcome (unsigned, hash-invalid, untrusted root, wrong publisher)
  returns False so the caller can abort the update. }
function VerifyDownloadedRuntimeSignature(const FilePath: String): Boolean;
var
  Params: String;
  ResultCode: Integer;
begin
  Params :=
    '-NoProfile -NonInteractive -Command ' +
    '"$sig = Get-AuthenticodeSignature -LiteralPath ''" + FilePath + '''; ' +
    'if ($sig.Status -ne ''Valid'') { exit 1 }; ' +
    'if ($null -eq $sig.SignerCertificate) { exit 2 }; ' +
    'if ($sig.SignerCertificate.Subject -notlike ''*Microsoft Corporation*'') { exit 3 }; ' +
    'exit 0"';
  Result :=
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if not Result then
    Log(Format('Signature verification failed for %s (PowerShell exit code %d)', [FilePath, ResultCode]));
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
          // Verify Authenticode signatures before either runtime installer is
          // executed. A failed check aborts the update; the downloaded files
          // are left in {tmp}, which Inno Setup cleans up automatically.
          if requireDotNet and (not VerifyDownloadedRuntimeSignature(ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'))) then begin
            SuppressibleMsgBox('The downloaded .NET Desktop Runtime installer failed its digital signature check and was not run. Setup cannot continue.', mbCriticalError, MB_OK, IDOK);
            Result := False;
          end;
          if Result and requireWebView2 and (not VerifyDownloadedRuntimeSignature(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'))) then begin
            SuppressibleMsgBox('The downloaded WebView2 Runtime installer failed its digital signature check and was not run. Setup cannot continue.', mbCriticalError, MB_OK, IDOK);
            Result := False;
          end;
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
