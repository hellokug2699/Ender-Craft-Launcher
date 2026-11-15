param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [ValidateSet("main", "dev", "nightly", "stable", "beta")]
    [string]$Channel = "dev",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [string]$OutputRoot = "artifacts/release",

    [string]$SigningKeyPath = $env:ECL_UPDATE_SIGNING_KEY,

    [string]$KeyId = "ecl-release",

    [string]$PublicKeyPath,

    [string]$TrustStorePath,

    [string]$CodeSigningPfxPath,

    [string]$CodeSigningPasswordPath,

    [string]$ReleaseNotes = "",

    [switch]$Unsigned
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$codeSigningCertificate = $null
if (-not [string]::IsNullOrWhiteSpace($CodeSigningPfxPath)) {
    if (-not (Test-Path $CodeSigningPfxPath)) { throw "代码签名 PFX 不存在：$CodeSigningPfxPath" }
    if ([string]::IsNullOrWhiteSpace($CodeSigningPasswordPath) -or -not (Test-Path $CodeSigningPasswordPath)) {
        throw "代码签名需要 -CodeSigningPasswordPath 指向 PFX 密码文件。"
    }
    $secure = ConvertTo-SecureString (Get-Content $CodeSigningPasswordPath -Raw).Trim() -AsPlainText -Force
    $codeSigningCertificate = Get-PfxCertificate -FilePath $CodeSigningPfxPath -Password $secure
}

function Invoke-CodeSign([string]$Path) {
    if ($null -eq $codeSigningCertificate -or -not (Test-Path $Path)) { return }
    $signature = Set-AuthenticodeSignature -FilePath $Path -Certificate $codeSigningCertificate -HashAlgorithm SHA256
    if ($signature.Status -eq 'NotSigned') { throw "代码签名失败：$Path" }
}

try {
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$') {
        throw "版本号必须是 semver（例如 1.0.1 或 1.0.1-rc.1）：$Version"
    }

    $channel = switch ($Channel) { "stable" { "main" } "beta" { "dev" } default { $Channel } }
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

    # LogHost 与主程序一起部署：活动会话由独立进程持有管道。
    dotnet publish src/ECL.LogHost/ECL.LogHost.csproj `
        -c Release `
        -r $Runtime `
        --self-contained $selfContainedValue `
        -o $publish `
        $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "发布 ECL.LogHost 失败" }

    # 独立卸载器发布为单文件；安装器同时把它放到安装根目录，避免依赖同目录 DLL。
    $uninstallerSingle = Join-Path $releaseRoot "uninstaller-single"
    New-Item -ItemType Directory -Force -Path $uninstallerSingle | Out-Null
    dotnet publish src/ECL.Uninstaller/ECL.Uninstaller.csproj `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $uninstallerSingle `
        $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "发布 ECL.Uninstaller 失败" }
    Copy-Item (Join-Path $uninstallerSingle "ECL.Uninstaller.exe") $publish -Force

    # 发布链中的可执行文件在最终产物确定后签名；签名后不得再修改。
    Invoke-CodeSign (Join-Path $publish "ECL.exe")
    Invoke-CodeSign (Join-Path $publish "ECL.LogHost.exe")
    Invoke-CodeSign (Join-Path $publish "ECL.Uninstaller.exe")

    # 公开证书与固定指纹随程序部署，供设置页展示与用户显式信任。
    Copy-Item (Join-Path $root "build/code-signing/*") $publish -Force

    # ADR 0032：Additions 目录公钥随客户端部署，用于验证官方扩展目录签名。
    Copy-Item (Join-Path $root "build/additions-trust.json") $publish -Force

    # Bootstrapper 的唯一分发形态为自包含单文件 EXE。
    # 独立下载与完整安装介质共用同一个二进制，避免同版本运行时契约分叉。
    dotnet publish src/ECL.Bootstrapper/ECL.Bootstrapper.csproj `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $bootstrap `
        $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "发布自包含单文件 ECL.Bootstrapper 失败" }
    Invoke-CodeSign (Join-Path $bootstrap "ECL.Bootstrapper.exe")

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
        channel               = $channel
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

    # 无论 Manifest 是否使用测试签名，完整安装介质都必须携带公开信任库。
    if (-not (Test-Path $trustPath) -and -not [string]::IsNullOrWhiteSpace($TrustStorePath)) {
        if (-not (Test-Path $TrustStorePath)) { throw "更新信任库不存在：$TrustStorePath" }
        Copy-Item $TrustStorePath $trustPath -Force
    }
    if (-not (Test-Path $trustPath)) {
        throw "完整安装介质缺少 update-trust.json；请提供 -TrustStorePath。"
    }

    # ADR 0071：先组装完整部署介质，再将其作为资源嵌入独立 Installer。
    # 用户只需下载并运行一个 EXE；Installer 在临时目录释放介质，再委托 Bootstrapper。
    $installerMedia = Join-Path $releaseRoot "installer-media"
    if (Test-Path $installerMedia) { Remove-Item $installerMedia -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $installerMedia | Out-Null

    Copy-Item (Join-Path $bootstrap "ECL.Bootstrapper.exe") $installerMedia -Force
    Copy-Item (Join-Path $publish "ECL.Uninstaller.exe") $installerMedia -Force
    Copy-Item $manifestPath $installerMedia -Force
    if (Test-Path $trustPath) { Copy-Item $trustPath $installerMedia -Force }
    if (Test-Path $signaturePath) { Copy-Item $signaturePath $installerMedia -Force }
    Copy-Item $zipPath (Join-Path $installerMedia "ECL-package.zip") -Force

    $bundlePath = Join-Path $releaseRoot "installer-bundle.zip"
    if (Test-Path $bundlePath) { Remove-Item $bundlePath -Force }
    Compress-Archive -Path (Join-Path $installerMedia "*") -DestinationPath $bundlePath -CompressionLevel Optimal

    $installerPublish = Join-Path $releaseRoot "installer-publish"
    New-Item -ItemType Directory -Force -Path $installerPublish | Out-Null
    dotnet publish src/ECL.Installer/ECL.Installer.csproj `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EclInstallerBundle="$bundlePath" `
        -o $installerPublish `
        $versionProperty
    if ($LASTEXITCODE -ne 0) { throw "发布自包含单文件 ECL.Installer 失败" }

    $installerExe = Join-Path $installerPublish "ECL.Installer.exe"
    Invoke-CodeSign $installerExe
    $installerAsset = Join-Path $releaseRoot "ECL.Installer-$Version-$Runtime.exe"
    Copy-Item $installerExe $installerAsset -Force
    $installerHash = (Get-FileHash $installerAsset -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = "$installerAsset.sha256"
    [System.IO.File]::WriteAllText($checksumPath, "$installerHash  $([System.IO.Path]::GetFileName($installerAsset))", [System.Text.UTF8Encoding]::new($false))

    Write-Host "发布目录：$releaseRoot" -ForegroundColor Green
    Write-Host "  包：$zipPath"
    Write-Host "  清单：$manifestPath"
    Write-Host "  自包含引导程序：$(Join-Path $bootstrap 'ECL.Bootstrapper.exe')"
    Write-Host "  独立安装程序：$installerAsset"
    Write-Host "  官方 SHA-256：$checksumPath"
    Write-Host "  内嵌部署介质：$bundlePath"
}
finally {
    Pop-Location
}
