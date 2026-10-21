param([string]$InstallDirectory = "$env:LOCALAPPDATA\OneSetup")
$ErrorActionPreference = 'Stop'
function Get-OneSetupUserPath([string]$CurrentPath, [string]$Directory) {
    function Normalize([string]$Value) {
        $expanded = [Environment]::ExpandEnvironmentVariables($Value.Trim().Trim('"'))
        try { return [IO.Path]::GetFullPath($expanded).TrimEnd('\','/') }
        catch { Write-Warning "Cannot normalize PATH entry: $Value"; return $expanded.TrimEnd('\','/') }
    }
    $target = Normalize $Directory
    $found = $false
    $entries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($entry in ($CurrentPath -split ';')) {
        if ($entry.Trim().Length -gt 0 -and (Normalize $entry) -ieq $target) {
            if (-not $found) { $entries.Add($entry); $found = $true }
        } else { $entries.Add($entry) }
    }
    if (-not $found) {
        if ($CurrentPath.Length -eq 0) { return $Directory }
        if ($entries[$entries.Count-1] -eq '') { $entries[$entries.Count-1] = $Directory }
        else { $entries.Add($Directory) }
    }
    return $entries -join ';'
}
if ($MyInvocation.InvocationName -eq '.') { return }
$registry = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Environment')
try {
    $current = [string]$registry.GetValue('Path','',[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $updated = Get-OneSetupUserPath $current $InstallDirectory
    if ($updated -cne $current) {
        $kind = if ($registry.GetValueNames() -contains 'Path') { $registry.GetValueKind('Path') } else { [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $registry.SetValue('Path',$updated,$kind)
    }
} finally { $registry.Dispose() }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OneSetupEnvironmentNotification {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wparam, string lparam, uint flags, uint timeout, out UIntPtr result);
}
'@
$result = [UIntPtr]::Zero
$sent = [OneSetupEnvironmentNotification]::SendMessageTimeout([IntPtr]0xffff,0x001a,[UIntPtr]::Zero,'Environment',2,5000,[ref]$result)
if ($sent -eq [IntPtr]::Zero) { Write-Warning 'PATH saved; notification timed out. Sign out and back in if needed.' }
Write-Host 'User PATH configured. Close all terminal windows and open a new terminal.'
