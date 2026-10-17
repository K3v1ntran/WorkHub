# Isolated developer launches of WorkHub (Phase 11 C1-C4, §10.2). Windows PowerShell 5.1 compatible.
#
# Every safety decision is made BEFORE a process exists:
#   Get-IsolatedLaunchPlan  validates the executable, an explicit synthetic data root that must be a strict child of one
#                           sandbox base, and its overlap with the real profile after resolving existing path aliases
#                           (junctions, symbolic links, subst drives, 8.3 names). It has no side effects.
#   Start-IsolatedLaunch    accepts only a plan this module issued, once; it REGENERATES the plan from the original inputs,
#                           refuses any difference, and starts only the regenerated executable and arguments.
# After the start, the process command line is compared with the plan. That is verification only: it never kills or
# changes the process, and it cannot prevent anything; "not independently verified" is reported when inspection fails.

Set-StrictMode -Version 2.0

$script:DefaultSandboxBase = 'C:\WorkHubTest11\roots'
$script:Issued = @{}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class WorkHubDevNativePath
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint InvalidAttributes = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string name);

    /// <summary>The final (alias-free) path of an EXISTING file or directory. Opens it for attribute reads only.</summary>
    public static string FinalPath(string path)
    {
        using (var handle = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero))
        {
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open for attribute reads: " + path);
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve the final path of: " + path);
            return buffer.ToString();
        }
    }

    /// <summary>Attributes from the file system metadata (no handle to the contents); -1 when the path does not exist.</summary>
    public static long Attributes(string path)
    {
        var attributes = GetFileAttributesW(path);
        return attributes == InvalidAttributes ? -1L : (long)attributes;
    }
}
'@

