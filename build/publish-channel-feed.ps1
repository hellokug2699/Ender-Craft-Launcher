param(
    [Parameter(Mandatory = $true)][ValidateSet("main", "dev", "nightly")][string]$Channel,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$SigningKeyPath,
    [Parameter(Mandatory = $true)][string]$KeyId,
    [Parameter(Mandatory = $true)][string]$TrustStorePath,
    [string]$FeedRoot = "artifacts/channel-feed",
    [string]$Repository = "hellokug2699/Ender-Craft-Launcher"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw "GH_TOKEN is required." }
if ([string]::IsNullOrWhiteSpace($env:CLOUDFLARE_ACCOUNT_ID) -or
    [string]::IsNullOrWhiteSpace($env:CLOUDFLARE_API_TOKEN)) {
    throw "Cloudflare Pages credentials are required."
}
foreach ($required in @($SigningKeyPath, $TrustStorePath)) {
    if (-not (Test-Path $required)) { throw "Required signing material not found: $required" }
}

$ReleaseDirectory = (Resolve-Path $ReleaseDirectory).Path
$FeedRoot = [System.IO.Path]::GetFullPath($FeedRoot)
$workRoot = Join-Path $env:RUNNER_TEMP "ecl-feed-publish"
$storeRoot = Join-Path $workRoot "packages-store"
$legacyRoot = Join-Path $workRoot "legacy"
if (Test-Path $workRoot) { Remove-Item $workRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $workRoot, $legacyRoot, $FeedRoot | Out-Null

$trust = Get-Content $TrustStorePath -Raw | ConvertFrom-Json
$privatePem = Get-Content $SigningKeyPath -Raw
$auth = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$env:GH_TOKEN"))

function Invoke-Git([string[]]$Arguments) {
    & git -C $storeRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Test-ManifestSignature([string]$ManifestPath, [string]$SignaturePath) {
    $envelope = Get-Content $SignaturePath -Raw | ConvertFrom-Json
    if ($envelope.schemaVersion -ne 1 -or $envelope.algorithm -ne "RSA-PSS-SHA256") {
        throw "Unsupported signature envelope."
    }
    $key = @($trust.keys | Where-Object { $_.keyId -ceq $envelope.keyId } | Select-Object -First 1)
    if ($key.Count -ne 1) { throw "Unknown signing key id: $($envelope.keyId)" }
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem([string]$key[0].publicKey)
        $valid = $rsa.VerifyData(
            [System.IO.File]::ReadAllBytes($ManifestPath),
            [Convert]::FromBase64String([string]$envelope.signature),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally { $rsa.Dispose() }
    if (-not $valid) { throw "Existing channel Manifest signature verification failed." }
}

function Write-SignedManifest([object]$Manifest, [string]$Directory) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $manifestPath = Join-Path $Directory "update-manifest.json"
    $signaturePath = Join-Path $Directory "update-manifest.sig"
    $json = $Manifest | ConvertTo-Json -Depth 24
    [System.IO.File]::WriteAllText($manifestPath, $json, [System.Text.UTF8Encoding]::new($false))

    $rsa = [System.Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($privatePem)
        $signature = $rsa.SignData(
            [System.IO.File]::ReadAllBytes($manifestPath),
            [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally { $rsa.Dispose() }

    $envelope = [ordered]@{
        schemaVersion = 1
        keyId = $KeyId
        algorithm = "RSA-PSS-SHA256"
        signature = [Convert]::ToBase64String($signature)
    }
    [System.IO.File]::WriteAllText(
        $signaturePath,
        ($envelope | ConvertTo-Json -Compress),
        [System.Text.UTF8Encoding]::new($false))
}

function Add-PackageToStore(
    [string]$SourcePath,
    [string]$PackageChannel,
    [string]$PackageVersion,
    [string]$Architecture) {
    if (-not (Test-Path $SourcePath)) { throw "Package not found: $SourcePath" }
    $item = Get-Item $SourcePath
    if ($item.Length -le 0 -or $item.Length -ge 90MB) {
        throw "Package must be non-empty and smaller than 90 MiB for GitHub repository storage."
    }
    $digest = (Get-FileHash $SourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $fileName = "ECL-$PackageVersion-win-$Architecture-$digest.zip"
    $relative = "packages/$PackageChannel/windows/$Architecture/$PackageVersion/$fileName"
    $target = Join-Path $storeRoot ($relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    $directory = Split-Path -Parent $target

    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $remote = & git -C $storeRoot ls-remote --heads origin packages
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect packages branch." }
        if ($remote) {
            Invoke-Git @("fetch", "--depth=1", "origin", "packages")
            Invoke-Git @("checkout", "-B", "packages", "FETCH_HEAD")
        }
        elseif ((& git -C $storeRoot branch --show-current) -ne "packages") {
            Invoke-Git @("checkout", "--orphan", "packages")
        }

        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        if (Test-Path $target) {
            $existing = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($existing -ne $digest) { throw "Immutable package path collision: $relative" }
            $commit = (& git -C $storeRoot rev-parse HEAD).Trim()
            return "https://raw.githubusercontent.com/$Repository/$commit/$relative"
        }

        Copy-Item $SourcePath $target
        Invoke-Git @("add", "--", $relative)
        Invoke-Git @("-c", "user.name=github-actions[bot]", "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com", "commit", "-m", "packages($PackageChannel): add $PackageVersion $Architecture $digest")
        & git -C $storeRoot push origin "HEAD:refs/heads/packages"
        if ($LASTEXITCODE -eq 0) {
            $commit = (& git -C $storeRoot rev-parse HEAD).Trim()
            return "https://raw.githubusercontent.com/$Repository/$commit/$relative"
        }

        Write-Warning "packages branch changed concurrently; retrying append (attempt $attempt/6)."
        if ($attempt -eq 6) { throw "Unable to append package after concurrent updates." }
        Invoke-Git @("fetch", "--depth=1", "origin", "packages")
        Invoke-Git @("reset", "--hard", "FETCH_HEAD")
    }
    throw "Package append did not complete."
}

function Confirm-PackageUrl([string]$Url, [long]$ExpectedSize, [string]$ExpectedHash) {
    $uri = [Uri]$Url
    if ($uri.Scheme -ne "https" -or $uri.Host -ne "raw.githubusercontent.com" -or
        $uri.AbsolutePath -notmatch "^/$([regex]::Escape($Repository))/[0-9a-f]{40}/packages/") {
        throw "Package URL is outside the immutable GitHub Raw repository namespace."
    }
    $download = Join-Path $workRoot "verify-$([Guid]::NewGuid().ToString('N')).zip"
    Invoke-WebRequest -Uri $Url -OutFile $download -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
    $file = Get-Item $download
    $hash = (Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant()
    Remove-Item $download -Force
    if ($file.Length -ne $ExpectedSize -or $hash -ne $ExpectedHash) {
        throw "Anonymous Package download did not match the signed size/SHA-256."
    }
}

# Clone without checking out the source tree. The packages branch starts as an
# orphan root commit so application source history is never copied into storage.
$cloneArgs = @("-c", "http.extraheader=AUTHORIZATION: basic $auth", "clone", "--filter=blob:none", "--no-checkout", "https://github.com/$Repository.git", $storeRoot)
& git @cloneArgs
if ($LASTEXITCODE -ne 0) { throw "Unable to clone package repository metadata." }
& git -C $storeRoot config http.https://github.com/.extraheader "AUTHORIZATION: basic $auth"
& git -C $storeRoot config user.name "github-actions[bot]"
& git -C $storeRoot config user.email "41898282+github-actions[bot]@users.noreply.github.com"
$hasPackages = & git -C $storeRoot ls-remote --heads origin packages
if ($LASTEXITCODE -ne 0) { throw "Unable to inspect package branch." }
if ($hasPackages) {
    Invoke-Git @("fetch", "--depth=1", "origin", "packages")
    Invoke-Git @("checkout", "-B", "packages", "FETCH_HEAD")
}
else {
    Invoke-Git @("checkout", "--orphan", "packages")
}

# Preserve all existing channels. On the first deployment, migrate each channel's
# current signed Release package into the immutable package store before Pages is
# switched. Later deployments reuse the already verified Production Feed.
foreach ($candidate in @("main", "dev", "nightly")) {
    if ($candidate -eq $Channel) { continue }
    $channelDirectory = Join-Path $FeedRoot $candidate
    $existingManifest = Join-Path $channelDirectory "update-manifest.json"
    $existingSignature = Join-Path $channelDirectory "update-manifest.sig"
    $isLive = $false
    try {
        New-Item -ItemType Directory -Force -Path $channelDirectory | Out-Null
        Invoke-WebRequest -Uri "https://updates.endercraftlauncher.top/$candidate/update-manifest.json" -OutFile $existingManifest -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
        Invoke-WebRequest -Uri "https://updates.endercraftlauncher.top/$candidate/update-manifest.sig" -OutFile $existingSignature -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
        Test-ManifestSignature $existingManifest $existingSignature
        $existing = Get-Content $existingManifest -Raw | ConvertFrom-Json
        $existingPackageUri = [Uri]$existing.package.file
        $expectedPackagePattern = "^/$([regex]::Escape($Repository))/[0-9a-f]{40}/packages/$candidate/windows/$([regex]::Escape([string]$existing.architecture))/$([regex]::Escape([string]$existing.version))/ECL-$([regex]::Escape([string]$existing.version))-win-$([regex]::Escape([string]$existing.architecture))-$([regex]::Escape([string]$existing.package.sha256))\.zip$"
        if ($existing.channel -ne $candidate -or
            $existingPackageUri.Scheme -ne "https" -or
            $existingPackageUri.Host -ne "raw.githubusercontent.com" -or
            $existingPackageUri.AbsolutePath -notmatch $expectedPackagePattern) {
            throw "Existing Production Feed is not a valid migrated channel."
        }
        Confirm-PackageUrl $existingPackageUri.AbsoluteUri ([long]$existing.package.size) ([string]$existing.package.sha256)
        $isLive = $true
    }
    catch {
        Write-Host "No usable Production Feed for $candidate; migrating the current signed GitHub Release."
    }

    if (-not $isLive) {
        $legacyDirectory = Join-Path $legacyRoot $candidate
        New-Item -ItemType Directory -Force -Path $legacyDirectory | Out-Null
        & gh release download $candidate --pattern "update-manifest.json" --pattern "update-manifest.sig" --dir $legacyDirectory
        if ($LASTEXITCODE -ne 0) { throw "No existing signed release feed is available to seed channel $candidate." }
        $legacyManifestPath = Join-Path $legacyDirectory "update-manifest.json"
        $legacySignaturePath = Join-Path $legacyDirectory "update-manifest.sig"
        Test-ManifestSignature $legacyManifestPath $legacySignaturePath
        $legacyManifest = Get-Content $legacyManifestPath -Raw | ConvertFrom-Json
        if ($legacyManifest.channel -ne $candidate) { throw "Legacy Release channel mismatch for $candidate." }
        $legacyPackageName = [string]$legacyManifest.package.file
        if ([System.IO.Path]::GetFileName($legacyPackageName) -ne $legacyPackageName) {
            throw "Legacy Manifest package filename is not a leaf name."
        }
        & gh release download $candidate --pattern $legacyPackageName --dir $legacyDirectory
        if ($LASTEXITCODE -ne 0) { throw "Unable to download legacy Package for $candidate." }
        $legacyPackagePath = Join-Path $legacyDirectory $legacyPackageName
        $legacyHash = (Get-FileHash $legacyPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($legacyHash -ne ([string]$legacyManifest.package.sha256).ToLowerInvariant() -or
            (Get-Item $legacyPackagePath).Length -ne [long]$legacyManifest.package.size) {
            throw "Legacy Package failed SHA-256/size validation for $candidate."
        }
        $legacyArchitecture = [string]$legacyManifest.architecture
        $legacyVersion = [string]$legacyManifest.version
        $legacyUrl = Add-PackageToStore $legacyPackagePath $candidate $legacyVersion $legacyArchitecture
        Confirm-PackageUrl $legacyUrl ([long]$legacyManifest.package.size) ([string]$legacyManifest.package.sha256)
        $legacyManifest.package.file = $legacyUrl
        Write-SignedManifest $legacyManifest $channelDirectory
    }
}

# Publish the current build's package only after it has passed local size/hash checks.
$currentManifestPath = Join-Path $ReleaseDirectory "update-manifest.json"
$currentManifest = Get-Content $currentManifestPath -Raw | ConvertFrom-Json
if ($currentManifest.channel -ne $Channel -or $currentManifest.version -ne $Version) {
    throw "Current build Manifest channel/version mismatch."
}
$currentPackagePath = Join-Path $ReleaseDirectory ([string]$currentManifest.package.file)
$currentPackageHash = (Get-FileHash $currentPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($currentPackageHash -ne ([string]$currentManifest.package.sha256).ToLowerInvariant() -or
    (Get-Item $currentPackagePath).Length -ne [long]$currentManifest.package.size) {
    throw "Current build Package failed SHA-256/size validation."
}
$currentUrl = Add-PackageToStore $currentPackagePath $Channel $Version ([string]$currentManifest.architecture)
$currentManifest.package.file = $currentUrl
$currentDirectory = Join-Path $FeedRoot $Channel
Write-SignedManifest $currentManifest $currentDirectory

# Verify the signed output before deploying. The feed contains all three channels.
Test-ManifestSignature (Join-Path $currentDirectory "update-manifest.json") (Join-Path $currentDirectory "update-manifest.sig")
Confirm-PackageUrl $currentUrl ([long]$currentManifest.package.size) ([string]$currentManifest.package.sha256)

foreach ($candidate in @("main", "dev", "nightly")) {
    foreach ($file in @("update-manifest.json", "update-manifest.sig")) {
        $path = Join-Path (Join-Path $FeedRoot $candidate) $file
        if (-not (Test-Path $path)) { throw "Feed deployment is incomplete: $candidate/$file is missing." }
    }
}

# Wrangler Direct Upload replaces the complete static site in one Production
# deployment. No Pages Functions, Workers, or Package binaries are uploaded.
npx --yes wrangler@4 pages deploy $FeedRoot --project-name=ender-craft-launcher-feed --branch=main
if ($LASTEXITCODE -ne 0) { throw "Cloudflare Pages Production deployment failed." }

foreach ($candidate in @("main", "dev", "nightly")) {
    foreach ($extension in @("json", "sig")) {
        $file = if ($extension -eq "json") { "update-manifest.json" } else { "update-manifest.sig" }
        $url = "https://updates.endercraftlauncher.top/$candidate/$file"
        $response = Invoke-WebRequest -Uri $url -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
        if ($response.StatusCode -ne 200) { throw "Feed smoke test failed: $url" }
    }

    $verifyDirectory = Join-Path $workRoot "deployed-$candidate"
    New-Item -ItemType Directory -Force -Path $verifyDirectory | Out-Null
    $verifyManifest = Join-Path $verifyDirectory "update-manifest.json"
    $verifySignature = Join-Path $verifyDirectory "update-manifest.sig"
    Invoke-WebRequest -Uri "https://updates.endercraftlauncher.top/$candidate/update-manifest.json" -OutFile $verifyManifest -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
    Invoke-WebRequest -Uri "https://updates.endercraftlauncher.top/$candidate/update-manifest.sig" -OutFile $verifySignature -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
    Test-ManifestSignature $verifyManifest $verifySignature
    $deployed = Get-Content $verifyManifest -Raw | ConvertFrom-Json
    if ($deployed.channel -ne $candidate) { throw "Deployed channel mismatch: $candidate." }
    Confirm-PackageUrl ([string]$deployed.package.file) ([long]$deployed.package.size) ([string]$deployed.package.sha256)
}

Write-Host "All three signed channel feeds are deployed. Current package URL: $currentUrl"
