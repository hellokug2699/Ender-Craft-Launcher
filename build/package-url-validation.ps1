function Assert-ImmutablePackageUrl([string]$Url, [string]$Repository, [string]$ExpectedHash) {
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        throw "Package URL is not a valid absolute URI."
    }

    $normalizedHash = $ExpectedHash.Trim().ToLowerInvariant()
    if ($normalizedHash -notmatch "^[0-9a-f]{64}$") {
        throw "Manifest Package SHA-256 is not a 64-character hexadecimal digest."
    }

    # Match the canonical URL shape explicitly. Keep the path visible in the
    # failure message so migration failures can be diagnosed without weakening
    # the GitHub host, repository, channel, architecture, or digest boundary.
    $packagePathPattern = "^/$([regex]::Escape($Repository))/releases/download/(main|dev|nightly)-packages/ECL-[A-Za-z0-9][A-Za-z0-9.-]*-win-(x64|arm64)-$normalizedHash\.zip$"
    $validUri = $uri.Scheme -eq "https" -and
        [string]::Equals($uri.Host, "github.com", [StringComparison]::OrdinalIgnoreCase) -and
        $uri.IsDefaultPort -and
        [string]::IsNullOrEmpty($uri.UserInfo) -and
        [string]::IsNullOrEmpty($uri.Query) -and
        [string]::IsNullOrEmpty($uri.Fragment)
    $validPath = [regex]::IsMatch(
        $uri.AbsolutePath,
        $packagePathPattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not ($validUri -and $validPath)) {
        throw "Package URL is outside the immutable GitHub Release asset namespace. Host='$($uri.Host)'; Path='$($uri.AbsolutePath)'; ExpectedPathPattern='$packagePathPattern'."
    }

    return $uri
}
