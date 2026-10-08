#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
# Prefer the check environment SDK; support the documented local installation.
if ! command -v dotnet >/dev/null 2>&1; then
  sdk_dir="${EUCLENS_DOTNET_DIR:-/tmp/euclens-dotnet}"
  if [[ ! -x "$sdk_dir/dotnet" ]]; then
    echo 'Install the .NET 10 SDK or set EUCLENS_DOTNET_DIR to its installation directory.' >&2
    exit 127
  fi
  export DOTNET_ROOT="$sdk_dir"
  export PATH="$sdk_dir:$PATH"
fi
dotnet --info
python3 tools/ux-contract.py --check
dotnet restore EucLens.sln -m:1
dotnet build EucLens.sln -c Release --no-restore -m:1
dotnet test tests/EucLens.Tests/EucLens.Tests.csproj -c Release --no-build -m:1 \
  --logger 'trx;LogFileName=verification.trx' --results-directory TestResults
dotnet publish src/EucLens.App/EucLens.App.csproj -p:PublishProfile=Portable -o artifacts/win-x64 -m:1
python3 - <<'PY'
from pathlib import Path
files = sorted(p.name for p in Path('artifacts/win-x64').iterdir() if p.is_file())
if files != ['EucLens.exe']:
    raise SystemExit(f'Expected only EucLens.exe, found {files}')
print('PASS: portable publish contains exactly EucLens.exe')
PY
