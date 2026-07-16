param(
  [switch]$SkipRelay
)

$ErrorActionPreference = "Stop"

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

Write-Host "McGo C# release written to $OutputDir"
