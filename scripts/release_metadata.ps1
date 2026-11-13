function Assert-ReleaseUrl {
  param([Parameter(Mandatory = $true)][string]$Url)
  $uri = $null
  if ($Url -match '^/[^/\\]' -and $Url -notmatch '[\\\s#]') { return }
  if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or
      $uri.Scheme -notin @('http', 'https') -or -not $uri.Host -or $uri.UserInfo -or $uri.Fragment) {
    throw 'DownloadUrl must be an HTTP(S) URL or a root-relative website path.'
  }
}

function Get-ArtifactVersion {
  param([Parameter(Mandatory = $true)][string]$Path)
  $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $Path).Path).ProductVersion
  $version = ($version -split '\+', 2)[0]
  if ($version -notmatch '^\d+\.\d+\.\d+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?$') {
    throw "Artifact has no usable product version: $Path"
  }
  return $version
}

function Assert-ArtifactVersion {
  param([string]$Path, [string]$ExpectedVersion)
  $actual = Get-ArtifactVersion -Path $Path
  if ($ExpectedVersion -and $actual -cne $ExpectedVersion.Trim()) {
    throw "Artifact version mismatch: $Path is $actual, expected $ExpectedVersion"
  }
  return $actual
}

function Write-UpdateManifest {
  param(
    [Parameter(Mandatory = $true)][string]$ArtifactPath,
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$ExpectedVersion,
    [bool]$IsInstaller = $false,
    [string]$Notes = ''
  )
  Assert-ReleaseUrl -Url $Url
  $version = Assert-ArtifactVersion -Path $ArtifactPath -ExpectedVersion $ExpectedVersion
  $manifest = [ordered]@{
    version = $version
    url = $Url
    sha256 = (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    size = (Get-Item -LiteralPath $ArtifactPath).Length
    installer = $IsInstaller
    product = 'mcgo-winui'
    notes = $Notes.Trim()
  }
  $output = [IO.Path]::GetFullPath($OutputPath)
  if ($output -eq [IO.Path]::GetFullPath($ArtifactPath)) { throw 'Manifest cannot overwrite the artifact.' }
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
  [IO.File]::WriteAllText($output, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
  return [pscustomobject]$manifest
}

function Open-InstallerPayload {
  param([Parameter(Mandatory = $true)][string]$Path)
  Add-Type -AssemblyName System.IO.Compression
  $file = [IO.File]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
  $payload = [IO.MemoryStream]::new()
  try {
    $reader = [IO.BinaryReader]::new($file)
    if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Installer is not a PE executable.' }
    $file.Position = 0x3c
    $file.Position = $reader.ReadInt32()
    if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
      throw 'Installer must be a Windows x64 PE executable.'
    }
    # Match the bootstrap footer search, including packages with a trailing signature.
    $tailLength = [int][Math]::Min(16MB, $file.Length)
    $file.Position = $file.Length - $tailLength
    $tail = $reader.ReadBytes($tailLength)
    $footer = [Text.Encoding]::ASCII.GetString($tail).LastIndexOf('MCGOSETUPv1!', [StringComparison]::Ordinal)
    if ($footer -lt 8) { throw 'Installer payload footer is missing.' }
    $length = [BitConverter]::ToInt64($tail, $footer - 8)
    $offset = $file.Length - $tailLength + $footer - 8 - $length
    if ($length -le 0 -or $offset -le 0) { throw 'Installer payload length is invalid.' }
    $file.Position = $offset
    if ($reader.ReadUInt32() -ne 0x04034b50) { throw 'Installer payload is not a ZIP archive.' }
    $file.Position = $offset
    $buffer = [byte[]]::new(1MB)
    while ($length -gt 0) {
      $read = $file.Read($buffer, 0, [int][Math]::Min($buffer.Length, $length))
      if ($read -eq 0) { throw 'Installer payload is truncated.' }
      $payload.Write($buffer, 0, $read)
      $length -= $read
    }
    $payload.Position = 0
    return [IO.Compression.ZipArchive]::new($payload, [IO.Compression.ZipArchiveMode]::Read)
  }
  catch { $payload.Dispose(); throw }
  finally { $file.Dispose() }
}
