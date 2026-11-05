#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
if [[ "${1:-}" == --help ]]; then exec python3 "$script_dir/ci/verify-legacy-assets.py" "$@"; fi
task_state="${XDG_STATE_HOME:-$HOME/.local/state}/teleg-verifier"
mkdir -p "$task_state"
chmod 700 "$task_state"
unset PYTHONHOME PYTHONPATH
if [[ ! -x "$task_state/python/bin/python" ]]; then
  if ! python3 -m venv "$task_state/python" >"$task_state/setup.log" 2>&1; then
    echo 'Cannot create the isolated verifier environment. Python 3 with venv support is required; the fleet has not been changed.' >&2
    exit 2
  fi
fi
if ! "$task_state/python/bin/python" -c 'from importlib.metadata import version;assert all(version(p)==v for p,v in [("cryptography","49.0.0"),("python-dotenv","1.2.2"),("cffi","2.1.0"),("pycparser","3.0")])' >/dev/null 2>&1; then
  echo 'Preparing the isolated verifier dependencies.'
  if ! "$task_state/python/bin/python" -m pip install --disable-pip-version-check --no-input -r "$script_dir/ci/legacy-assets-requirements.txt" >"$task_state/setup.log" 2>&1; then
    echo 'Verifier dependency setup failed. The protected setup log is in the verifier state directory; the fleet has not been changed.' >&2
    exit 2
  fi
fi
exec "$task_state/python/bin/python" "$script_dir/ci/verify-legacy-assets.py" "$@"
