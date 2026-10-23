# Runs the published app in demo mode on a CI Windows desktop and captures screenshots.
# Fails if the app exits (crashes) during a run.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Prefix = 'win',
    [string] $Msi
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Hide the taskbar search box (0 = hidden) and restart Explorer so the taskbar re-lays out.
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Search' -Name SearchboxTaskbarMode -Value 0 -Type DWord -Force
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 6
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer; Start-Sleep -Seconds 6 }

# Hosted runners start at 1024x768; try a common laptop/desktop resolution (best effort).
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Display {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string d, int m, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int f);
    public static int Set(int w, int h) {
        var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(null, -1, ref dm)) return -100;
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 0);
    }
}
'@
try { Write-Host "ChangeDisplaySettings 1920x1080 -> $([Display]::Set(1920, 1080))"; Start-Sleep -Seconds 3 } catch { Write-Host "Resolution change failed: $_" }

function Save-Screen([string] $name, [int] $height) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $full = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($full)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()
    $full.Save((Join-Path $OutDir "$Prefix-$name-full.png"))
    # Bottom-right corner: where the widget, tray and flyout live.
    $w = [Math]::Min(1000, $b.Width); $h = [Math]::Min($height, $b.Height)
    $crop = $full.Clone((New-Object System.Drawing.Rectangle ($b.Width - $w), ($b.Height - $h), $w, $h), $full.PixelFormat)
    $crop.Save((Join-Path $OutDir "$Prefix-$name.png"))
    $crop.Dispose(); $full.Dispose()
}

function Invoke-Run([string] $name, [string[]] $appArgs, [int] $height, [int] $wait = 8) {
    Write-Host "== $name : $($appArgs -join ' ')"
    $p = Start-Process -FilePath $Exe -ArgumentList $appArgs -PassThru
    Start-Sleep -Seconds $wait
    if ($p.HasExited) { throw "${name}: app exited with code $($p.ExitCode)" }
    Save-Screen $name $height
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 2
}

Invoke-Run 'bars'         @('--demo', '--style=bars')                     160
Invoke-Run 'compact'      @('--demo', '--style=compact')                  160
Invoke-Run 'flyout'       @('--demo', '--style=bars', '--open-details')   480
Invoke-Run 'notification' @('--demo=alert', '--style=bars')               480 12
Invoke-Run 'signed-out'   @('--style=bars')                               160
# Network path through the embedded browser (informational: CI machines may be challenged by Cloudflare).
Write-Host "== network self-test"
$net = Join-Path $OutDir "$Prefix-network.txt"
Start-Process -FilePath $Exe -ArgumentList "--selftest-network=`"$net`"" -Wait
if (Test-Path $net) { Write-Host "network: $(Get-Content $net -Raw)" } else { Write-Host 'network: no result' }

# --quit closes a running copy (single-instance signal).
Write-Host "== --quit"
$p = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
Start-Sleep -Seconds 6
Start-Process -FilePath $Exe -ArgumentList '--quit' -Wait
if (-not $p.WaitForExit(10000)) { throw '--quit did not close the running copy' }
Write-Host '--quit OK'

# A different copy (another file/version) takes over from the running one.
Write-Host "== takeover by a different copy"
$otherDir = Join-Path $env:TEMP 'claudestatus-other'
New-Item -ItemType Directory -Force $otherDir | Out-Null
$other = Join-Path $otherDir 'ClaudeStatus.exe'
Copy-Item $Exe $other -Force
$old = Start-Process -FilePath $other -ArgumentList '--demo' -PassThru
Start-Sleep -Seconds 6
$new = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
if (-not $old.WaitForExit(20000)) { throw 'the running copy was not replaced' }
Start-Sleep -Seconds 4
if ($new.HasExited) { throw "the new copy exited instead of taking over (code $($new.ExitCode))" }
Stop-Process -Id $new.Id -Force
Start-Sleep -Seconds 2
Write-Host 'takeover OK'

if ($Msi) {
    # Installer wizard: open it, capture the first page, cancel.
    Write-Host "== installer wizard"
    $ui = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"") -PassThru
    Start-Sleep -Seconds 10
    Save-Screen 'installer' 600
    Stop-Process -Id $ui.Id -Force -ErrorAction SilentlyContinue
    Get-Process msiexec -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 5

    # Silent install / uninstall, as GPO or Intune would do it, with a copy running: the installer
    # must close it (CloseApplication) instead of failing or leaving it running.
    Write-Host "== silent install over a running copy"
    # Same image name as the installed app (the installer matches running copies by name).
    $copyDir = Join-Path $env:TEMP 'claudestatus-smoke'
    New-Item -ItemType Directory -Force $copyDir | Out-Null
    $copy = Join-Path $copyDir 'ClaudeStatus.exe'
    Copy-Item $Exe $copy -Force
    $running = Start-Process -FilePath $copy -ArgumentList '--demo' -PassThru
    Start-Sleep -Seconds 6
    $log = Join-Path $OutDir "$Prefix-install.log"
    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/qn', '/l*v', "`"$log`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "silent install failed: $($p.ExitCode)" }
    if (-not $running.WaitForExit(15000)) { throw 'installer did not close the running copy' }
    Write-Host "running copy closed by the installer (exit code $($running.ExitCode))"
    $exe = Join-Path $env:ProgramFiles 'Claude Status\ClaudeStatus.exe'
    if (-not (Test-Path $exe)) { throw "not installed: $exe" }
    $run = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClaudeStatus -ErrorAction SilentlyContinue
    if (-not $run) { throw 'autostart entry missing' }
    Write-Host "installed: $exe; autostart: $($run.ClaudeStatus)"
    $p = Start-Process msiexec.exe -ArgumentList @('/x', "`"$Msi`"", '/qn') -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "silent uninstall failed: $($p.ExitCode)" }
    if (Test-Path $exe) { throw 'uninstall left the exe behind' }
    Write-Host 'silent install/uninstall OK'
}

Write-Host "Screenshots in $OutDir"

