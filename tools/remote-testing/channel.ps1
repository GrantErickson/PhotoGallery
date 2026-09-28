<#
.SYNOPSIS
  The message channel between the Claude sessions testing remote access: the host agent (on the computer with the
  library) and the tester agent (on the other computer). It's an append-only log.md on this repo's "remote-testing"
  branch, so both sides need only git, and main stays clean.

  THE REPO IS PUBLIC. Never write the passphrase, session tokens, personal details, what's in photos, file names of
  personal photos, or screenshots here. Test ids, pass/fail, error messages and timings are fine.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 read
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 read -Last 3
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 send -From tester -Message "S1-S19 pass. BUG-1: ..."
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 wait -Minutes 30
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\remote-testing\channel.ps1 watch

  wait: returns (exit 0) with the new messages once someone posts, or exit 1 after -Minutes.
  watch: prints each new message as it arrives, until stopped (for a background monitor).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('read', 'send', 'wait', 'watch')][string]$Action = 'read',
    [string]$From,
    [string]$Message,
    [int]$Last = 0,
    [int]$Minutes = 30,
    [int]$PollSeconds = 30
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$branch = 'remote-testing'
$repo = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
$worktree = Join-Path ([IO.Path]::GetTempPath()) 'photogallery-remote-testing'

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
}

function Get-RemoteHead {
    $line = & git -C $repo ls-remote origin "refs/heads/$branch" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $line) { return $null }
    return ($line -split '\s+')[0]
}

# The log's messages, oldest first; each starts with a "## <time> - <from>" line.
function Get-Messages {
    Invoke-Git -C $repo fetch -q origin $branch
    $text = (& git -C $repo show "origin/${branch}:log.md") -join "`n"
    $messages = @()
    $current = $null
    foreach ($line in ($text -split "`n")) {
        if ($line.StartsWith('## ')) {
            if ($null -ne $current) { $messages += $current.TrimEnd() }
            $current = $line
        }
        elseif ($null -ne $current) { $current += "`n" + $line }
    }
    if ($null -ne $current) { $messages += $current.TrimEnd() }
    return , $messages
}

function Write-Messages($messages) {
    foreach ($m in $messages) {
        [Console]::Out.WriteLine($m)
        [Console]::Out.WriteLine('')
    }
    [Console]::Out.Flush()
}

switch ($Action) {
    'read' {
        $messages = Get-Messages
        if ($Last -gt 0 -and $messages.Count -gt $Last) { $messages = $messages[($messages.Count - $Last)..($messages.Count - 1)] }
        Write-Messages $messages
    }
    'send' {
        if (-not $From -or -not $Message) { throw 'send needs -From (host or tester) and -Message' }
        if (-not (Test-Path (Join-Path $worktree '.git'))) {
            & git -C $repo worktree prune
            Invoke-Git -C $repo fetch -q origin $branch
            Invoke-Git -C $repo worktree add -q -f -B $branch $worktree "origin/$branch"
        }
        $utf8 = New-Object System.Text.UTF8Encoding $false
        for ($try = 1; $try -le 4; $try++) {
            # Start from what's there now each time, so a message posted meanwhile is kept (the log is append-only).
            Invoke-Git -C $worktree fetch -q origin $branch
            Invoke-Git -C $worktree reset -q --hard "origin/$branch"
            # ASCII only in this file: Windows PowerShell reads scripts without a BOM as ANSI.
            $entry = "`n## $(Get-Date -Format 'yyyy-MM-dd HH:mm zzz') $([char]0x00B7) $From`n`n$($Message.Trim())`n"
            [IO.File]::AppendAllText((Join-Path $worktree 'log.md'), $entry, $utf8)
            Invoke-Git -C $worktree add log.md
            Invoke-Git -C $worktree commit -q -m "remote-testing: message from $From"
            & git -C $worktree push -q origin "HEAD:$branch"
            if ($LASTEXITCODE -eq 0) { Write-Output 'Sent.'; return }
            Start-Sleep -Seconds (2 * $try)
        }
        throw 'Could not push the message (someone else kept posting, or no push access).'
    }
    { $_ -in 'wait', 'watch' } {
        $seen = (Get-Messages).Count
        $head = Get-RemoteHead
        $deadline = (Get-Date).AddMinutes($Minutes)
        while ($Action -eq 'watch' -or (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds $PollSeconds
            $now = Get-RemoteHead
            if (-not $now -or $now -eq $head) { continue }
            $head = $now
            $messages = Get-Messages
            if ($messages.Count -gt $seen) {
                Write-Messages $messages[$seen..($messages.Count - 1)]
                $seen = $messages.Count
                if ($Action -eq 'wait') { exit 0 }
            }
        }
        Write-Output "No new message in $Minutes minutes."
        exit 1
    }
}
