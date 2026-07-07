$ErrorActionPreference = "Stop"

$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$OutputDir = Join-Path $ProjectRoot "dist"
$AppOutputDir = Join-Path $OutputDir "app"
$RelayOutputDir = Join-Path $OutputDir "relay"

Remove-Item -LiteralPath $AppOutputDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $RelayOutputDir -Recurse -Force -ErrorAction SilentlyContinue

dotnet build (Join-Path $ProjectRoot "src\McGo.WinUI\McGo.WinUI.csproj") `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  -p:WindowsPackageType=None `
  -p:RestoreIgnoreFailedSources=true

if ($LASTEXITCODE -ne 0) {
  throw "dotnet build for McGo.WinUI failed with exit code $LASTEXITCODE"
}

$WinUiBuildDir = Join-Path $ProjectRoot "src\McGo.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64"
if (-not (Test-Path $WinUiBuildDir)) {
  throw "WinUI build output was not found: $WinUiBuildDir"
}

New-Item -ItemType Directory -Force -Path $AppOutputDir | Out-Null
Copy-Item -Path (Join-Path $WinUiBuildDir "*") -Destination $AppOutputDir -Recurse -Force

dotnet publish (Join-Path $ProjectRoot "src\McGo.Relay\McGo.Relay.csproj") `
  -c Release `
  -p:RestoreIgnoreFailedSources=true `
  -o $RelayOutputDir

if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish for McGo.Relay failed with exit code $LASTEXITCODE"
}

Write-Host "McGo C# release written to $OutputDir"
