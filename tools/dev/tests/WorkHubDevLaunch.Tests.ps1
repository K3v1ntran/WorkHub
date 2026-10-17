<#
.SYNOPSIS
    Tests for tools\dev\WorkHubDevLaunch.psm1 (Phase 11 C5, §10.2). Plain PowerShell 5.1, no Pester.

.DESCRIPTION
    Run: powershell.exe -NoProfile -File tools\dev\tests\WorkHubDevLaunch.Tests.ps1
    Everything happens under a fresh %TEMP%\WorkHubDevLaunchTests\<guid> tree: a fake repository with dummy WorkHub.exe and
    WorkHub.dll (never executed), a fake sandbox base and a fake "real profile". The real profile is never referenced.
    Every refusal must call the process-start seam zero times. Exit code 0 only when every case passes.
#>
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\WorkHubDevLaunch.psm1') -Force

$T = Join-Path $env:TEMP ("WorkHubDevLaunchTests\" + [guid]::NewGuid().ToString('N'))
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Cases = 0
$script:Starts = New-Object System.Collections.Generic.List[object]
$script:NotApplicable = New-Object System.Collections.Generic.List[string]
$script:Processes = New-Object System.Collections.Generic.List[object]

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    $script:Cases++
    if ($Condition) { Write-Host "  ok   $Name" }
    else { Write-Host "  FAIL $Name $Detail" -ForegroundColor Red; $script:Failures.Add("$Name $Detail") }
}

$Recorder = {
    param([string]$Exe, [string]$CommandLine)
    $script:Starts.Add([pscustomobject]@{ Exe = $Exe; CommandLine = $CommandLine })
    $process = [pscustomobject]@{ Id = 4242; Killed = $false }
    $process | Add-Member -MemberType ScriptMethod -Name Kill -Value { $this.Killed = $true }
    $script:Processes.Add($process)
    return $process
}

function New-Junction([string]$Link, [string]$Target) {
    $output = & cmd.exe /c mklink /J "$Link" "$Target" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "mklink /J failed: $output" }
}

function New-FakeBuild([string]$Repo, [string]$Configuration, [bool]$WithDll = $true) {
    $bin = Join-Path $Repo "src\WorkHub.App\bin\$Configuration\net10.0-windows"
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $bin 'WorkHub.exe'), 'not a program')
    if ($WithDll) { [System.IO.File]::WriteAllText((Join-Path $bin 'WorkHub.dll'), 'not a library') }
}

# ---- Fixture -------------------------------------------------------------------------------------------------------------
$Repo = Join-Path $T 'repo'
New-FakeBuild $Repo 'Release'
New-FakeBuild $Repo 'Debug'
$Base = Join-Path $T 'WorkHubTest11\roots'
$Default = Join-Path $T 'profile\WorkHub'
New-Item -ItemType Directory -Force -Path $Base, (Join-Path $Default 'Data\LongFolderNameForShortNames'), (Join-Path $T 'elsewhere'), (Join-Path $T 'WorkHubTest11\roots-other') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Base 'exists') | Out-Null
$Common = @{ RepoRoot = $Repo; SandboxBase = $Base; DefaultRoot = $Default }

function Plan([string]$Root, [hashtable]$Overrides = @{}) {
    $parameters = $Common.Clone()
    foreach ($key in $Overrides.Keys) { $parameters[$key] = $Overrides[$key] }
    return Get-IsolatedLaunchPlan -DataRoot $Root @parameters
}

function Refused([string]$Name, $Plan, [string]$ReasonLike = '*') {
    $before = $script:Starts.Count
    $start = Start-IsolatedLaunch -Plan $Plan -Starter $Recorder -Inspector { param($id) throw 'no inspection in refusals' }
    Check $Name ((-not $Plan.Ok) -and ($Plan.Reason -like $ReasonLike) -and (-not $start.Started) -and $script:Starts.Count -eq $before) "(plan ok=$($Plan.Ok) reason='$(if ($Plan.Ok) { '' } else { $Plan.Reason })' starts=$($script:Starts.Count - $before))"
}

