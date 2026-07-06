$ErrorActionPreference = "Stop"

$ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$VenvPython = Join-Path $ProjectRoot ".venv\Scripts\python.exe"
$Python = if ($env:MCGO_PYTHON) {
  $env:MCGO_PYTHON
} elseif (Test-Path $VenvPython) {
  $VenvPython
} else {
  "python"
}

& $Python -m pip install nuitka ordered-set zstandard
& $Python -m nuitka `
  --standalone `
  --onefile `
  --assume-yes-for-downloads `
  --output-dir=dist `
  --output-filename=mcgo-p2p-server.exe `
  p2p_server.py
