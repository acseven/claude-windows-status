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

