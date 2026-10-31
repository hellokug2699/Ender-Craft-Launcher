param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("stable", "beta", "dev")]
    [string]$Channel = "dev",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [string]$OutputRoot = "artifacts/release",

    [string]$SigningKeyPath = $env:ECL_UPDATE_SIGNING_KEY,

    [string]$KeyId = "ecl-release",

    [string]$PublicKeyPath,

    [string]$TrustStorePath,

    [string]$ReleaseNotes = "",

    [switch]$Unsigned
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') {
        throw "版本号必须是 semver（例如 1.0.1 或 1.0.1-rc.1）：$Version"
    }

    $architecture = if ($Runtime -eq "win-arm64") { "arm64" } else { "x64" }
    $releaseRoot = Join-Path $root (Join-Path $OutputRoot $Version)
    $publish = Join-Path $releaseRoot "app"
    $bootstrap = Join-Path $releaseRoot "bootstrapper"
    $zipName = "ECL-$Version-$Runtime.zip"
    $zipPath = Join-Path $releaseRoot $zipName
    $manifestPath = Join-Path $releaseRoot "update-manifest.json"
    $signaturePath = Join-Path $releaseRoot "update-manifest.sig"
    $trustPath = Join-Path $releaseRoot "update-trust.json"

    if (Test-Path $releaseRoot) { Remove-Item $releaseRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $publish, $bootstrap | Out-Null

    $versionProperty = "/p:Version=$Version"
    $selfContainedValue = if ($SelfContained) { "true" } else { "false" }

    dotnet publish src/ECL.App/ECL.App.csproj `
        -c Release `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -o $publish `
        $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "发布 ECL.App 失败" }

    dotnet publish src/ECL.Bootstrapper/ECL.Bootstrapper.csproj `
        -c Release `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -o $bootstrap `
        $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "发布 ECL.Bootstrapper 失败" }

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zipPath -CompressionLevel Optimal
    $zipBytes = [System.IO.File]::ReadAllBytes($zipPath)
    $zipHash = [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($zipBytes)
    ).ToLowerInvariant()

    $files = @()
    Get-ChildItem $publish -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $files += [ordered]@{
            path   = $relative
            size   = $_.Length
            sha256 = $hash
        }
    }

    $manifest = [ordered]@{
        schemaVersion         = 1
        product               = "ECL"
        channel               = $Channel
        version               = $Version
        platform              = "windows"
        architecture          = $architecture
        minimumUpdaterVersion = "0.1.0"
        package               = [ordered]@{
            file   = $zipName
            size   = $zipBytes.Length
            sha256 = $zipHash
        }
        files                 = $files
        removedFiles          = @()
        releaseNotes          = $ReleaseNotes
        publishedAt           = [DateTimeOffset]::UtcNow.ToString("o")
    }

    $json = $manifest | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($manifestPath, $json, [System.Text.UTF8Encoding]::new($false))

    if ($Unsigned) {
        Write-Host "已生成未签名清单：$manifestPath" -ForegroundColor Yellow
        Write-Host "发布流程必须使用受控私钥签名；未签名清单不会通过客户端验证。" -ForegroundColor Yellow
    }
    else {
        if ([string]::IsNullOrWhiteSpace($SigningKeyPath)) {
            throw "未提供更新签名私钥。设置 ECL_UPDATE_SIGNING_KEY 或 -SigningKeyPath；仅本地检查可用 -Unsigned。"
        }

        if (-not (Test-Path $SigningKeyPath)) { throw "签名私钥不存在：$SigningKeyPath" }

        $privatePem = Get-Content $SigningKeyPath -Raw
        $rsa = [System.Security.Cryptography.RSA]::Create()
        try {
            $rsa.ImportFromPem($privatePem)
            $signature = $rsa.SignData(
                [System.IO.File]::ReadAllBytes($manifestPath),
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pss
            )
        }
        finally {
            $rsa.Dispose()
        }

        $envelope = [ordered]@{
            schemaVersion = 1
            keyId         = $KeyId
            algorithm     = "RSA-PSS-SHA256"
            signature     = [System.Convert]::ToBase64String($signature)
        }
        [System.IO.File]::WriteAllText(
            $signaturePath,
            ($envelope | ConvertTo-Json),
            [System.Text.UTF8Encoding]::new($false)
        )
        Write-Host "已生成签名：$signaturePath" -ForegroundColor Green

        if (-not [string]::IsNullOrWhiteSpace($PublicKeyPath)) {
            if (-not (Test-Path $PublicKeyPath)) { throw "更新公钥不存在：$PublicKeyPath" }
            $publicPem = Get-Content $PublicKeyPath -Raw
            $trust = [ordered]@{
                schemaVersion = 1
                keys          = @(
                    [ordered]@{
                        keyId     = $KeyId
                        algorithm = "RSA-PSS-SHA256"
                        publicKey = $publicPem
                    }
                )
            }
            [System.IO.File]::WriteAllText(
                $trustPath,
                ($trust | ConvertTo-Json -Depth 5),
                [System.Text.UTF8Encoding]::new($false)
            )
            Write-Host "已生成信任库：$trustPath" -ForegroundColor Green
        }
        elseif (-not [string]::IsNullOrWhiteSpace($TrustStorePath)) {
            if (-not (Test-Path $TrustStorePath)) { throw "更新信任库不存在：$TrustStorePath" }
            Copy-Item $TrustStorePath $trustPath -Force
            Write-Host "已复制信任库：$trustPath" -ForegroundColor Green
        }
    }

    Write-Host "发布目录：$releaseRoot" -ForegroundColor Green
    Write-Host "  包：$zipPath"
    Write-Host "  清单：$manifestPath"
    Write-Host "  引导程序：$bootstrap/ECL.Bootstrapper.exe"
}
finally {
    Pop-Location
}
