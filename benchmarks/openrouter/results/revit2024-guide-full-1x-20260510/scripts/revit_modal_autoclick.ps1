Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class RevitModalClickerNative {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
}
'@

$BM_CLICK = 0x00F5

function U([int[]]$Codes) {
  return -join ($Codes | ForEach-Object { [char]$_ })
}

function Get-WindowTextValue([IntPtr]$Handle) {
  $sb = New-Object System.Text.StringBuilder 1024
  [void][RevitModalClickerNative]::GetWindowText($Handle, $sb, $sb.Capacity)
  return $sb.ToString()
}

function Get-ChildTextRows([IntPtr]$Parent) {
  $rows = New-Object System.Collections.Generic.List[object]
  $childCb = [RevitModalClickerNative+EnumWindowsProc]{
    param([IntPtr]$Child, [IntPtr]$LParam)
    $text = Get-WindowTextValue $Child
    if ($text) {
      $rows.Add([pscustomobject]@{ Handle = $Child; Text = $text }) | Out-Null
    }
    return $true
  }
  [void][RevitModalClickerNative]::EnumChildWindows($Parent, $childCb, [IntPtr]::Zero)
  return $rows
}

$targets = @(
  @{
    TitleHints = @((U @(0xC11C,0xBA85,0x0020,0xC5C6,0xB294,0x0020,0xC560,0xB4DC,0xC778)), 'Unsigned Add-in')
    BodyHints = @((U @(0xAC8C,0xC2DC,0xC790,0xB97C,0x0020,0xD655,0xC778,0xD560,0x0020,0xC218,0x0020,0xC5C6,0xC2B5,0xB2C8,0xB2E4)), 'publisher could not be verified')
    ButtonHints = @((U @(0xD56D,0xC0C1,0x0020,0xB85C,0xB4DC)), 'Always Load')
  },
  @{
    TitleHints = @((U @(0xD574,0xACB0,0xB418,0xC9C0,0x0020,0xC54A,0xC740,0x0020,0xCC38,0xC870)), 'Unresolved References')
    BodyHints = @((U @(0xCC38,0xC870,0xB97C,0x0020,0xCC3E,0xAC70,0xB098,0x0020,0xC77D,0xC744,0x0020,0xC218,0x0020,0xC5C6,0xC2B5,0xB2C8,0xB2E4)), 'unresolved references')
    ButtonHints = @((U @(0xBB34,0xC2DC,0xD558,0xACE0,0x0020,0xACC4,0xC18D,0x0020,0xD504,0xB85C,0xC81D,0xD2B8,0x0020,0xC5F4,0xAE30)), 'Ignore and continue', 'continue opening')
  }
)

$topWindows = New-Object System.Collections.Generic.List[IntPtr]
$topCb = [RevitModalClickerNative+EnumWindowsProc]{
  param([IntPtr]$Handle, [IntPtr]$LParam)
  if ([RevitModalClickerNative]::IsWindowVisible($Handle)) {
    $topWindows.Add($Handle) | Out-Null
  }
  return $true
}
[void][RevitModalClickerNative]::EnumWindows($topCb, [IntPtr]::Zero)

$clicked = 0
foreach ($window in $topWindows) {
  $title = Get-WindowTextValue $window
  $children = @(Get-ChildTextRows $window)
  $allText = ($title + "`n" + (($children | ForEach-Object { $_.Text }) -join "`n"))

  foreach ($target in $targets) {
    $matchesTitle = $false
    foreach ($hint in $target.TitleHints) {
      if ($title -like "*$hint*") { $matchesTitle = $true; break }
    }

    $matchesBody = $false
    foreach ($hint in $target.BodyHints) {
      if ($allText -like "*$hint*") { $matchesBody = $true; break }
    }

    if (-not ($matchesTitle -or $matchesBody)) {
      continue
    }

    $button = $null
    foreach ($hint in $target.ButtonHints) {
      $button = $children | Where-Object { $_.Text -like "*$hint*" } | Select-Object -First 1
      if ($button) { break }
    }

    if ($button) {
      [void][RevitModalClickerNative]::SendMessage($button.Handle, $BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero)
      Write-Output ("clicked: {0} | window: {1}" -f $button.Text, $title)
      $clicked++
    }
  }
}

Write-Output ("clicked_count={0}" -f $clicked)
