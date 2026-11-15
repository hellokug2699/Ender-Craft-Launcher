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
if (Test-Path $FeedRoot) { Remove-Item $FeedRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $workRoot, $legacyRoot, $FeedRoot | Out-Null
$feedHeaders = "/*`n  Cache-Control: no-store`n"
[System.IO.File]::WriteAllText(
    (Join-Path $FeedRoot "_headers"),
    $feedHeaders,
    [System.Text.UTF8Encoding]::new($false))

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

function Get-PublishedFeedFile([string]$Url, [string]$Destination) {
    $statusCode = 0
    Invoke-WebRequest -Uri $Url -OutFile $Destination -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0 -SkipHttpErrorCheck -StatusCodeVariable statusCode
    if ($statusCode -eq 404) {
        Remove-Item $Destination -Force -ErrorAction SilentlyContinue
        return $false
    }
    if ($statusCode -ne 200) {
        throw "Production Feed request failed with HTTP ${statusCode}: $Url"
    }
    return $true
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

    for ($attempt = 1; $attempt -le 6; $attempt++) {
        $remote = & git -C $storeRoot ls-remote --heads origin packages
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect packages branch." }

        $parent = ""
        if ($remote) {
            Invoke-Git @("fetch", "--depth=1", "origin", "packages")
            $parent = (& git -C $storeRoot rev-parse FETCH_HEAD).Trim()
            Invoke-Git @("read-tree", $parent)
            & git -C $storeRoot update-ref refs/heads/packages $parent
            if ($LASTEXITCODE -ne 0) { throw "Unable to set local package branch head." }
            $unexpectedPaths = @(& git -C $storeRoot ls-tree -r --name-only $parent | Where-Object { $_ -notlike "packages/*" })
            if ($LASTEXITCODE -ne 0 -or $unexpectedPaths.Count -gt 0) {
                throw "The packages branch must contain only immutable files under packages/."
            }

            $existingEntry = & git -C $storeRoot ls-tree --name-only $parent -- $relative
            if ($LASTEXITCODE -ne 0) { throw "Unable to inspect immutable Package path." }
            if ($existingEntry) {
                $objectPath = $parent + ":" + $relative
                $existingPath = Join-Path $workRoot "existing-$([Guid]::NewGuid().ToString('N')).zip"
                & git -C $storeRoot show $objectPath > $existingPath
                if ($LASTEXITCODE -ne 0) { throw "Unable to read existing immutable Package." }
                $existingHash = (Get-FileHash $existingPath -Algorithm SHA256).Hash.ToLowerInvariant()
                Remove-Item $existingPath -Force
                if ($existingHash -ne $digest) { throw "Immutable package path collision: $relative" }
                return "https://raw.githubusercontent.com/$Repository/$parent/$relative"
            }
        }
        else {
            & git -C $storeRoot update-ref -d refs/heads/packages 2>$null
            Invoke-Git @("read-tree", "--empty")
        }

        $blob = (& git -C $storeRoot hash-object -w $SourcePath).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($blob)) {
            throw "Unable to create package Git blob."
        }
        Invoke-Git @("update-index", "--add", "--cacheinfo", "100644", $blob, $relative)
        $tree = (& git -C $storeRoot write-tree).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tree)) {
            throw "Unable to write package tree."
        }

        $message = "packages($PackageChannel): add $PackageVersion $Architecture $digest"
        $commitArgs = @("commit-tree", $tree, "-m", $message)
        if ($parent) { $commitArgs = @("commit-tree", $tree, "-p", $parent, "-m", $message) }
        $commit = (& git -C $storeRoot @commitArgs).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
            throw "Unable to create append-only package commit."
        }
        if ($parent) {
            & git -C $storeRoot update-ref refs/heads/packages $commit $parent
        }
        else {
            & git -C $storeRoot update-ref refs/heads/packages $commit
        }
        if ($LASTEXITCODE -ne 0) { throw "Unable to update local package branch ref." }

        & git -C $storeRoot push origin "refs/heads/packages:refs/heads/packages"
        if ($LASTEXITCODE -eq 0) {
            return "https://raw.githubusercontent.com/$Repository/$commit/$relative"
        }

        Write-Warning "packages branch changed concurrently; retrying append (attempt $attempt/6)."
        if ($attempt -eq 6) { throw "Unable to append package after concurrent updates." }
    }
    throw "Package append did not complete."
}

