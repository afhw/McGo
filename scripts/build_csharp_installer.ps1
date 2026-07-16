param(
  [string]$OutputDirectory,
  [string]$SetupName = "McGo-CSharp-Preview-x64-Setup.exe"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $ProjectRoot "webside\public\download"
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$ArtifactsDirectory = Join-Path $ProjectRoot "artifacts\csharp-installer"
$PayloadPath = Join-Path $ArtifactsDirectory "payload.zip"
$BootstrapOutput = Join-Path $ArtifactsDirectory "bootstrap"
$AppOutput = Join-Path $ProjectRoot "dist\app"
$SetupPath = Join-Path $OutputDirectory $SetupName
$TemporarySetupPath = $SetupPath + ".tmp"
$FooterMagic = [System.Text.Encoding]::ASCII.GetBytes("MCGOSETUPv1!")

& (Join-Path $PSScriptRoot "build_release.ps1") -SkipRelay
if ($LASTEXITCODE -ne 0) {
  throw "Client release build failed with exit code $LASTEXITCODE"
}

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
  -r win-x64 `
  -o $BootstrapOutput
if ($LASTEXITCODE -ne 0) {
  throw "Setup bootstrap publish failed with exit code $LASTEXITCODE"
}

$BootstrapPath = Join-Path $BootstrapOutput "McGo.SetupBootstrap.exe"
if (-not (Test-Path -LiteralPath $BootstrapPath)) {
  throw "Setup bootstrap was not produced: $BootstrapPath"
}

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
$hash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = $SetupPath + ".sha256"
[System.IO.File]::WriteAllText(
  $checksumPath,
  "$hash  $SetupName`n",
  [System.Text.Encoding]::ASCII)

$setup = Get-Item -LiteralPath $SetupPath
Write-Host "Installer: $($setup.FullName)"
Write-Host "Size: $($setup.Length) bytes"
Write-Host "SHA-256: $hash"
