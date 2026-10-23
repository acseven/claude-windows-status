# Runs the published app in demo mode on a CI Windows desktop and captures screenshots.
# Fails if the app exits (crashes) during a run.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Prefix = 'win'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

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
Write-Host "Screenshots in $OutDir"

