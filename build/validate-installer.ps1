$ErrorActionPreference = "Stop"

$keyPath = Join-Path $env:RUNNER_TEMP "ci-signing.key"
$pubPath = Join-Path $env:RUNNER_TEMP "ci-signing.pub"
try {
    $rsa = [System.Security.Cryptography.RSA]::Create(3072)
    try {
        [System.IO.File]::WriteAllText($keyPath, $rsa.ExportPkcs8PrivateKeyPem())
        [System.IO.File]::WriteAllText($pubPath, $rsa.ExportSubjectPublicKeyInfoPem())
    }
    finally { $rsa.Dispose() }

    pwsh -NoProfile -File build/pack-release.ps1 -Version 1.0.0-ci -Channel nightly -OutputRoot artifacts/ci-signed -SigningKeyPath $keyPath -KeyId "ci" -PublicKeyPath $pubPath -SelfContained
    if ($LASTEXITCODE -ne 0) { throw "signed pack-release failed" }

    $release = "artifacts/ci-signed/1.0.0-ci"
    $installer = Join-Path $release "ECL.Installer-1.0.0-ci-win-x64.exe"
    $checksum = "$installer.sha256"
    if (-not (Test-Path $installer) -or -not (Test-Path $checksum)) {
        throw "Standalone installer or SHA-256 sidecar was not produced."
    }
    $expectedHash = (Get-Content $checksum -Raw).Trim().Split()[0]
    $actualHash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHash -ne $actualHash) { throw "Installer SHA-256 sidecar does not match the final EXE." }

    $media = Join-Path $release "installer-media"
    foreach ($file in @(
        "ECL.Bootstrapper.exe",
        "ECL.Uninstaller.exe",
        "update-manifest.json",
        "update-manifest.sig",
        "update-trust.json",
        "ECL-package.zip"
    )) {
        if (-not (Test-Path (Join-Path $media $file))) { throw "嵌入介质缺少 $file" }
    }

    $standaloneBootstrapper = Join-Path $release "bootstrapper/ECL.Bootstrapper.exe"
    $embeddedBootstrapper = Join-Path $media "ECL.Bootstrapper.exe"
    $standaloneHash = (Get-FileHash $standaloneBootstrapper -Algorithm SHA256).Hash
    $embeddedHash = (Get-FileHash $embeddedBootstrapper -Algorithm SHA256).Hash
    if ($standaloneHash -ne $embeddedHash) { throw "独立与内嵌 Bootstrapper 并非同一构建。" }

    $package = Join-Path $release "ECL-1.0.0-ci-win-x64.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package)
    try {
        $names = $zip.Entries | ForEach-Object { $_.FullName }
        foreach ($required in @("ECL.exe", "ECL.LogHost.exe", "ECL.Uninstaller.exe")) {
            if ($names -notcontains $required) { throw "程序包缺少 $required" }
        }
    }
    finally { $zip.Dispose() }

    $target = Join-Path $env:RUNNER_TEMP "ecl-ci-install"
    # No --media argument: verify that the single-file EXE releases its embedded media.
    $process = Start-Process -FilePath $installer -ArgumentList @("--silent", "--accept-unverified", "--install-root", $target) -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "静默安装失败：$($process.ExitCode)" }
    if (-not (Test-Path (Join-Path $target "current.json"))) { throw "安装后缺少 current.json" }
    $versionDir = Join-Path $target "versions/1.0.0-ci"
    if (-not (Test-Path (Join-Path $versionDir "ECL.exe"))) { throw "安装后缺少版本可执行文件" }
    if (-not (Test-Path (Join-Path $target "install-manifest.json"))) { throw "安装后缺少安装清单" }
    if (-not (Test-Path (Join-Path $target "ECL.Uninstaller.exe"))) { throw "安装后缺少独立卸载器" }

    # The standalone uninstaller must clean program files without launching ECL/Bootstrapper.
    $uninstall = Start-Process -FilePath (Join-Path $target "ECL.Uninstaller.exe") -ArgumentList @("--silent", "--install-root", $target) -PassThru -Wait
    if ($uninstall.ExitCode -ne 0) { throw "静默卸载失败：$($uninstall.ExitCode)" }
    if (Test-Path (Join-Path $target "current.json")) { throw "卸载后仍存在 current.json" }
    if (Test-Path (Join-Path $versionDir "ECL.exe")) { throw "卸载后仍存在版本可执行文件" }

    # A tampered Manifest must fail closed and must not create a current pointer.
    $bad = Join-Path $env:RUNNER_TEMP "ecl-ci-bad-media"
    Copy-Item $media $bad -Recurse
    $manifest = Join-Path $bad "update-manifest.json"
    (Get-Content $manifest -Raw).Replace('1.0.0-ci', '9.9.9-tampered') | Set-Content $manifest -Encoding utf8NoBOM
    $badTarget = Join-Path $env:RUNNER_TEMP "ecl-ci-install-bad"
    $badProcess = Start-Process -FilePath $installer -ArgumentList @("--silent", "--accept-unverified", "--install-root", $badTarget, "--media", $bad) -PassThru -Wait
    if ($badProcess.ExitCode -eq 0) { throw "篡改介质被错误接受" }
    if (Test-Path (Join-Path $badTarget "current.json")) { throw "篡改介质不应生成 current 指针" }

    Write-Host "Standalone Installer, embedded media, Bootstrapper identity, silent install/uninstall and tamper rejection passed."
}
finally {
    Remove-Item $keyPath, $pubPath -Force -ErrorAction SilentlyContinue
}
