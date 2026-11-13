param(
  [Parameter(Mandatory = $true)][string]$ArtifactPath,
  [Parameter(Mandatory = $true)][string]$ManifestPath,
  [string]$AppDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release_metadata.ps1')
$artifact = Get-Item -LiteralPath $ArtifactPath
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
Assert-ReleaseUrl -Url $manifest.url
if ($manifest.installer -isnot [bool] -or -not $manifest.installer -or
    $manifest.product -isnot [string] -or $manifest.product -cne 'mcgo-winui') {
  throw 'Expected a mcgo-winui installer manifest.'
}
if ($manifest.version -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest.version)) {
  throw 'Manifest version must be a nonempty string.'
}
if ($manifest.sha256 -isnot [string] -or $manifest.sha256 -cnotmatch '^[a-f0-9]{64}$') {
  throw 'Manifest SHA-256 must contain 64 lowercase hexadecimal characters.'
}
if ([Uri]::UnescapeDataString(($manifest.url -split '\?')[0].Split('/')[-1]) -cne $artifact.Name) {
  throw 'Manifest URL does not name the installer artifact.'
}
if ($manifest.size -isnot [long] -and $manifest.size -isnot [int]) { throw 'Manifest size must be an integer.' }
if ($manifest.size -ne $artifact.Length) { throw 'Manifest size does not match the final installer.' }
$hash = (Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.sha256 -cne $hash) { throw 'Manifest SHA-256 does not match the final installer.' }
if ((Get-Content -LiteralPath "$ArtifactPath.sha256" -Raw).TrimEnd() -cne "$hash  $($artifact.Name)") {
  throw 'Checksum file does not match the final installer.'
}
$version = Assert-ArtifactVersion -Path $artifact.FullName -ExpectedVersion $manifest.version
if ($version -cne $manifest.version) { throw 'Manifest version must exactly match the artifact version.' }
$archive = Open-InstallerPayload -Path $artifact.FullName
$versionScratch = Join-Path ([IO.Path]::GetTempPath()) ("mcgo-release-check-" + [Guid]::NewGuid().ToString('N'))
try {
  $files = @{}
  foreach ($entry in $archive.Entries) {
    $name = $entry.FullName.Replace('\', '/')
    if ($name.StartsWith('/') -or $name -match '(^|/)\.\.(/|$)|:') { throw "Unsafe payload path: $name" }
    if ($name.EndsWith('/')) { continue }
    if ($files.ContainsKey($name)) { throw "Duplicate payload entry: $name" }
    $files[$name] = $entry
    if ($name -match '(?i)(^|/)(accounts\.json|launcher_config\.ini|version_settings\.json|logs|\.minecraft)(/|$)|McGo\.Relay|\.pdb$') {
      throw "Unwanted client payload file: $name"
    }
    $topDirectory = $name.Split('/')[0]
    if ($topDirectory -match '^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})+$' -and $topDirectory -cne 'zh-CN') {
      throw "Unwanted language directory: $topDirectory"
    }
  }
  foreach ($required in @('McGo.WinUI.exe', 'McGo.WinUI.dll', 'McGo.Core.dll', 'McGo.WinUI.runtimeconfig.json',
      'McGo.WinUI.deps.json', 'McGo.WinUI.pri', 'MainWindow.xbf', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.UI.Xaml.dll')) {
    if (-not $files.ContainsKey($required)) { throw "Missing client payload file: $required" }
  }
  foreach ($prefix in @('zh-CN/', 'Assets/')) {
    if (-not @($files.Keys | Where-Object { $_.StartsWith($prefix) -and $files[$_].Length -gt 0 }).Count) {
      throw "Missing client payload resources: $prefix"
    }
  }
  [IO.Directory]::CreateDirectory($versionScratch) | Out-Null
  foreach ($name in @('McGo.WinUI.exe', 'McGo.WinUI.dll', 'McGo.Core.dll')) {
    $target = Join-Path $versionScratch $name
    $inputStream = $files[$name].Open()
    $outputStream = [IO.File]::Create($target)
    try { $inputStream.CopyTo($outputStream) }
    finally { $inputStream.Dispose(); $outputStream.Dispose() }
    Assert-ArtifactVersion -Path $target -ExpectedVersion $version | Out-Null
  }
  if ($AppDirectory) {
    $appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
    $sourceFiles = @(Get-ChildItem -LiteralPath $appRoot -Recurse -File)
    if ($sourceFiles.Count -ne $files.Count) { throw 'Payload file count differs from published client.' }
    foreach ($source in $sourceFiles) {
      $name = $source.FullName.Substring($appRoot.Length + 1).Replace('\', '/')
      if (-not $files.ContainsKey($name) -or $files[$name].Length -ne $source.Length) {
        throw "Payload file missing or wrong size: $name"
      }
      $stream = $files[$name].Open()
      try { $entryHash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
      finally { $stream.Dispose() }
      if ($entryHash -cne (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash) {
        throw "Payload content differs from published client: $name"
      }
    }
  }
  Write-Host "Verified installer $version, $($artifact.Length) bytes, SHA-256 $hash; $($files.Count) payload files."
}
finally {
  $archive.Dispose()
  if (Test-Path -LiteralPath $versionScratch) {
    # Only the unique temporary directory created above is removed.
    Remove-Item -LiteralPath $versionScratch -Recurse -Force
  }
}
