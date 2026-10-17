<#
.SYNOPSIS
    Starts this repository's WorkHub build against an explicit synthetic data root. Never the real profile.

.DESCRIPTION
    The only approved way to launch WorkHub while developing or testing (README "Developer launches"). All checks run
    before any process is created; a refusal starts nothing and exits 1. The data root must be a strict child of the
    sandbox base (default C:\WorkHubTest11\roots) and its parent must exist. After the start, the process command line is
    compared with the plan and the outcome ("verified", "MISMATCH" or "not independently verified") is reported and
    recorded in %TEMP%\WorkHubDev\launches.log. That comparison is verification only; it never stops the process.

.EXAMPLE
    New-Item -ItemType Directory C:\WorkHubTest11\roots -Force | Out-Null
    tools\dev\Start-WorkHubIsolated.ps1 -DataRoot C:\WorkHubTest11\roots\try-1
#>
param(
    [Parameter(Mandatory = $true)][AllowEmptyString()][string]$DataRoot,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$SandboxBase = 'C:\WorkHubTest11\roots',
    [string]$LogFile = (Join-Path $env:TEMP 'WorkHubDev\launches.log')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkHubDevLaunch.psm1') -Force

$plan = Get-IsolatedLaunchPlan -DataRoot $DataRoot -Configuration $Configuration -SandboxBase $SandboxBase
if (-not $plan.Ok) {
    [Console]::Error.WriteLine("Refused, nothing was started: $($plan.Reason)")
    exit 1
}

$result = Start-IsolatedLaunch -Plan $plan
if (-not $result.Started) {
    [Console]::Error.WriteLine("Refused, nothing was started: $($result.Reason)")
    exit 1
}

$head = try { (& git -C (Join-Path $PSScriptRoot '..\..') rev-parse HEAD 2>$null) } catch { $null }
if (-not $head) { $head = 'unknown' }
$record = "{0}`tpid={1}`texe={2}`tcommandLine={3}`tverification={4}`thead={5}" -f [DateTime]::UtcNow.ToString('o'), $result.ProcessId, $result.Exe, $result.CommandLine, $result.Verification, $head
$logDirectory = Split-Path -Parent $LogFile
if (-not (Test-Path -LiteralPath $logDirectory)) { New-Item -ItemType Directory -Path $logDirectory | Out-Null }
Add-Content -LiteralPath $LogFile -Value $record -Encoding UTF8

"Started WorkHub (pid $($result.ProcessId)) on data root $($result.Root)"
"Command line: $($result.CommandLine)"
"Verification: $($result.Verification)"
if ($result.Verification -ne 'verified') { "Observed: $($result.Observed)" }
"Recorded in $LogFile"
exit 0
