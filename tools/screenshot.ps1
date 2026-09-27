# Saves a picture of a window (by title substring) or of the whole screen. Used to check the desktop app.
# Usage: pwsh tools/screenshot.ps1 -Out shot.png [-Title "GPO Catalogue"]
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Title
)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
}
'@
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($Title) {
    $p = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$Title*" } | Select-Object -First 1
    if (-not $p) { throw "No window titled *$Title*" }
    [Win]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300
    $r = New-Object Win+RECT
    # The frame without the invisible resize border.
    if ([Win]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16) -ne 0) { [Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null }
    $bounds = [System.Drawing.Rectangle]::FromLTRB($r.Left, $r.Top, $r.Right, $r.Bottom)
}
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output $Out
