# Edit the defaults below for each release, or override them on the command line.
# The tag is the source of truth for the application and MSIX versions.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Version = '1.0.0',
    [string]$Tag = '',
    [ValidateNotNullOrEmpty()]
    [string]$Remote = 'origin',
    [ValidateNotNullOrEmpty()]
    [string]$Target = 'HEAD'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    $result = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed (exit code $LASTEXITCODE)."
    }
    $result
}

if ([string]::IsNullOrWhiteSpace($Tag)) { $Tag = "v$Version" }
if ($Tag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-beta\.([1-9]\d*))?$') {
    throw 'Use vMajor.Minor.Patch or vMajor.Minor.Patch-beta.Number, for example v1.2.3-beta.1.'
}
$parts = @($Matches[1], $Matches[2], $Matches[3])
$beta = $Matches[4]
foreach ($part in $parts) {
    if ([double]$part -gt 65535) { throw 'MSIX version components cannot exceed 65535.' }
}
if ($beta -and [double]$beta -ge 65535) { throw 'Beta number must be between 1 and 65534; 65535 is reserved for stable releases.' }
if ($PSBoundParameters.ContainsKey('Version') -and $Tag -ne "v$Version") {
    throw 'Version and Tag disagree. Supply either one, or matching values.'
}
$revision = if ($beta) { $beta } else { '65535' }
$packageVersion = ($parts + $revision) -join '.'

Get-Command git -ErrorAction Stop | Out-Null
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $commit = Invoke-Checked git @('rev-parse', '--verify', "$Target^{commit}")
    Write-Host "Release: $Tag | MSIX: $packageVersion | Commit: $commit | Remote: $Remote"
    if ($WhatIfPreference) {
        Write-Host "Would verify GitHub access and a clean working tree, create annotated tag $Tag, and push only that tag to $Remote."
        Write-Host 'The tag push triggers release-msix.yml to build, sign, and publish the release with generated notes.'
        return
    }

    Get-Command gh -ErrorAction Stop | Out-Null
    Invoke-Checked gh @('auth', 'status') | Out-Host
    $remoteUrl = Invoke-Checked git @('remote', 'get-url', '--push', $Remote)
    $repoUrl = Invoke-Checked gh @('repo', 'view', $remoteUrl, '--json', 'url', '--jq', '.url')
    $dirty = Invoke-Checked git @('status', '--porcelain')
    if ($dirty) { throw 'Commit or stash all changes before releasing (including untracked files).' }
    $existingRemoteTag = Invoke-Checked git @('ls-remote', '--tags', $Remote, "refs/tags/$Tag")
    if ($existingRemoteTag) { throw "Remote tag $Tag already exists. Inspect its Actions run; use a new version for a new release." }
    $existingLocalTag = Invoke-Checked git @('tag', '--list', $Tag)
    if ($existingLocalTag) {
        $tagCommit = Invoke-Checked git @('rev-parse', '--verify', "refs/tags/$Tag^{commit}")
        if ($tagCommit -ne $commit) { throw "Local tag $Tag points to another commit. Choose a new tag; existing tags are never moved." }
        Write-Host "Reusing local tag $Tag at $commit (for example, after a failed push)."
    }
    if ($PSCmdlet.ShouldProcess("$repoUrl at $commit", "Create and push release tag $Tag")) {
        if (-not $existingLocalTag) {
            Invoke-Checked git @('tag', '-a', $Tag, $commit, '-m', "Release $Tag") | Out-Host
        }
        Invoke-Checked git @('push', $Remote, "refs/tags/${Tag}:refs/tags/${Tag}") | Out-Host
        Write-Host "Tag pushed. Follow the build: $repoUrl/actions/workflows/release-msix.yml"
        Write-Host "After the workflow succeeds: $repoUrl/releases/tag/$Tag"
    }
}
finally {
    Pop-Location
}
