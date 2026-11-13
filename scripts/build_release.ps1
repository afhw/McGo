param(
  [switch]$SkipRelay,
  # 可选版本断言；实际版本读取构建产物，不覆盖产物版本。
  [string]$ReleaseVersion,
  # HTTP/HTTPS 地址或以 / 开头的网站路径。
  [string]$DownloadUrl,
  # 下载地址指向安装器而非可直接替换的 exe 时设置。
  [switch]$Installer,
  # 更新说明/更新日志文本。
  [string]$ReleaseNotes = "",
  [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'release_metadata.ps1')

if ($ReleaseVersion -and -not $DownloadUrl -and -not $Installer) {
  throw 'ReleaseVersion requires DownloadUrl when building an executable update.'
}
if ($DownloadUrl) { Assert-ReleaseUrl -Url $DownloadUrl }
if ($Installer) {
  & (Join-Path $PSScriptRoot 'build_csharp_installer.ps1') -DownloadUrl $DownloadUrl -ValidateOnly
}
if ($ValidateOnly) { return }

$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$OutputDir = Join-Path $ProjectRoot "dist"
$AppOutputDir = Join-Path $OutputDir "app"
$RelayOutputDir = Join-Path $OutputDir "relay"
$ClientLanguages = @("zh-CN")

function Remove-UnusedClientLanguages {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [Parameter(Mandatory = $true)]
    [string[]]$Languages
  )

  $cultureDirectoryPattern = '^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$'
  Get-ChildItem -LiteralPath $Path -Directory |
    Where-Object {
      $_.Name -match $cultureDirectoryPattern -and
      $_.Name -notin $Languages
    } |
    Remove-Item -Recurse -Force
}

Remove-Item -LiteralPath $AppOutputDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $RelayOutputDir -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $ProjectRoot "src\McGo.WinUI\McGo.WinUI.csproj") `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  -p:WindowsPackageType=None `
  -p:WindowsAppSDKSelfContained=true `
  -p:RestoreIgnoreFailedSources=true `
  --self-contained true `
  -o $AppOutputDir

if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish for McGo.WinUI failed with exit code $LASTEXITCODE"
}

Remove-UnusedClientLanguages -Path $AppOutputDir -Languages $ClientLanguages
@("accounts.json", "launcher_config.ini", ".minecraft", "logs") | ForEach-Object {
  Remove-Item -LiteralPath (Join-Path $AppOutputDir $_) -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $AppOutputDir -Recurse -File -Filter "*.pdb" |
  Remove-Item -Force

if (-not $SkipRelay) {
  dotnet publish (Join-Path $ProjectRoot "src\McGo.Relay\McGo.Relay.csproj") `
    -c Release `
    -p:RestoreIgnoreFailedSources=true `
    -o $RelayOutputDir

  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for McGo.Relay failed with exit code $LASTEXITCODE"
  }
}

if ($Installer) {
  $installerParameters = @{
    SkipClientBuild = $true
    OutputDirectory = $OutputDir
    ManifestPath = (Join-Path $OutputDir 'update.json')
    ReleaseVersion = $ReleaseVersion
    ReleaseNotes = $ReleaseNotes
  }
  if ($DownloadUrl) { $installerParameters.DownloadUrl = $DownloadUrl }
  & (Join-Path $PSScriptRoot 'build_csharp_installer.ps1') @installerParameters
}
elseif ($DownloadUrl) {
  $manifestArtifact = Join-Path $AppOutputDir "McGo.WinUI.exe"
  $manifestPath = Join-Path $OutputDir "update.json"
  Write-UpdateManifest `
    -ArtifactPath $manifestArtifact `
    -ExpectedVersion $ReleaseVersion `
    -Url $DownloadUrl `
    -OutputPath $manifestPath `
    -Notes $ReleaseNotes | Out-Null
}

Write-Host "McGo C# release written to $OutputDir"
