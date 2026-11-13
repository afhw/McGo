$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $root 'scripts/release_metadata.ps1')

function Assert-Throws {
  param([scriptblock]$Action, [string]$Message)
  $threw = $false
  try { & $Action } catch { $threw = $true }
  if (-not $threw) { throw "Expected rejection: $Message" }
}

# A guard ensures parameter validation can never accidentally start a build.
$script:DotnetInvoked = $false
function dotnet { $script:DotnetInvoked = $true; throw 'Parameter checks must never invoke dotnet.' }
foreach ($url in @('/download/Setup.exe', 'https://example.com/Setup.exe', 'http://localhost/Setup.exe')) {
  Assert-ReleaseUrl -Url $url
}
foreach ($url in @('file:///C:/Setup.exe', '//example.com/Setup.exe', 'javascript:alert(1)', 'relative.exe', '/\example.com/a', 'https://user:secret@example.com/a')) {
  Assert-Throws { Assert-ReleaseUrl -Url $url } $url
}
$release = Join-Path $root 'scripts/build_release.ps1'
$installer = Join-Path $root 'scripts/build_csharp_installer.ps1'
& $release -ValidateOnly
& $release -Installer -ValidateOnly
& $release -DownloadUrl 'https://example.com/McGo.WinUI.exe' -ValidateOnly
& $installer -SetupName 'Example.exe' -DownloadUrl '/download/Example.exe' -ValidateOnly
Assert-Throws { & $release -ReleaseVersion '1.2.3' -ValidateOnly } 'incomplete executable update arguments'
Assert-Throws { & $release -Installer -DownloadUrl '/download/McGo.WinUI.exe' -ValidateOnly } 'installer URL names the client executable'
Assert-Throws { & $release -DownloadUrl 'file:///C:/a.exe' -ValidateOnly } 'invalid URL before publish'
Assert-Throws { & $installer -SetupName '../Other.exe' -ValidateOnly } 'installer path traversal'
Assert-Throws { & $installer -SetupName 'Other.exe' -DownloadUrl '/download/Different.exe' -ValidateOnly } 'URL filename mismatch'
Assert-Throws { & $installer -OutputDirectory $PSScriptRoot -SetupName 'Other.exe' -ManifestPath (Join-Path $PSScriptRoot 'Other.exe') -ValidateOnly } 'manifest overwrites artifact'
if ($script:DotnetInvoked) { throw 'Parameter validation unexpectedly invoked dotnet.' }

$artifact = Join-Path $root 'webside/public/download/McGo-CSharp-Preview-x64-Setup.exe'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('mcgo-metadata-tests-' + [Guid]::NewGuid().ToString('N'))
try {
  $manifestPath = Join-Path $scratch 'update.json'
  $metadata = Write-UpdateManifest -ArtifactPath $artifact -Url '/download/McGo-CSharp-Preview-x64-Setup.exe' -OutputPath $manifestPath -IsInstaller $true
  $readBack = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  if ($readBack.version -cne (Get-ArtifactVersion $artifact) -or
      $readBack.sha256 -cne (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant() -or
      $readBack.size -ne (Get-Item -LiteralPath $artifact).Length -or $readBack.installer -ne $true) {
    throw 'Generated metadata does not describe the actual artifact.'
  }
  Assert-Throws { Write-UpdateManifest -ArtifactPath $artifact -Url '/download/a.exe' -OutputPath $manifestPath -ExpectedVersion '0.0.0' } 'fabricated version'
  Assert-Throws { Write-UpdateManifest -ArtifactPath $artifact -Url '/download/a.exe' -OutputPath $artifact } 'artifact overwrite'
  $verify = Join-Path $root 'scripts/verify_release.ps1'
  & $verify -ArtifactPath $artifact -ManifestPath $manifestPath
  foreach ($field in @('sha256', 'size', 'version', 'url', 'installer', 'missingVersion', 'booleanHash', 'booleanProduct')) {
    $bad = $metadata.PSObject.Copy()
    switch ($field) {
      'sha256' { $bad.sha256 = '0' * 64 }
      'size' { $bad.size++ }
      'version' { $bad.version = '0.0.0' }
      'url' { $bad.url = '/download/McGo.WinUI.exe' }
      'installer' { $bad.installer = $false }
      'missingVersion' { $bad.version = $null }
      'booleanHash' { $bad.sha256 = $true }
      'booleanProduct' { $bad.product = $true }
    }
    [IO.File]::WriteAllText($manifestPath, ($bad | ConvertTo-Json))
    Assert-Throws { & $verify -ArtifactPath $artifact -ManifestPath $manifestPath } "tampered $field"
  }
}
finally {
  if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
Write-Host 'Release parameter and metadata checks passed without a dotnet build.'
