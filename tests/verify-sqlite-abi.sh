#!/usr/bin/env bash
set -euo pipefail
library="${1:?Usage: bash tests/verify-sqlite-abi.sh path/to/libe_sqlite3.so}"
maximum="$(readelf --version-info "$library" | sed -n 's/.*Name: GLIBC_\([0-9.]*\).*/\1/p' | sort -V | tail -n 1)"
if [ -z "$maximum" ] || [ "$(printf '%s\n' "$maximum" '2.28' | sort -V | tail -n 1)" != '2.28' ]; then
  echo "FAIL: $library requires glibc ${maximum:-unknown}; deployment limit is 2.28." >&2
  exit 1
fi
echo "PASS: $library requires glibc $maximum (limit 2.28)."