try {
    Write-Host 'Arguments'
    Refused 'empty root' (Plan '')
    Refused 'whitespace root' (Plan '   ')
    Refused 'relative root' (Plan 'roots\x')
    Refused 'UNC root' (Plan '\\server\share\x') '*network*'
    Refused 'device root' (Plan '\\?\C:\x') '*network*'
    Refused 'quote in root' (Plan "$Base\a`"b") '*not allowed*'
    Refused 'dot-dot segment' (Plan "$Base\..\roots\x") "*'..'*"
    Refused 'dot segment' (Plan "$Base\.\x") "*'..'*"
    Refused 'stream colon' (Plan "$Base\x:stream") '*not allowed*'

    Write-Host 'Real profile'
    Refused 'equal to the profile' (Plan $Default) '*real WorkHub profile*'
    Refused 'inside the profile' (Plan "$Default\Data") '*real WorkHub profile*'
    Refused 'containing the profile' (Plan (Split-Path $Default)) '*real WorkHub profile*'
    Refused 'profile, other case and trailing separator' (Plan ($Default.ToUpperInvariant() + '\')) '*real WorkHub profile*'

    Write-Host 'Sandbox'
    Refused 'equal to the base' (Plan $Base) '*sandbox base*'
    Refused 'outside the base' (Plan "$T\elsewhere\x") '*sandbox base*'
    Refused 'sibling-prefix of the base' (Plan "$T\WorkHubTest11\roots-other\x") '*sandbox base*'
    Refused 'missing sandbox base' (Plan "$T\nobase\x" @{ SandboxBase = "$T\nobase" }) '*sandbox base does not exist*'

    Write-Host 'Missing descendants'
    Refused 'missing parent' (Plan "$Base\new\deeper") "*parent folder does not exist*"
    $ok = Plan "$Base\exists\deeper"
    Check 'missing leaf under an existing child is accepted' ($ok.Ok -and $ok.Root -eq "$((Get-Item $Base).FullName)\exists\deeper") "(reason: $(if (-not $ok.Ok) { $ok.Reason }))"

    Write-Host 'Aliases'
    New-Junction (Join-Path $Base 'alias-to-profile') $Default
    Refused 'junction in the sandbox pointing into the profile (below it)' (Plan "$Base\alias-to-profile\Data") '*real WorkHub profile*'
    Refused 'junction in the sandbox pointing at the profile (the root itself)' (Plan "$Base\alias-to-profile") '*'
    New-Junction (Join-Path $Base 'alias-outside') (Join-Path $T 'elsewhere')
    Refused 'junction leading outside the base' (Plan "$Base\alias-outside\x") '*sandbox base*'
    New-Item -ItemType Directory -Force -Path (Join-Path $T 'elsewhere\real-root-target') | Out-Null
    New-Junction (Join-Path $Base 'root-is-junction') (Join-Path $Base 'exists')
    Refused 'root that is itself a junction' (Plan "$Base\root-is-junction") '*reparse point*'
    [System.IO.File]::WriteAllText((Join-Path $Base 'a-file'), 'x')
    Refused 'root that is an existing file' (Plan "$Base\a-file") '*file with that name*'
    $long = Join-Path $Default 'Data\LongFolderNameForShortNames'
    $short = (& cmd.exe /c "for %I in (`"$long`") do @echo %~sI").Trim()
    if ($short -and $short -ne $long -and $short -match '~') {
        Refused '8.3 short-name spelling of a path inside the profile' (Plan $short) '*real WorkHub profile*'
    }
    else {
        $script:NotApplicable.Add('8.3 short names are not generated on this volume')
        Write-Host '  n/a  8.3 short-name case (no short names on this volume)'
    }

    Write-Host 'Executable'
    Refused 'unknown configuration' (Plan "$Base\cfg" @{ Configuration = 'Foo' }) '*Debug or Release*'
    $noBuild = Join-Path $T 'repo-nobuild'
    New-Item -ItemType Directory -Force -Path $noBuild | Out-Null
    Refused 'missing executable' (Plan "$Base\x" @{ RepoRoot = $noBuild }) '*executable not found*'
    $noDll = Join-Path $T 'repo-nodll'
    New-FakeBuild $noDll 'Release' $false
    Refused 'missing WorkHub.dll' (Plan "$Base\x" @{ RepoRoot = $noDll }) '*WorkHub.dll*'
    $redirected = Join-Path $T 'repo-redirected'
    New-Item -ItemType Directory -Force -Path (Join-Path $redirected 'src\WorkHub.App\bin') | Out-Null
    New-FakeBuild (Join-Path $T 'outside-build') 'Release'
    New-Junction (Join-Path $redirected 'src\WorkHub.App\bin\Release') (Join-Path $T 'outside-build\src\WorkHub.App\bin\Release')
    Refused 'executable behind a junction (outside the repository)' (Plan "$Base\x" @{ RepoRoot = $redirected }) '*reparse point*'

    Write-Host 'Accepted plans and quoting'
    $spaces = Plan "$Base\a b"
    $baseFinal = (Get-Item $Base).FullName
    Check 'root with spaces is quoted' ($spaces.Ok -and $spaces.CommandLine -ceq "`"--data-root`" `"$baseFinal\a b`"") "($($spaces.CommandLine))"
    $trailing = Plan "$Base\t1\"
    Check 'trailing backslash is trimmed' ($trailing.Ok -and $trailing.Root -ceq "$baseFinal\t1" -and $trailing.CommandLine -ceq "`"--data-root`" `"$baseFinal\t1`"") "($($trailing.CommandLine))"
    $slash = Plan "$Base/t2/"
    Check 'trailing slash is trimmed' ($slash.Ok -and $slash.Root -ceq "$baseFinal\t2") "($($slash.Root))"
    $debug = Plan "$Base\dbg" @{ Configuration = 'Debug' }
    Check 'Debug configuration selects the Debug build' ($debug.Ok -and $debug.Exe -like '*\bin\Debug\net10.0-windows\WorkHub.exe')

    $before = $script:Starts.Count
    $started = Start-IsolatedLaunch -Plan $spaces -Starter $Recorder -Inspector { param($id) $null }
    $call = $script:Starts[$script:Starts.Count - 1]
    Check 'an accepted plan starts exactly once with the planned exe and command line' ($started.Started -and $script:Starts.Count -eq $before + 1 -and $call.Exe -ceq $spaces.Exe -and $call.CommandLine -ceq $spaces.CommandLine)

    Check 'quote: plain' ((ConvertTo-WindowsArgument 'abc') -ceq '"abc"')
    Check 'quote: empty' ((ConvertTo-WindowsArgument '') -ceq '""')
    Check 'quote: spaces' ((ConvertTo-WindowsArgument 'C:\a b') -ceq '"C:\a b"')
    Check 'quote: trailing backslash doubled before the closing quote' ((ConvertTo-WindowsArgument 'C:\a b\') -ceq '"C:\a b\\"')
    Check 'quote: trailing backslash run doubled' ((ConvertTo-WindowsArgument 'C:\x\\') -ceq '"C:\x\\\\"')
    Check 'quote: inner backslashes unchanged' ((ConvertTo-WindowsArgument 'a\\b\c') -ceq '"a\\b\c"')
    Check 'quote: embedded quote escaped' ((ConvertTo-WindowsArgument 'a"b') -ceq '"a\"b"')
    Check 'quote: backslashes before an embedded quote doubled plus one' ((ConvertTo-WindowsArgument 'a\"b') -ceq '"a\\\"b"')

    Write-Host 'Validated plans (Codex condition 2)'
    foreach ($field in 'Exe', 'Root', 'CommandLine', 'Configuration') {
        $plan = Plan "$Base\mutate-$field"
        $plan.$field = switch ($field) {
            'Exe' { 'C:\Windows\System32\notepad.exe' }
            'Root' { $Default }
            'CommandLine' { '' }
            'Configuration' { 'Debug' }
        }
        $before = $script:Starts.Count
        $result = Start-IsolatedLaunch -Plan $plan -Starter $Recorder -Inspector { param($id) $null }
        Check "mutated $field is refused with zero starts" ((-not $result.Started) -and $result.Reason -like '*modified*' -and $script:Starts.Count -eq $before) "($($result.Reason))"
    }
    $plan = Plan "$Base\mutate-args"
    $plan.Arguments = @('--data-root', $Default)
    $before = $script:Starts.Count
    $result = Start-IsolatedLaunch -Plan $plan -Starter $Recorder -Inspector { param($id) $null }
    Check 'mutated Arguments is refused with zero starts' ((-not $result.Started) -and $script:Starts.Count -eq $before) "($($result.Reason))"

    $forged = [pscustomobject]@{ Ok = $true; PlanId = [guid]::NewGuid().ToString('N'); Exe = $spaces.Exe; Root = $spaces.Root; Arguments = $spaces.Arguments; CommandLine = $spaces.CommandLine; Configuration = 'Release' }
    $before = $script:Starts.Count
    $result = Start-IsolatedLaunch -Plan $forged -Starter $Recorder -Inspector { param($id) $null }
    Check 'a hand-built plan is refused with zero starts' ((-not $result.Started) -and $result.Reason -like '*unknown*' -and $script:Starts.Count -eq $before)
    $result = Start-IsolatedLaunch -Plan ([pscustomobject]@{ Exe = $spaces.Exe }) -Starter $Recorder
    Check 'an object without a plan id is refused with zero starts' ((-not $result.Started) -and $script:Starts.Count -eq $before)

    $before = $script:Starts.Count
    $result = Start-IsolatedLaunch -Plan $spaces -Starter $Recorder -Inspector { param($id) $null }
    Check 'a used plan cannot be started again' ((-not $result.Started) -and $result.Reason -like '*already used*' -and $script:Starts.Count -eq $before)

    New-Item -ItemType Directory -Force -Path (Join-Path $Base 'swap') | Out-Null
    $plan = Plan "$Base\swap\x"
    Check 'plan accepted before the on-disk change' $plan.Ok
    Remove-Item -LiteralPath (Join-Path $Base 'swap') -Recurse -Force
    New-Junction (Join-Path $Base 'swap') $Default
    $before = $script:Starts.Count
    $result = Start-IsolatedLaunch -Plan $plan -Starter $Recorder -Inspector { param($id) $null }
    Check 'a root changed into a junction after planning is refused at revalidation' ((-not $result.Started) -and $result.Reason -like '*revalidation failed*real WorkHub profile*' -and $script:Starts.Count -eq $before) "($($result.Reason))"

    Write-Host 'Post-start verification (verification only)'
    $verificationCases = @(
        @{ Name = 'inspection returns nothing'; Inspector = { param($id) $null }; Expected = 'not independently verified' },
        @{ Name = 'inspection fails'; Inspector = { param($id) throw 'access denied' }; Expected = 'not independently verified' },
        @{ Name = 'inspection shows another command line'; Inspector = { param($id) '"C:\other.exe" --data-root "C:\x"' }; Expected = 'MISMATCH' },
        @{ Name = 'inspection matches'; Inspector = $null; Expected = 'verified' }
    )
    foreach ($case in $verificationCases) {
        $plan = Plan "$Base\verify-$($script:Cases)"
        $inspector = if ($case.Inspector) { $case.Inspector } else { $p = $plan; { param($id) "`"$($p.Exe)`" $($p.CommandLine)" }.GetNewClosure() }
        $result = Start-IsolatedLaunch -Plan $plan -Starter $Recorder -Inspector $inspector
        Check "verification: $($case.Name) -> $($case.Expected), process untouched" ($result.Started -and $result.Verification -ceq $case.Expected) "($($result.Verification))"
    }
    Check 'verification never kills the process' ($script:Processes.Count -gt 0 -and -not ($script:Processes | Where-Object { $_.Killed }))
}
catch {
    $script:Failures.Add("unexpected error: $($_.Exception.Message) at $($_.InvocationInfo.PositionMessage)")
    Write-Host "  ERROR $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    # Junctions are removed as links (rmdir), never followed.
    Get-ChildItem -LiteralPath $T -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object { & cmd.exe /c rmdir "$($_.FullName)" }
    Remove-Item -LiteralPath $T -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "$($script:Cases) checks, $($script:Failures.Count) failed, $($script:NotApplicable.Count) not applicable."
foreach ($item in $script:NotApplicable) { Write-Host "  n/a: $item" }
foreach ($failure in $script:Failures) { Write-Host "  failed: $failure" }
if ($script:Failures.Count -gt 0) { exit 1 }
exit 0
