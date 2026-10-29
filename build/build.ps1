param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Test,

    [switch]$Pack
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    Write-Host "==> CurseForge：通过 ECL 代理访问，构建不包含 API key" -ForegroundColor DarkGray

    Write-Host "==> Restore" -ForegroundColor Cyan
    dotnet restore ECL.sln
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败" }

    Write-Host "==> Build ($Configuration)" -ForegroundColor Cyan
    dotnet build ECL.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败" }

    if ($Test) {
        Write-Host "==> Test" -ForegroundColor Cyan
        dotnet test tests/ECL.Tests/ECL.Tests.csproj -c $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw "测试失败" }
    }

    if ($Pack) {
        $output = Join-Path $root "artifacts/publish/win-x64"
        $archive = Join-Path $root "artifacts/ECL-win-x64.zip"

        Write-Host "==> Publish ($Configuration, win-x64)" -ForegroundColor Cyan
        dotnet publish src/ECL.App/ECL.App.csproj `
            -c $Configuration `
            -r win-x64 `
            --self-contained false `
            -o $output
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

        if (Test-Path $archive) { Remove-Item $archive -Force }
        Compress-Archive -Path (Join-Path $output "*") -DestinationPath $archive -Force
        Write-Host "输出：$archive" -ForegroundColor Green
        Write-Host "签名：./build/sign.ps1 -Files '$output/ECL.exe'" -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
