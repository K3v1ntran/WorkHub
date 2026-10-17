<#
.SYNOPSIS
    Metadata-only snapshot of a WorkHub data folder (default: the real profile, %LOCALAPPDATA%\WorkHub), or a comparison
    of two snapshots.

.DESCRIPTION
    Lists every entry's relative path, kind, size, creation time and last-write time (UTC). It never reads file contents,
    never opens SQLite and never changes anything. Equal metadata does NOT prove equal content: SQLite can change a file
    without a noticeable timestamp or size change.

.EXAMPLE
    tools\dev\Get-ProfileSnapshot.ps1 -OutFile $env:TEMP\WorkHubDev\before.tsv
    tools\dev\Get-ProfileSnapshot.ps1 -Compare $env:TEMP\WorkHubDev\before.tsv -With $env:TEMP\WorkHubDev\after.tsv
#>
[CmdletBinding(DefaultParameterSetName = 'Snapshot')]
param(
    [Parameter(ParameterSetName = 'Snapshot')]
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'WorkHub'),

    [Parameter(ParameterSetName = 'Snapshot', Mandatory = $true)]
    [string]$OutFile,

    [Parameter(ParameterSetName = 'Compare', Mandatory = $true)]
    [string]$Compare,

    [Parameter(ParameterSetName = 'Compare', Mandatory = $true)]
    [string]$With
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$limit = '# Metadata only. Equal metadata does not prove equal content.'

function Read-Snapshot([string]$path) {
    $rows = @{}
    foreach ($line in Get-Content -LiteralPath $path -Encoding UTF8) {
        if ($line.StartsWith('#') -or $line -eq '') { continue }
        $fields = $line.Split("`t")
        $rows[$fields[0]] = $line
    }
    return $rows
}

if ($PSCmdlet.ParameterSetName -eq 'Compare') {
    $before = Read-Snapshot $Compare
    $after = Read-Snapshot $With
    $differences = 0
    foreach ($key in ($before.Keys + $after.Keys | Sort-Object -Unique)) {
        if (-not $after.ContainsKey($key)) { "REMOVED  $($before[$key])"; $differences++ }
        elseif (-not $before.ContainsKey($key)) { "ADDED    $($after[$key])"; $differences++ }
        elseif ($before[$key] -ne $after[$key]) { "CHANGED  $($before[$key])"; "      -> $($after[$key])"; $differences++ }
    }
    "$differences difference(s)."
    $limit
    return
}

$full = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# WorkHub data-folder metadata snapshot of $full at $([DateTime]::UtcNow.ToString('o'))")
$lines.Add($limit)
$lines.Add("# RelativePath`tKind`tLength`tCreationUtc`tLastWriteUtc")
if (Test-Path -LiteralPath $full -PathType Container) {
    # Get-ChildItem returns names and attributes from the directory listing; no file is opened.
    foreach ($item in Get-ChildItem -LiteralPath $full -Recurse -Force | Sort-Object FullName) {
        $relative = $item.FullName.Substring($full.Length + 1)
        $kind = if ($item.PSIsContainer) { 'dir' } else { 'file' }
        $length = if ($item.PSIsContainer) { '' } else { [string]$item.Length }
        $lines.Add("$relative`t$kind`t$length`t$($item.CreationTimeUtc.ToString('o'))`t$($item.LastWriteTimeUtc.ToString('o'))")
    }
}
else {
    $lines.Add('# (folder does not exist)')
}

$directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutFile))
if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
[System.IO.File]::WriteAllLines([System.IO.Path]::GetFullPath($OutFile), $lines, (New-Object System.Text.UTF8Encoding $false))
"$($lines.Count - 3) entries written to $OutFile"
$limit