function Confirm-PackageUrl([string]$Url, [long]$ExpectedSize, [string]$ExpectedHash) {
    Renew-FeedLock
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


function Write-BranchFile([string]$Branch, [string]$Path, [string]$Content, [string]$Message, [string]$ExpectedOwner = "") {
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        $remote = & git -C $storeRoot ls-remote --heads origin $Branch
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect $Branch branch." }
        $parent = ""
        if ($remote) {
            Invoke-Git @("fetch", "--depth=1", "origin", $Branch)
            $parent = (& git -C $storeRoot rev-parse FETCH_HEAD).Trim()
            if ($Branch -eq "feed-lock") {
                $currentLockText = (& git -C $storeRoot show ($parent + ":lock.json")) -join [Environment]::NewLine
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($currentLockText)) {
                    $currentLock = $currentLockText | ConvertFrom-Json
                    if ($ExpectedOwner) {
                        if ($currentLock.owner -ne $ExpectedOwner) { throw "Feed lock ownership changed." }
                    }
                    elseif ($currentLock.owner -and [DateTimeOffset]::Parse([string]$currentLock.expiresAt) -gt [DateTimeOffset]::UtcNow) {
                        throw "Feed lock is already held by another run."
                    }
                }
            }
            Invoke-Git @("read-tree", $parent)
            & git -C $storeRoot update-ref "refs/heads/$Branch" $parent
        }
        else {
            & git -C $storeRoot update-ref -d "refs/heads/$Branch" 2>$null
            Invoke-Git @("read-tree", "--empty")
        }

        $temp = Join-Path $workRoot "branch-file-$([Guid]::NewGuid().ToString('N')).json"
        [System.IO.File]::WriteAllText($temp, $Content, [System.Text.UTF8Encoding]::new($false))
        $blob = (& git -C $storeRoot hash-object -w $temp).Trim()
        Remove-Item $temp -Force
        if ($LASTEXITCODE -ne 0) { throw "Unable to create branch metadata blob." }
        Invoke-Git @("update-index", "--add", "--cacheinfo", "100644", $blob, $Path)
        $tree = (& git -C $storeRoot write-tree).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Unable to create branch metadata tree." }
        $commitArgs = @("commit-tree", $tree, "-m", $Message)
        if ($parent) { $commitArgs = @("commit-tree", $tree, "-p", $parent, "-m", $Message) }
        $commit = (& git -C $storeRoot @commitArgs).Trim()
        if ($LASTEXITCODE -ne 0) { throw "Unable to create branch metadata commit." }
        if ($parent) {
            & git -C $storeRoot update-ref "refs/heads/$Branch" $commit $parent
        }
        else {
            & git -C $storeRoot update-ref "refs/heads/$Branch" $commit
        }
        if ($LASTEXITCODE -ne 0) { throw "Unable to update local $Branch ref." }
        $refspec = "refs/heads/" + $Branch + ":refs/heads/" + $Branch
        & git -C $storeRoot push origin $refspec
        if ($LASTEXITCODE -eq 0) { return $commit }
        if ($attempt -eq 8) { throw "Unable to update $Branch after concurrent writes." }
    }
    throw "Unable to update $Branch."
}

