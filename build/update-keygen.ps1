param(
    [Parameter(Mandatory = $true)]
    [string]$PrivateKeyPath,

    [Parameter(Mandatory = $true)]
    [string]$PublicKeyPath,

    [string]$TrustStorePath = "build/update-trust.json",

    [string]$KeyId = "ecl-release"
)

$ErrorActionPreference = "Stop"

if (Test-Path $PrivateKeyPath) {
    throw "拒绝覆盖已存在的私钥：$PrivateKeyPath"
}

$privateDirectory = Split-Path -Parent $PrivateKeyPath
if (-not [string]::IsNullOrWhiteSpace($privateDirectory)) {
    New-Item -ItemType Directory -Force -Path $privateDirectory | Out-Null
}
$publicDirectory = Split-Path -Parent $PublicKeyPath
if (-not [string]::IsNullOrWhiteSpace($publicDirectory)) {
    New-Item -ItemType Directory -Force -Path $publicDirectory | Out-Null
}

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $privatePem = $rsa.ExportPkcs8PrivateKeyPem()
    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()
}
finally {
    $rsa.Dispose()
}

[System.IO.File]::WriteAllText($PrivateKeyPath, $privatePem, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText($PublicKeyPath, $publicPem, [System.Text.UTF8Encoding]::new($false))

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
    $TrustStorePath,
    ($trust | ConvertTo-Json -Depth 5),
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "已生成更新签名密钥：" -ForegroundColor Green
Write-Host "  私钥（只放 private Core 仓库/受控发布环境）：$PrivateKeyPath"
Write-Host "  公钥（可提交到 public 仓库）：$PublicKeyPath"
Write-Host "  信任库：$TrustStorePath"
Write-Host ""
Write-Host "签名私钥不得进入 public 仓库、客户端或安装包。" -ForegroundColor Yellow
