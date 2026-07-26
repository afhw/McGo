param(
  [switch]$SkipRelay,
  # 发布版本号（如 "2.1.0"）。与 -DownloadUrl 同时提供时生成 dist/update.json 更新清单。
  [string]$ReleaseVersion,
  # 更新可执行文件的下载地址（HTTP/HTTPS 绝对地址）。
  [string]$DownloadUrl,
  # 下载地址指向安装器而非可直接替换的 exe 时设置。
  [switch]$Installer,
  # 更新说明/更新日志文本。
  [string]$ReleaseNotes = ""
)

$ErrorActionPreference = "Stop"

function Write-UpdateManifest {
  param(
    [Parameter(Mandatory = $true)][string]$ArtifactPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [bool]$IsInstaller = $false,
    [string]$Notes = ""
  )

  if (-not (Test-Path -LiteralPath $ArtifactPath)) {
    throw "找不到用于生成更新清单的发布文件：$ArtifactPath"
  }

  $uri = $null
  if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or ($uri.Scheme -ne "http" -and $uri.Scheme -ne "https")) {
    throw "更新清单 url 必须是 HTTP 或 HTTPS 绝对地址：$Url"
  }

  $sha256 = (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
  $size = (Get-Item -LiteralPath $ArtifactPath).Length
  # 字段顺序/名称必须与 McGo.Core.Updates.UpdaterService.BuildUpdateManifest 保持一致。
  $manifest = [ordered]@{
    version   = $Version.Trim()
    url       = $Url.Trim()
    sha256    = $sha256
    size      = $size
    installer = [bool]$IsInstaller
    notes     = $Notes.Trim()
  }

  $json = $manifest | ConvertTo-Json -Depth 4
  [System.IO.File]::WriteAllText($OutputPath, $json, (New-Object System.Text.UTF8Encoding($false)))
  Write-Host "更新清单已写入 $OutputPath (version=$Version, sha256=$sha256, size=$size, installer=$IsInstaller)"
}

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

if ($ReleaseVersion -and $DownloadUrl) {
  $manifestArtifact = Join-Path $AppOutputDir "McGo.WinUI.exe"
  $manifestPath = Join-Path $OutputDir "update.json"
  Write-UpdateManifest `
    -ArtifactPath $manifestArtifact `
    -Version $ReleaseVersion `
    -Url $DownloadUrl `
    -OutputPath $manifestPath `
    -IsInstaller:$Installer.IsPresent `
    -Notes $ReleaseNotes
}
elseif ($ReleaseVersion -or $DownloadUrl) {
  Write-Warning "同时提供 -ReleaseVersion 与 -DownloadUrl 才会生成更新清单，已跳过 update.json 生成。"
}

Write-Host "McGo C# release written to $OutputDir"