function Acquire-FeedLock {
    $script:feedLockOwner = "$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
    for ($attempt = 1; $attempt -le 180; $attempt++) {
        $remote = & git -C $storeRoot ls-remote --heads origin feed-lock
        if ($LASTEXITCODE -ne 0) { throw "Unable to inspect feed-lock branch." }
        $parent = ""
        $locked = $false
        if ($remote) {
            Invoke-Git @("fetch", "--depth=1", "origin", "feed-lock")
            $parent = (& git -C $storeRoot rev-parse FETCH_HEAD).Trim()
            $objectPath = $parent + ":lock.json"
            $lockText = (& git -C $storeRoot show $objectPath) -join [Environment]::NewLine
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($lockText)) {
                $state = $lockText | ConvertFrom-Json
                if ($state.owner -and [DateTimeOffset]::Parse([string]$state.expiresAt) -gt [DateTimeOffset]::UtcNow) {
                    $locked = $true
                }
            }
        }
        if ($locked) {
            Write-Host "Another channel publication owns the Pages lock; waiting (attempt $attempt/180)."
            Start-Sleep -Seconds 15
            continue
        }

        $state = [ordered]@{
            owner = $script:feedLockOwner
            expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(45).ToString("o")
        }
        $json = $state | ConvertTo-Json -Compress
        try {
            $null = Write-BranchFile "feed-lock" "lock.json" $json "ci(feed): acquire Production deployment lock"
            return
        }
        catch {
            if ($attempt -eq 180) { throw }
            Start-Sleep -Seconds 3
        }
    }
    throw "Timed out waiting for the Cloudflare Pages Production lock."
}

function Renew-FeedLock {
    if ([string]::IsNullOrWhiteSpace($script:feedLockOwner)) {
        throw "Cannot renew an unowned feed lock."
    }
    $renewed = [ordered]@{
        owner = $script:feedLockOwner
        expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(45).ToString("o")
    }
    $null = Write-BranchFile "feed-lock" "lock.json" ($renewed | ConvertTo-Json -Compress) "ci(feed): renew Production deployment lease" $script:feedLockOwner
}

function Assert-ChannelFreshness {
    if ($env:ECL_REF_TYPE -eq "tag") { return }
    $head = & gh api "repos/$Repository/branches/$Channel" --jq ".commit.sha"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        throw "Unable to verify current branch head for $Channel."
    }
    if ($head.Trim() -ne $env:GITHUB_SHA) {
        throw "Refusing stale publication for $env:GITHUB_SHA; current $Channel head is $head."
    }
}

function Release-FeedLock {
    if ([string]::IsNullOrWhiteSpace($script:feedLockOwner)) { return }
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        $remote = & git -C $storeRoot ls-remote --heads origin feed-lock
        if ($LASTEXITCODE -ne 0 -or -not $remote) { return }
        Invoke-Git @("fetch", "--depth=1", "origin", "feed-lock")
        $parent = (& git -C $storeRoot rev-parse FETCH_HEAD).Trim()
        $objectPath = $parent + ":lock.json"
        $lockText = (& git -C $storeRoot show $objectPath) -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0) { return }
        $state = $lockText | ConvertFrom-Json
        if ($state.owner -ne $script:feedLockOwner) { return }

        $released = [ordered]@{
            owner = ""
            expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(-1).ToString("o")
        }
        try {
            $null = Write-BranchFile "feed-lock" "lock.json" ($released | ConvertTo-Json -Compress) "ci(feed): release Production deployment lock" $script:feedLockOwner
            $script:feedLockOwner = ""
            return
        }
        catch {
            if ($attempt -eq 8) { Write-Warning "Could not release feed lock cleanly: $_" }
        }
    }
}

# Clone repository metadata only. Package commits are assembled with Git's
# index/tree plumbing, avoiding checkout/download of the entire immutable package history.
$cloneArgs = @("-c", "http.extraheader=AUTHORIZATION: basic $auth", "clone", "--depth=1", "--filter=blob:none", "--no-checkout", "https://github.com/$Repository.git", $storeRoot)
& git @cloneArgs
if ($LASTEXITCODE -ne 0) { throw "Unable to clone package repository metadata." }
& git -C $storeRoot config http.https://github.com/.extraheader "AUTHORIZATION: basic $auth"
& git -C $storeRoot config user.name "github-actions[bot]"
& git -C $storeRoot config user.email "41898282+github-actions[bot]@users.noreply.github.com"

