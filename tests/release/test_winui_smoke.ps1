param(
  [Parameter(Mandatory = $true)][string]$ExecutablePath,
  [string]$ReportPath = (Join-Path $PSScriptRoot '../../artifacts/ci/ui-smoke.txt'),
  [ValidateRange(10, 600)][int]$TimeoutSeconds = 180,
  [switch]$Resize,
  [switch]$AllowUnavailableDesktop
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$report = [IO.Path]::GetFullPath($ReportPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($report)) | Out-Null
Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue

# Hosted Windows runners may run in session 0. Do not claim a UI pass there.
$interactive = [Environment]::UserInteractive -and [Diagnostics.Process]::GetCurrentProcess().SessionId -ne 0
if (-not $interactive) {
  if (-not $AllowUnavailableDesktop) { throw 'UI smoke requires an interactive Windows desktop.' }
  $message = 'SKIP UI smoke: runner has no interactive Windows desktop; run on an interactive x64 Windows runner.'
  [IO.File]::WriteAllText($report, $message)
  if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $message }
  Write-Warning $message
  return
}

$mode = if ($Resize) { '--smoke-resize' } else { '--smoke-pages' }
$process = Start-Process -FilePath $exe -ArgumentList @($mode, ('--smoke-report="' + $report + '"')) `
  -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden -PassThru
try {
  if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "UI smoke timed out after $TimeoutSeconds seconds." }
  if (-not (Test-Path -LiteralPath $report)) { throw "UI smoke produced no report (exit $($process.ExitCode))." }
  $lines = @(Get-Content -LiteralPath $report)
  $lines | Write-Host
  if ($process.ExitCode -ne 0 -or @($lines | Where-Object { $_ -match '^FAIL' }).Count) {
    throw "UI smoke failed (exit $($process.ExitCode))."
  }
  foreach ($page in @('home', 'launch', 'download', 'resources', 'modpacks', 'online', 'accounts', 'settings', 'logs', 'about')) {
    $pattern = if ($Resize) { "^PASS resize $page step=" } else { "^PASS \d+x\d+ $page$" }
    $expected = if ($Resize) { 9 } else { 3 }
    if (@($lines | Where-Object { $_ -match $pattern }).Count -ne $expected) {
      throw "UI smoke did not complete all $expected layout checks for $page."
    }
  }
  # The app reports actual screen-constrained dimensions; 1920x1080 is not assumed.
}
finally {
  if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
  $process.Dispose()
}
