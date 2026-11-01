#Requires -Version 7.0
<#
.SYNOPSIS
    检查提交内容是否泄漏本机路径、临时文件或密钥。

.DESCRIPTION
    仓库规则：提交中不得出现开发者本机绝对路径（例如非系统盘目录、用户目录）、
    临时/构建产物或任何私钥材料。脚本默认检查暂存区（git diff --cached），
    使用 -All 时扫描全部已跟踪文件，供 CI 使用。

    判定规则保持与具体机器无关：脚本不保存任何开发者路径，而是识别“非系统盘
    绝对路径”“用户目录路径”“当前仓库根的绝对路径”等形态。

.PARAMETER Staged
    只检查暂存区新增行（默认行为）。

.PARAMETER All
    扫描全部已跟踪文件。用于 CI 与提交前全量复核。

.PARAMETER RepositoryRoot
    仓库根目录；默认取 `git rev-parse --show-toplevel`。

.OUTPUTS
    退出码：0 通过；1 发现违规；2 无法执行检查。
#>
[CmdletBinding()]
param(
    [switch]$Staged,
    [switch]$All,
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Staged -and $All) {
    Write-Error '不能同时指定 -Staged 与 -All。'
    exit 2
}

if (-not $All) {
    $Staged = $true
}

$utf8 = [System.Text.UTF8Encoding]::new($false)
try {
    [Console]::OutputEncoding = $utf8
} catch {
    # 非交互宿主可能不支持；不影响 git 输出解析的尽力而为。
}

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & git -c core.quotepath=false @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') 失败：$output"
    }

    return @($output)
}

if (-not $RepositoryRoot) {
    try {
        $RepositoryRoot = (@(Invoke-Git -Arguments @('rev-parse', '--show-toplevel')) | Select-Object -First 1).Trim()
    } catch {
        Write-Error "当前目录不是 git 仓库：$($_.Exception.Message)"
        exit 2
    }
}

$RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
Push-Location $RepositoryRoot
try {
    $rootVariants = @(
        [regex]::Escape($RepositoryRoot),
        [regex]::Escape(($RepositoryRoot -replace '\\', '/')),
        [regex]::Escape(($RepositoryRoot -replace '/', '\'))
    ) | Select-Object -Unique

    $rules = [System.Collections.Generic.List[object]]::new()

    # 本机路径形态。C: 盘只在用户目录下判定，避免误伤文档中的通用示例（C:\Program Files\...）。
    $rules.Add([pscustomobject]@{
            Name = 'absolute-path-outside-system-drive'
            Pattern = '(?<![A-Za-z0-9_])[D-Zd-z]:[\\/](?![\\/])'
            Message = '出现非系统盘绝对路径，疑似本机路径泄漏；请改用相对路径或 %LOCALAPPDATA% 等占位符'
        })
    $rules.Add([pscustomobject]@{
            Name = 'windows-user-profile-path'
            Pattern = '(?i)[A-Za-z]:[\\/]Users[\\/]'
            Message = '出现 Windows 用户目录绝对路径'
        })
    $rules.Add([pscustomobject]@{
            Name = 'unix-home-path'
            Pattern = '(?<![A-Za-z0-9_.-])/(?:home|Users)/[A-Za-z0-9._-]+/'
            Message = '出现 Unix 用户目录绝对路径'
        })

    foreach ($variant in $rootVariants) {
        $rules.Add([pscustomobject]@{
                Name = 'repository-absolute-path'
                Pattern = $variant
                Message = '出现当前仓库的绝对路径'
            })
    }

    # 凭据与密钥材料。
    $rules.Add([pscustomobject]@{
            Name = 'private-key-material'
            Pattern = '-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----'
            Message = '出现私钥内容'
        })
    $rules.Add([pscustomobject]@{
            Name = 'github-token'
            Pattern = '\b(?:ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|gho_[A-Za-z0-9]{20,})'
            Message = '出现 GitHub 访问令牌'
        })
    $rules.Add([pscustomobject]@{
            Name = 'cloud-access-key'
            Pattern = '\bAKIA[0-9A-Z]{16}\b'
            Message = '出现云访问密钥'
        })

    $violations = [System.Collections.Generic.List[object]]::new()

    function Add-Violation {
        param([string]$Path, [int]$LineNumber, [string]$RuleName, [string]$Message, [string]$Snippet)

        $violations.Add([pscustomobject]@{
                Path = $Path
                Line = $LineNumber
                Rule = $RuleName
                Message = $Message
                Snippet = $Snippet.Trim()
            })
    }

    function Test-LineContent {
        param([string]$Path, [int]$LineNumber, [string]$Line)

        if ([string]::IsNullOrWhiteSpace($Line)) {
            return
        }

        foreach ($rule in $rules) {
            if ([regex]::IsMatch($Line, $rule.Pattern)) {
                $snippet = if ($Line.Length -gt 160) { $Line.Substring(0, 160) + '…' } else { $Line }
                Add-Violation -Path $Path -LineNumber $LineNumber -RuleName $rule.Name -Message $rule.Message -Snippet $snippet
            }
        }
    }

    # 不允许进入版本库的文件形态。
    $forbiddenPathPattern = '(?i)(?:^|/)(?:bin|obj|artifacts|TestResults|publish|\.vs)/|(?:^|/)~\$|\.(?:tmp|orig|rej|bak)$|(?:^|/)\.update\.lock$'
    $forbiddenPathMessage = '提交了构建产物、临时文件或部署锁文件'

    function Test-TrackedPath {
        param([string]$Path)

        if ([regex]::IsMatch($Path, $forbiddenPathPattern)) {
            Add-Violation -Path $Path -LineNumber 0 -RuleName 'forbidden-path' -Message $forbiddenPathMessage -Snippet $Path
        }
    }

    if ($All) {
        $tracked = Invoke-Git -Arguments @('ls-files', '-z')
        $files = @(($tracked -join "`n") -split "`0" | Where-Object { $_ -ne '' })

        foreach ($file in $files) {
            Test-TrackedPath -Path $file
        }

        foreach ($rule in $rules) {
            $matches = @()
            try {
                $matches = & git -c core.quotepath=false grep -n -I -E -- $rule.Pattern 2>$null
            } catch {
                $matches = @()
            }

            if ($LASTEXITCODE -ne 0 -or -not $matches) {
                continue
            }

            foreach ($match in @($matches)) {
                if ($match -isnot [string]) {
                    continue
                }

                $parts = $match.Split(':', 3)
                if ($parts.Length -lt 3) {
                    continue
                }

                $lineNumber = 0
                [void][int]::TryParse($parts[1], [ref]$lineNumber)
                $snippet = if ($parts[2].Length -gt 160) { $parts[2].Substring(0, 160) + '…' } else { $parts[2] }
                Add-Violation -Path $parts[0] -LineNumber $lineNumber -RuleName $rule.Name -Message $rule.Message -Snippet $snippet
            }
        }
    }
    else {
        $diff = Invoke-Git -Arguments @('diff', '--cached', '--unified=0', '--no-color', '--diff-filter=ACMR')
        $currentPath = $null
        $newLine = 0

        foreach ($line in @($diff)) {
            if ($line -isnot [string]) {
                continue
            }

            if ($line.StartsWith('+++ b/')) {
                $currentPath = $line.Substring(6)
                continue
            }

            if ($line.StartsWith('+++ /dev/null')) {
                $currentPath = $null
                continue
            }

            if ($line.StartsWith('@@')) {
                $header = [regex]::Match($line, '^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@')
                if ($header.Success) {
                    $newLine = [int]$header.Groups[1].Value
                }
                continue
            }

            if ($null -eq $currentPath) {
                continue
            }

            if ($line.StartsWith('+')) {
                Test-LineContent -Path $currentPath -LineNumber $newLine -Line $line.Substring(1)
                $newLine++
            }
        }

        # 暂存区新增/重命名的文件也要检查路径本身。
        $stagedNames = Invoke-Git -Arguments @('diff', '--cached', '--name-only', '--diff-filter=ACMR')
        foreach ($name in @($stagedNames)) {
            if ($name -is [string] -and $name) {
                Test-TrackedPath -Path $name
            }
        }
    }
}
finally {
    Pop-Location
}

if ($violations.Count -eq 0) {
    $scope = if ($All) { '已跟踪文件' } else { '暂存区' }
    Write-Host "提交卫生检查通过（$scope）。" -ForegroundColor Green
    exit 0
}

Write-Host "发现 $($violations.Count) 处提交卫生问题：" -ForegroundColor Red
foreach ($group in ($violations | Group-Object Path | Sort-Object Name)) {
    Write-Host ""
    Write-Host $group.Name -ForegroundColor Yellow
    foreach ($violation in $group.Group) {
        $location = if ($violation.Line -gt 0) { "$($group.Name):$($violation.Line)" } else { $group.Name }
        Write-Host "  ${location}: [$($violation.Rule)] $($violation.Message)"
        if ($violation.Snippet) {
            Write-Host "    $($violation.Snippet)" -ForegroundColor DarkGray
        }
    }
}

Write-Host ""
Write-Host '提交被拒绝：请移除本机路径、临时产物或密钥后重试。' -ForegroundColor Red
exit 1
