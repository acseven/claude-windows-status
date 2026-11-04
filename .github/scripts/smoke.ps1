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
Invoke-Run 'stats'        @('--demo', '--style=bars', '--open-stats')     700
Invoke-Run 'notification' @('--demo=alert', '--style=bars')               480 12
Invoke-Run 'about'        @('--demo', '--style=bars', '--open-about')     700
# Portuguese (Portugal) interface
Invoke-Run 'flyout-pt'    @('--demo', '--style=bars', '--open-details', '--lang=pt-PT') 480
Invoke-Run 'stats-pt'     @('--demo', '--style=bars', '--open-stats', '--lang=pt-PT')   700
Invoke-Run 'notification-pt' @('--demo=alert', '--style=bars', '--lang=pt-PT')          480 12
Invoke-Run 'about-pt'     @('--demo', '--style=bars', '--open-about', '--lang=pt-PT')   700
Invoke-Run 'welcome'      @('--demo', '--style=bars', '--welcome')       700
Invoke-Run 'welcome-pt'   @('--demo', '--style=bars', '--welcome', '--lang=pt-PT') 700
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

# Only one copy runs per Windows session: starting the same copy again, even several times at once,
# leaves exactly the first one running (the others show its flyout and exit).
Write-Host "== single instance"
$first = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
Start-Sleep -Seconds 6
$extra = 1..3 | ForEach-Object { Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru }
foreach ($e in $extra) { if (-not $e.WaitForExit(15000)) { throw "single instance: a second copy kept running (PID $($e.Id))" } }
if ($first.HasExited) { throw 'single instance: the first copy exited when started again' }
$count = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue).Count
if ($count -ne 1) { throw "single instance: $count copies running" }
Stop-Process -Id $first.Id -Force
Start-Sleep -Seconds 2
Write-Host 'single instance OK'

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
    $installedExe = Join-Path $env:ProgramFiles 'Claude Status\ClaudeStatus.exe'
    if (-not (Test-Path $installedExe)) { throw "not installed: $installedExe" }
    $run = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ClaudeStatus -ErrorAction SilentlyContinue
    if (-not $run) { throw 'autostart entry missing' }
    Write-Host "installed: $installedExe; autostart: $($run.ClaudeStatus)"
    $p = Start-Process msiexec.exe -ArgumentList @('/x', "`"$Msi`"", '/qn') -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "silent uninstall failed: $($p.ExitCode)" }
    if (Test-Path $installedExe) { throw 'uninstall left the exe behind' }
    Write-Host 'silent install/uninstall OK'
}

# Policy "Do not allow users to quit" (DisableExit): quit paths are ignored; takeover and installer still work.
Write-Host "== DisableExit policy"
# Positive control: without the policy a plain close request (taskkill without /f) does close the app.
# Otherwise the "ignored under the policy" check below could pass without testing anything.
$ctl = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
Start-Sleep -Seconds 6
& taskkill /PID $ctl.Id 2>&1 | Write-Host
if (-not $ctl.WaitForExit(15000)) { Stop-Process -Id $ctl.Id -Force; throw 'control: a plain close request did not close the app, so the DisableExit check would prove nothing' }
Write-Host 'control: a plain close request closes the app (no policy)'
$polKey = 'HKLM:\SOFTWARE\Policies\ClaudeStatus'
try {
    New-Item -Path $polKey -Force | Out-Null
    Set-ItemProperty -Path $polKey -Name DisableExit -Value 1 -Type DWord
    $locked = Start-Process -FilePath $Exe -ArgumentList '--demo' -PassThru
    Start-Sleep -Seconds 6
    Start-Process -FilePath $Exe -ArgumentList '--quit' -Wait
    Start-Sleep -Seconds 4
    if ($locked.HasExited) { throw 'DisableExit: --quit closed the app' }
    Write-Host 'DisableExit: --quit ignored'

    # Taskkill without /f sends a plain close request.
    & taskkill /PID $locked.Id 2>&1 | Write-Host
    Start-Sleep -Seconds 4
    if ($locked.HasExited) { throw 'DisableExit: a plain close request closed the app' }
    Write-Host 'DisableExit: plain close request ignored'

    # A different copy still takes over.
    $otherDir2 = Join-Path $env:TEMP 'claudestatus-other2'
    New-Item -ItemType Directory -Force $otherDir2 | Out-Null
    $other2 = Join-Path $otherDir2 'ClaudeStatus.exe'
    Copy-Item $Exe $other2 -Force
    $taker = Start-Process -FilePath $other2 -ArgumentList '--demo' -PassThru
    if (-not $locked.WaitForExit(25000)) { throw 'DisableExit: takeover did not replace the running copy' }
    Start-Sleep -Seconds 4
    if ($taker.HasExited) { throw 'DisableExit: the taking-over copy exited' }
    Write-Host 'DisableExit: takeover works'

    if ($Msi) {
        # The installer must still be able to replace a running, locked copy (it ends the process itself).
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/qn') -Wait -PassThru
        if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { throw "DisableExit: silent install failed: $($p.ExitCode)" }
        if (-not $taker.WaitForExit(30000)) { throw 'DisableExit: installer did not end the running copy' }
        Write-Host 'DisableExit: installer ends the running copy'
        $u = Start-Process msiexec.exe -ArgumentList @('/x', "`"$Msi`"", '/qn') -Wait -PassThru
        if ($u.ExitCode -ne 0) { throw "DisableExit: silent uninstall failed: $($u.ExitCode)" }
    }
}
finally {
    Remove-Item -Path $polKey -Recurse -Force -ErrorAction SilentlyContinue
    # Leave the runner clean even if an assertion above failed after the install (1605 = not installed: fine).
    if ($Msi) { Start-Process msiexec.exe -ArgumentList @('/x', "`"$Msi`"", '/qn') -Wait -ErrorAction SilentlyContinue | Out-Null }
    Get-Process -Name ClaudeStatus -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host "Screenshots in $OutDir"

