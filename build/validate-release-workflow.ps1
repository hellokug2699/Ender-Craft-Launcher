$ErrorActionPreference = "Stop"

$workflowPath = ".github/workflows/release.yml"
$lines = Get-Content $workflowPath
$validated = 0

for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -notmatch '^(\s*)run:\s*\|\s*$') {
        continue
    }

    $blockIndent = $Matches[1].Length
    $scriptLines = [System.Collections.Generic.List[string]]::new()
    $startLine = $i + 2

    for ($j = $i + 1; $j -lt $lines.Count; $j++) {
        $candidate = $lines[$j]
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            $scriptLines.Add("")
            continue
        }

        $leading = [regex]::Match($candidate, '^\s*').Length
        if ($leading -le $blockIndent) {
            break
        }
        if ($leading -lt ($blockIndent + 2)) {
            throw "Invalid YAML indentation in PowerShell run block near line $($j + 1)."
        }

        $scriptLines.Add($candidate.Substring($blockIndent + 2))
    }

    $script = $scriptLines -join [Environment]::NewLine
    try {
        [void][scriptblock]::Create($script)
    }
    catch {
        throw "Invalid PowerShell in release.yml run block starting at line ${startLine}: $($_.Exception.Message)"
    }

    $validated++
    $i = $j - 1
}

if ($validated -eq 0) {
    throw "No PowerShell run blocks were found in $workflowPath."
}

Write-Host "Validated $validated PowerShell run blocks in $workflowPath."
