<#
.SYNOPSIS
    为官方 Additions 目录生成 detached RSA-PSS 签名。

.DESCRIPTION
    客户端通过 build/additions-trust.json 中的公钥验证目录签名，随后信任目录条目中的
    包 SHA-256。私钥只能位于私有 Core 仓库或受控发布环境，不得进入公开仓库或客户端。
    签名算法与 ADR 0016 / 0024 的更新签名保持一致：RSA-PSS-SHA256。
#>
param(
    [string]$CatalogPath = "additions/catalog.json",

    [string]$SignaturePath = "additions/catalog.json.sig",

    [string]$SigningKeyPath = $env:ECL_ADDITIONS_SIGNING_KEY,

    [string]$KeyId = "ecl-additions"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $CatalogPath)) {
    throw "目录文件不存在：$CatalogPath"
}

if ([string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    throw "未提供扩展目录签名私钥。设置 ECL_ADDITIONS_SIGNING_KEY 或 -SigningKeyPath。"
}

if (-not (Test-Path $SigningKeyPath)) {
    throw "签名私钥不存在：$SigningKeyPath"
}

$privatePem = Get-Content $SigningKeyPath -Raw
$catalogBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $CatalogPath))

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($privatePem)
    $signature = $rsa.SignData(
        $catalogBytes,
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
    $SignaturePath,
    ($envelope | ConvertTo-Json),
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "已生成扩展目录签名：$SignaturePath" -ForegroundColor Green
