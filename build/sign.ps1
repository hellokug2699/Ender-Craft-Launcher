param(
    [Parameter(Mandatory = $true)]
    [string[]]$Files,

    [string]$PfxPath = $env:ECL_SIGNING_PFX,

    [string]$PfxPassword = $env:ECL_SIGNING_PASSWORD,

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PfxPath) -or [string]::IsNullOrWhiteSpace($PfxPassword)) {
    throw @"
签名证书来自 private Core 仓库（Ender-Craft-Launcher-Core）。
请设置环境变量 ECL_SIGNING_PFX 与 ECL_SIGNING_PASSWORD，或显式传入 -PfxPath / -PfxPassword。
public 仓库不保存证书与口令。
"@
}

if (-not (Test-Path $PfxPath)) {
    throw "证书文件不存在：$PfxPath"
}

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { $signtool = $candidate.FullName }
    }
}

if (-not $signtool) {
    throw "未找到 signtool.exe，请安装 Windows SDK。"
}

$resolvedFiles = $Files | ForEach-Object { (Resolve-Path $_).Path }
$signtoolPath = if ($signtool -is [System.Management.Automation.ApplicationInfo]) { $signtool.Source } else { $signtool }

foreach ($file in $resolvedFiles) {
    Write-Host "签名：$file" -ForegroundColor Cyan
    & $signtoolPath sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $PfxPath /p $PfxPassword $file
    if ($LASTEXITCODE -ne 0) { throw "签名失败：$file" }
}

Write-Host "签名完成。" -ForegroundColor Green