Acquire-FeedLock
try {
Assert-ChannelFreshness
# Preserve all existing channels. On the first deployment, migrate each channel's
# current signed Release package into the immutable package store before Pages is
# switched. Later deployments reuse the already verified Production Feed.
foreach ($candidate in @("main", "dev", "nightly")) {
    if ($candidate -eq $Channel) { continue }
    $channelDirectory = Join-Path $FeedRoot $candidate
    $existingManifest = Join-Path $channelDirectory "update-manifest.json"
    $existingSignature = Join-Path $channelDirectory "update-manifest.sig"
    New-Item -ItemType Directory -Force -Path $channelDirectory | Out-Null
    $manifestExists = Get-PublishedFeedFile "https://updates.endercraftlauncher.top/$candidate/update-manifest.json" $existingManifest
    $signatureExists = Get-PublishedFeedFile "https://updates.endercraftlauncher.top/$candidate/update-manifest.sig" $existingSignature
    if ($manifestExists -xor $signatureExists) {
        throw "Production Feed for $candidate is incomplete; refusing to replace a partial or tampered deployment."
    }
    $isLive = $manifestExists -and $signatureExists
    if ($isLive) {
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
    }
    else {
        Write-Host "No Production Feed exists for $candidate; migrating the current signed GitHub Release."
    }

    if (-not $isLive) {
        $legacyDirectory = Join-Path $legacyRoot $candidate
        $legacyTags = switch ($candidate) {
            "main" { @("main", "stable") }
            "dev" { @("dev", "beta") }
            default { @("nightly") }
        }
        $legacyDownloaded = $false
        $selectedLegacyTag = $null
        foreach ($legacyTag in $legacyTags) {
            $attemptDirectory = Join-Path $legacyDirectory $legacyTag
            New-Item -ItemType Directory -Force -Path $attemptDirectory | Out-Null
            & gh release download $legacyTag --pattern "update-manifest.json" --pattern "update-manifest.sig" --dir $attemptDirectory
            if ($LASTEXITCODE -eq 0) {
                $legacyDirectory = $attemptDirectory
                $selectedLegacyTag = $legacyTag
                $legacyDownloaded = $true
                break
            }
        }
        if (-not $legacyDownloaded) { throw "No existing signed Release feed is available to seed channel $candidate." }
        $legacyManifestPath = Join-Path $legacyDirectory "update-manifest.json"
        $legacySignaturePath = Join-Path $legacyDirectory "update-manifest.sig"
        Test-ManifestSignature $legacyManifestPath $legacySignaturePath
        $legacyManifest = Get-Content $legacyManifestPath -Raw | ConvertFrom-Json
        $legacyChannel = switch ([string]$legacyManifest.channel) {
            "stable" { "main" }
            "beta" { "dev" }
            default { [string]$legacyManifest.channel }
        }
        if ($legacyChannel -ne $candidate) { throw "Legacy Release channel mismatch for $candidate." }
        $legacyManifest.channel = $candidate
        $legacyPackageName = [string]$legacyManifest.package.file
        if ([System.IO.Path]::GetFileName($legacyPackageName) -ne $legacyPackageName) {
            throw "Legacy Manifest package filename is not a leaf name."
        }
        & gh release download $selectedLegacyTag --pattern $legacyPackageName --dir $legacyDirectory
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

Assert-ChannelFreshness

# Wrangler Direct Upload replaces the complete static site in one Production
# deployment. No Pages Functions, Workers, or Package binaries are uploaded.
Renew-FeedLock
npx --yes wrangler@4 pages deploy $FeedRoot --project-name=ender-craft-launcher-feed --branch=main
if ($LASTEXITCODE -ne 0) { throw "Cloudflare Pages Production deployment failed." }

foreach ($candidate in @("main", "dev", "nightly")) {
    foreach ($extension in @("json", "sig")) {
        $file = if ($extension -eq "json") { "update-manifest.json" } else { "update-manifest.sig" }
        $url = "https://updates.endercraftlauncher.top/$candidate/$file"
        $response = Invoke-WebRequest -Uri $url -Headers @{ "User-Agent" = "EnderCraftLauncher/0.9.1" } -MaximumRedirection 0
        if ($response.StatusCode -ne 200) { throw "Feed smoke test failed: $url" }
        if ([string]$response.Headers["Cache-Control"] -notmatch "no-store") {
            throw "Feed metadata is cacheable; expected Cache-Control: no-store on $url."
        }
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

Assert-ChannelFreshness

Write-Host "All three signed channel feeds are deployed. Current package URL: $currentUrl"
}
finally {
    Release-FeedLock
}