function ConvertTo-WindowsArgument {
    <# Quotes one argument for CommandLineToArgvW / the .NET runtime: always wrapped in quotes; backslashes before a quote
       (an embedded one or the closing one) are doubled; an embedded quote becomes \". #>
    param([AllowEmptyString()][string]$Value)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq '\') { $backslashes++; continue }
        if ($ch -eq '"') {
            [void]$builder.Append([char]'\', 2 * $backslashes + 1).Append('"')
        }
        else {
            if ($backslashes -gt 0) { [void]$builder.Append([char]'\', $backslashes) }
            [void]$builder.Append($ch)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) { [void]$builder.Append([char]'\', 2 * $backslashes) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Test-IsReparsePoint([string]$Path) {
    $attributes = [WorkHubDevNativePath]::Attributes($Path)
    return $attributes -ge 0 -and ($attributes -band 0x400) -ne 0
}

function Get-Segments([string]$Path) { return @($Path.TrimEnd('\').Split('\') | Where-Object { $_ -ne '' }) }

function Test-IsStrictChild([string]$Child, [string]$Parent) {
    $c = Get-Segments $Child
    $p = Get-Segments $Parent
    if ($c.Count -le $p.Count) { return $false }
    for ($i = 0; $i -lt $p.Count; $i++) { if (-not [string]::Equals($c[$i], $p[$i], [StringComparison]::OrdinalIgnoreCase)) { return $false } }
    return $true
}

function Test-Overlap([string]$A, [string]$B) {
    return [string]::Equals($A.TrimEnd('\'), $B.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or (Test-IsStrictChild $A $B) -or (Test-IsStrictChild $B $A)
}

function ConvertFrom-FinalPath([string]$Final) {
    if ($Final.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { throw 'resolves to a network path' }
    if ($Final.StartsWith('\\?\', [StringComparison]::Ordinal)) { return $Final.Substring(4) }
    return $Final
}

function Resolve-AliasFreePath([string]$FullPath) {
    <# The final path of the deepest EXISTING ancestor (aliases resolved), plus the missing segments unchanged (a segment
       that does not exist cannot be an alias). #>
    $existing = $FullPath.TrimEnd('\')
    $missing = New-Object System.Collections.Generic.List[string]
    while ([WorkHubDevNativePath]::Attributes($existing) -lt 0) {
        $parent = [System.IO.Path]::GetDirectoryName($existing)
        if ([string]::IsNullOrEmpty($parent)) { throw "no existing ancestor: $FullPath" }
        $missing.Insert(0, [System.IO.Path]::GetFileName($existing))
        $existing = $parent
    }
    $resolved = (ConvertFrom-FinalPath ([WorkHubDevNativePath]::FinalPath($existing))).TrimEnd('\')
    foreach ($segment in $missing) { $resolved = $resolved + '\' + $segment }
    return $resolved
}

function New-PlanCore([hashtable]$In) {
    <# Validates the inputs and builds the plan fields; throws a refusal message. No side effects. #>
    if ($In.Configuration -cnotin @('Debug', 'Release')) { throw "configuration must be Debug or Release (got '$($In.Configuration)')" }

    # 1. Executable: derived from the repository, never given by the caller.
    if (-not (Test-Path -LiteralPath $In.RepoRoot -PathType Container)) { throw "repository root not found: $($In.RepoRoot)" }
    $repo = (ConvertFrom-FinalPath ([WorkHubDevNativePath]::FinalPath($In.RepoRoot))).TrimEnd('\')
    $binary = Join-Path $repo "src\WorkHub.App\bin\$($In.Configuration)\net10.0-windows"
    $exe = Join-Path $binary 'WorkHub.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "executable not found (build $($In.Configuration) first): $exe" }
    if (-not (Test-Path -LiteralPath (Join-Path $binary 'WorkHub.dll') -PathType Leaf)) { throw "WorkHub.dll is missing beside $exe" }
    for ($p = $exe; $p.Length -gt $repo.Length; $p = [System.IO.Path]::GetDirectoryName($p)) {
        if (Test-IsReparsePoint $p) { throw "the executable path contains a reparse point: $p" }
    }
    $exeFinal = ConvertFrom-FinalPath ([WorkHubDevNativePath]::FinalPath($exe))
    if (-not (Test-IsStrictChild $exeFinal $repo)) { throw "the executable resolves outside the repository: $exeFinal" }

    # 2. Explicit synthetic root, syntactically.
    $raw = $In.DataRoot
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'an explicit -DataRoot is required' }
    if ($raw.StartsWith('\\') -or $raw.StartsWith('//')) { throw 'network (UNC) and device paths are not allowed' }
    if ($raw.IndexOfAny([char[]]'"*?<>|') -ge 0 -or ($raw.ToCharArray() | Where-Object { [char]::IsControl($_) })) { throw 'the data root contains characters that are not allowed' }
    if ($raw -notmatch '^[A-Za-z]:[\\/]') { throw 'the data root must be a full local path such as C:\WorkHubTest11\roots\example' }
    if (@($raw.Substring(3).Split([char[]]'\/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -gt 0) { throw "'.' and '..' segments are not allowed" }
    if ($raw.Substring(2).IndexOf(':') -ge 0) { throw 'the data root contains characters that are not allowed' }

    # 3. Normalization (trailing separators trimmed).
    $normalized = [System.IO.Path]::GetFullPath($raw).TrimEnd('\')

    # 4. Aliases of the existing part resolved.
    $root = Resolve-AliasFreePath $normalized
    if (Test-IsReparsePoint $normalized) { throw "the data root itself is a reparse point: $normalized" }
    if (Test-Path -LiteralPath $root -PathType Leaf) { throw "a file with that name exists: $root" }

    # 5. Real profile: resolved through its PARENT only; the profile itself is never opened (attributes only).
    $default = [System.IO.Path]::GetFullPath($In.DefaultRoot).TrimEnd('\')
    if (Test-IsReparsePoint $default) { throw "the real profile folder is itself a reparse point; refusing to reason about its aliases: $default" }
    $defaultResolved = (Resolve-AliasFreePath ([System.IO.Path]::GetDirectoryName($default))) + '\' + [System.IO.Path]::GetFileName($default)
    if ((Test-Overlap $root $defaultResolved) -or (Test-Overlap $normalized $default)) { throw "the data root overlaps the real WorkHub profile ($default)" }

    # 6. One explicit sandbox base; the root must be a strict child (segment-wise, not a string prefix).
    $base = [System.IO.Path]::GetFullPath($In.SandboxBase).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { throw "the sandbox base does not exist: $base" }
    if (Test-IsReparsePoint $base) { throw "the sandbox base is a reparse point: $base" }
    $baseResolved = (ConvertFrom-FinalPath ([WorkHubDevNativePath]::FinalPath($base))).TrimEnd('\')
    if (-not (Test-IsStrictChild $root $baseResolved)) { throw "the data root must be inside the sandbox base $baseResolved (got $root)" }

    # 7. WorkHub's own rule: the parent must exist. Nothing is created here.
    if (-not (Test-Path -LiteralPath ([System.IO.Path]::GetDirectoryName($root)) -PathType Container)) { throw "the data root's parent folder does not exist: $([System.IO.Path]::GetDirectoryName($root))" }

    # 8. Arguments and the exact command-line text.
    $arguments = @('--data-root', $root)
    return @{
        Exe           = $exeFinal
        Root          = $root
        Arguments     = $arguments
        CommandLine   = (($arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' ')
        Configuration = $In.Configuration
    }
}

function Get-IsolatedLaunchPlan {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][AllowNull()][string]$DataRoot,
        [string]$Configuration = 'Release',
        [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
        [string]$SandboxBase = $script:DefaultSandboxBase,
        [string]$DefaultRoot = (Join-Path $env:LOCALAPPDATA 'WorkHub')
    )
    $inputs = @{ DataRoot = $DataRoot; Configuration = $Configuration; RepoRoot = $RepoRoot; SandboxBase = $SandboxBase; DefaultRoot = $DefaultRoot }
    try {
        $fields = New-PlanCore $inputs
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Reason = $_.Exception.Message }
    }

    $id = [guid]::NewGuid().ToString('N')
    $script:Issued[$id] = @{ Inputs = $inputs; Used = $false }
    return [pscustomobject]@{
        Ok            = $true
        PlanId        = $id
        Exe           = $fields.Exe
        Root          = $fields.Root
        Arguments     = $fields.Arguments
        CommandLine   = $fields.CommandLine
        Configuration = $fields.Configuration
    }
}

$script:DefaultStarter = {
    param([string]$Exe, [string]$CommandLine)
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = $CommandLine
    # ShellExecute: the app must not inherit the caller's (possibly redirected) standard handles, or a caller capturing
    # this script's output waits until WorkHub exits. The executable is a full path to the validated .exe.
    $info.UseShellExecute = $true
    $info.WorkingDirectory = [System.IO.Path]::GetDirectoryName($Exe)
    return [System.Diagnostics.Process]::Start($info)
}

$script:DefaultInspector = {
    param([int]$ProcessId)
    return (Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction Stop).CommandLine
}

function Get-LaunchVerification([string]$Observed, [string]$Exe, [string]$CommandLine) {
    if ([string]::IsNullOrWhiteSpace($Observed)) { return 'not independently verified' }
    $expected = @("`"$Exe`" $CommandLine", "$Exe $CommandLine")
    if ($expected -contains $Observed.Trim()) { return 'verified' }
    return 'MISMATCH'
}

function Start-IsolatedLaunch {
    [CmdletBinding()]
    param(
        $Plan,
        [scriptblock]$Starter = $script:DefaultStarter,
        [scriptblock]$Inspector = $script:DefaultInspector
    )
    $refuse = { param($reason) [pscustomobject]@{ Started = $false; Reason = $reason } }

    if ($null -eq $Plan -or -not ($Plan.PSObject.Properties.Name -contains 'PlanId') -or -not ($Plan.PSObject.Properties.Name -contains 'Ok') -or -not $Plan.Ok) {
        return & $refuse 'not a launch plan issued by Get-IsolatedLaunchPlan'
    }
    $entry = $script:Issued[[string]$Plan.PlanId]
    if ($null -eq $entry) { return & $refuse 'unknown launch plan (not issued by this module)' }
    if ($entry.Used) { return & $refuse 'this launch plan was already used' }
    $entry.Used = $true

    # Never trust the caller's object: regenerate from the original inputs and revalidate now.
    try {
        $fresh = New-PlanCore $entry.Inputs
    }
    catch {
        return & $refuse "revalidation failed: $($_.Exception.Message)"
    }
    foreach ($name in 'Exe', 'Root', 'CommandLine', 'Configuration') {
        if (-not ($Plan.PSObject.Properties.Name -contains $name) -or [string]$Plan.$name -cne [string]$fresh[$name]) {
            return & $refuse "the plan was modified or is inconsistent ($name)"
        }
    }
    if ((@($Plan.Arguments) -join "`n") -cne ($fresh.Arguments -join "`n")) { return & $refuse 'the plan was modified or is inconsistent (Arguments)' }

    $process = & $Starter $fresh.Exe $fresh.CommandLine

    # Verification only (never a safety check, never kills).
    $observed = $null
    try { $observed = & $Inspector ([int]$process.Id) } catch { $observed = $null }
    return [pscustomobject]@{
        Started      = $true
        ProcessId    = [int]$process.Id
        Exe          = $fresh.Exe
        Root         = $fresh.Root
        CommandLine  = $fresh.CommandLine
        Verification = Get-LaunchVerification $observed $fresh.Exe $fresh.CommandLine
        Observed     = $observed
    }
}

Export-ModuleMember -Function Get-IsolatedLaunchPlan, Start-IsolatedLaunch, ConvertTo-WindowsArgument
