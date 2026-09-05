[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0.0',

    [string]$Publisher = 'CN=DevBrowser Local Test'
)

$ErrorActionPreference = 'Stop'

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window. Local MSIX testing installs a temporary DevBrowser test root certificate for this machine.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'src\DeveloperBrowser.Package\Package.appxmanifest'
$packageProjectPath = Join-Path $repositoryRoot 'src\DeveloperBrowser.Package\DeveloperBrowser.Package.wapproj'
$outputDirectory = Join-Path $repositoryRoot 'artifacts\local-installer'
$vswherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $vswherePath)) {
    throw 'Visual Studio with MSBuild is required to create the local MSIX installer.'
}

$uapSdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\DesignTime\CommonConfiguration\Neutral\UAP"
if (-not (Test-Path $uapSdkRoot)) {
    throw 'Install the Visual Studio "Universal Windows Platform development" workload, then run this script again.'
}

$uapSdkVersion = Get-ChildItem $uapSdkRoot -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty Name
if ([string]::IsNullOrWhiteSpace($uapSdkVersion)) {
    throw 'No Windows SDK suitable for MSIX packaging was found. Install the Universal Windows Platform development workload.'
}

$signToolPath = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\$uapSdkVersion\x64\signtool.exe"
if (-not (Test-Path $signToolPath)) {
    throw 'The Windows SDK signing tool was not found. Repair or reinstall the Universal Windows Platform development workload.'
}

$msbuildPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
    throw 'Visual Studio MSBuild was not found.'
}

$localRootSubject = 'CN=DevBrowser Local Test Root'
$localRootCertificate = Get-ChildItem 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $localRootSubject -and $_.HasPrivateKey } |
    Select-Object -First 1

if ($null -eq $localRootCertificate) {
    $localRootCertificate = New-SelfSignedCertificate -Type Custom -Subject $localRootSubject -KeyUsage CertSign, CRLSign, DigitalSignature -KeyExportPolicy Exportable -KeySpec Signature -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.19={critical}{text}ca=true') -NotAfter (Get-Date).AddYears(4)
    Write-Host "Created a local development root certificate: $($localRootCertificate.Thumbprint)"
}

$certificate = Get-ChildItem 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $Publisher -and $_.Issuer -eq $localRootSubject -and $_.HasPrivateKey } |
    Select-Object -First 1

if ($null -eq $certificate) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $Publisher -Signer $localRootCertificate -KeyUsage DigitalSignature -KeyExportPolicy Exportable -KeySpec Signature -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.19={critical}{text}ca=false', '2.5.29.37={text}1.3.6.1.5.5.7.3.3') -NotAfter (Get-Date).AddYears(2)
    Write-Host "Created a local development signing certificate: $($certificate.Thumbprint)"
}

if ($null -eq (Get-ChildItem 'Cert:\LocalMachine\Root' | Where-Object Thumbprint -eq $localRootCertificate.Thumbprint | Select-Object -First 1)) {
    $localRootCertificateFile = Join-Path $env:TEMP "DevBrowser-root-$($localRootCertificate.Thumbprint).cer"
    try {
        Export-Certificate -Cert $localRootCertificate -FilePath $localRootCertificateFile | Out-Null
        Import-Certificate -FilePath $localRootCertificateFile -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $localRootCertificateFile -Force -ErrorAction SilentlyContinue
    }
}

$certificatePassword = [guid]::NewGuid().ToString('N')
$certificatePasswordSecure = ConvertTo-SecureString $certificatePassword -AsPlainText -Force
$certificatePfxFile = Join-Path $env:TEMP "DevBrowser-$($certificate.Thumbprint).pfx"
Export-PfxCertificate -Cert $certificate -FilePath $certificatePfxFile -Password $certificatePasswordSecure | Out-Null
$certificateChainFile = Join-Path $env:TEMP "DevBrowser-root-$($localRootCertificate.Thumbprint).cer"
Export-Certificate -Cert $localRootCertificate -FilePath $certificateChainFile | Out-Null

$originalManifest = [System.IO.File]::ReadAllText($manifestPath)
if (-not $originalManifest.Contains('__MSIX_PUBLISHER__')) {
    throw 'The package manifest must retain the __MSIX_PUBLISHER__ placeholder for local packaging.'
}

[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[System.IO.File]::WriteAllText($manifestPath, $originalManifest.Replace('__MSIX_PUBLISHER__', $Publisher), [System.Text.UTF8Encoding]::new($false))

try {
    & $msbuildPath $packageProjectPath /restore /m /p:Configuration=Release /p:Platform=x64 /p:DevBrowserPackagingTargetPlatformVersion=$uapSdkVersion /p:DevBrowserVersion=$Version /p:AppxPackageSigningEnabled=false /p:AppxPackageDir="$outputDirectory\"
    if ($LASTEXITCODE -ne 0) { throw "MSIX package build failed with exit code $LASTEXITCODE." }

    $package = Get-ChildItem $outputDirectory -Recurse -Filter '*.msix' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $package) { throw 'The MSIX package was not produced.' }

    & $signToolPath sign /fd SHA256 /f $certificatePfxFile /p $certificatePassword /sha1 $certificate.Thumbprint /ac $certificateChainFile $package.FullName
    if ($LASTEXITCODE -ne 0) { throw "MSIX signing failed with exit code $LASTEXITCODE." }
}
finally {
    [System.IO.File]::WriteAllText($manifestPath, $originalManifest, [System.Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $certificatePfxFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $certificateChainFile -Force -ErrorAction SilentlyContinue
}

$installerPath = Join-Path $outputDirectory "DevBrowser-$Version.msix"
Copy-Item -LiteralPath $package.FullName -Destination $installerPath -Force

Write-Host ''
Write-Host "Local DevBrowser installer created: $installerPath" -ForegroundColor Green
Write-Host 'Double-click the .msix file to install it with App Installer.'
