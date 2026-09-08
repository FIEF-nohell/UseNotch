# Session-start hook: injects project context into every fresh session.
# Emits: all hard rules, high-severity learnings, the 3 most recent learnings,
# and the frontmatter of any in-progress plan. Wired in .claude/settings.json.
$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path (Split-Path $PSScriptRoot)

function Get-Frontmatter([string]$Path) {
    $lines = Get-Content $Path -TotalCount 30
    if ($lines.Count -lt 2 -or $lines[0] -ne '---') { return $null }
    $end = ($lines | Select-Object -Skip 1 | Select-String -SimpleMatch '---' | Select-Object -First 1).LineNumber
    if (-not $end) { return $null }
    return ($lines[1..($end - 1)] -join "`n")
}

Write-Output '## Project context (auto-injected by session-start hook; do not re-read these files manually)'

$rules = Get-ChildItem (Join-Path $root '.docs/rules/*.md') | Where-Object { $_.Name -ne 'README.md' }
if ($rules) {
    Write-Output '### Hard rules (.docs/rules/, non-negotiable)'
    foreach ($r in $rules) {
        Write-Output "--- $($r.Name) ---"
        Write-Output (Get-Content $r.FullName -Raw)
    }
}

$learnings = Get-ChildItem (Join-Path $root '.docs/learnings/*.md') | Where-Object { $_.Name -ne 'README.md' } | Sort-Object Name
if ($learnings) {
    $recent = $learnings | Select-Object -Last 3
    $high = $learnings | Where-Object {
        $fm = Get-Frontmatter $_.FullName
        $fm -and $fm -match 'severity:\s*high' -and $fm -notmatch 'superseded-by:'
    }
    $selected = @($high) + @($recent) | Sort-Object FullName -Unique
    Write-Output '### Learnings (all high-severity plus the 3 most recent)'
    foreach ($l in $selected) {
        Write-Output "--- $($l.Name) ---"
        Write-Output (Get-Content $l.FullName -Raw)
    }
}

$plans = Get-ChildItem (Join-Path $root '.docs/plans/*.md') | Where-Object { $_.Name -ne 'README.md' }
$open = @()
foreach ($p in $plans) {
    $fm = Get-Frontmatter $p.FullName
    if ($fm -and $fm -match 'status:\s*in-progress') { $open += $p }
}
if ($open.Count -gt 0) {
    Write-Output '### In-progress plans (RESUME PROTOCOL APPLIES: surface these to the user before new work)'
    foreach ($p in $open) {
        Write-Output "--- $($p.Name) ---"
        Write-Output (Get-Frontmatter $p.FullName)
        $next = Select-String -Path $p.FullName -Pattern '^\s*- \[ \]' | Select-Object -First 1
        if ($next) { Write-Output "next unchecked task: $($next.Line.Trim())" }
    }
} else {
    Write-Output '### In-progress plans: none'
}
