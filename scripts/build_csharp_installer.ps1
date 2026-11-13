param(
  [string]$OutputDirectory,
  [string]$SetupName = "McGo-CSharp-Preview-x64-Setup.exe",
  [string]$ManifestPath,
  [string]$DownloadUrl,
  [string]$ReleaseVersion,
  [string]$ReleaseNotes = 'McGo C# WinUI 3 preview',
  [switch]$SkipClientBuild,
  [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'release_metadata.ps1')
$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($SetupName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.exe$') {
  throw 'SetupName must be an .exe filename without directories.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $ProjectRoot "webside\public\download"
  if (-not $ManifestPath) { $ManifestPath = Join-Path $ProjectRoot 'webside\public\update-csharp.json' }
}
if (-not $ManifestPath) { $ManifestPath = Join-Path $OutputDirectory 'update-csharp.json' }
if (-not $DownloadUrl) { $DownloadUrl = '/download/' + $SetupName }
Assert-ReleaseUrl -Url $DownloadUrl
if ([Uri]::UnescapeDataString(($DownloadUrl -split '\?')[0].Split('/')[-1]) -cne $SetupName) {
  throw 'DownloadUrl filename must match SetupName.'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$ArtifactsDirectory = Join-Path $ProjectRoot "artifacts\csharp-installer"
$PayloadPath = Join-Path $ArtifactsDirectory "payload.zip"
$BootstrapOutput = Join-Path $ArtifactsDirectory "bootstrap"
$AppOutput = Join-Path $ProjectRoot "dist\app"
$SetupPath = Join-Path $OutputDirectory $SetupName
$TemporarySetupPath = $SetupPath + ".tmp"
$FooterMagic = [System.Text.Encoding]::ASCII.GetBytes("MCGOSETUPv1!")
if ([IO.Path]::GetFullPath($ManifestPath) -in @($SetupPath, "$SetupPath.sha256", $TemporarySetupPath)) {
  throw 'ManifestPath must not overwrite the installer or checksum.'
}
if ($ValidateOnly) { return }

if (-not $SkipClientBuild) {
  & (Join-Path $PSScriptRoot "build_release.ps1") -SkipRelay
}
$clientVersion = Assert-ArtifactVersion -Path (Join-Path $AppOutput 'McGo.WinUI.exe') -ExpectedVersion $ReleaseVersion
Assert-ArtifactVersion -Path (Join-Path $AppOutput 'McGo.WinUI.dll') -ExpectedVersion $clientVersion | Out-Null
Assert-ArtifactVersion -Path (Join-Path $AppOutput 'McGo.Core.dll') -ExpectedVersion $clientVersion | Out-Null

$culturePattern = '^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$'
$cultures = @(Get-ChildItem -LiteralPath $AppOutput -Directory | Where-Object Name -Match $culturePattern)
if ($cultures.Count -ne 1 -or $cultures[0].Name -ne "zh-CN") {
  throw "Client output must contain only the zh-CN language directory: $($cultures.Name -join ', ')"
}

if (-not (Get-ChildItem -LiteralPath (Join-Path $AppOutput "zh-CN") -File -ErrorAction SilentlyContinue)) {
  throw "The zh-CN language directory does not contain published MUI resources."
}

if (Get-ChildItem -LiteralPath $AppOutput -Recurse -File | Where-Object Name -Match '(?i)McGo\.Relay') {
  throw "Relay files must not be included in the client installer."
}

Remove-Item -LiteralPath $ArtifactsDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $ArtifactsDirectory, $BootstrapOutput, $OutputDirectory | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
  $AppOutput,
  $PayloadPath,
  [System.IO.Compression.CompressionLevel]::Optimal,
  $false)

dotnet publish (Join-Path $ProjectRoot "src\McGo.SetupBootstrap\McGo.SetupBootstrap.csproj") `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  -o $BootstrapOutput
if ($LASTEXITCODE -ne 0) {
  throw "Setup bootstrap publish failed with exit code $LASTEXITCODE"
}

$BootstrapPath = Join-Path $BootstrapOutput "McGo.SetupBootstrap.exe"
if (-not (Test-Path -LiteralPath $BootstrapPath)) {
  throw "Setup bootstrap was not produced: $BootstrapPath"
}
Assert-ArtifactVersion -Path $BootstrapPath -ExpectedVersion $clientVersion | Out-Null

Remove-Item -LiteralPath $TemporarySetupPath -Force -ErrorAction SilentlyContinue
$output = [System.IO.File]::Create($TemporarySetupPath)
try {
  $bootstrap = [System.IO.File]::OpenRead($BootstrapPath)
  try {
    $bootstrap.CopyTo($output)
  }
  finally {
    $bootstrap.Dispose()
  }

  $payload = [System.IO.File]::OpenRead($PayloadPath)
  try {
    $payload.CopyTo($output)
    $payloadLength = $payload.Length
  }
  finally {
    $payload.Dispose()
  }

  $lengthBytes = [System.BitConverter]::GetBytes([long]$payloadLength)
  $output.Write($lengthBytes, 0, $lengthBytes.Length)
  $output.Write($FooterMagic, 0, $FooterMagic.Length)
}
finally {
  $output.Dispose()
}

Move-Item -LiteralPath $TemporarySetupPath -Destination $SetupPath -Force
$manifest = Write-UpdateManifest -ArtifactPath $SetupPath -Url $DownloadUrl `
  -OutputPath $ManifestPath -ExpectedVersion $clientVersion -IsInstaller $true -Notes $ReleaseNotes
$hash = $manifest.sha256
$checksumPath = $SetupPath + ".sha256"
[System.IO.File]::WriteAllText(
  $checksumPath,
  "$hash  $SetupName`n",
  [System.Text.Encoding]::ASCII)

& (Join-Path $PSScriptRoot 'verify_release.ps1') -ArtifactPath $SetupPath -ManifestPath $ManifestPath -AppDirectory $AppOutput

$setup = Get-Item -LiteralPath $SetupPath
Write-Host "Installer: $($setup.FullName)"
Write-Host "Size: $($setup.Length) bytes"
Write-Host "SHA-256: $hash"
