$ErrorActionPreference = "Stop"

python -m pip install nuitka ordered-set zstandard
python -m nuitka `
  --standalone `
  --onefile `
  --assume-yes-for-downloads `
  --output-dir=dist `
  --output-filename=mcgo-p2p-server.exe `
  p2p_server.py
