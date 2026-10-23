#!/usr/bin/env bash
# Rebuild SQLite 3.53.3 (the version in SQLitePCLRaw 2.1.13) for glibc 2.28.
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "$script_dir/../.." && pwd)"
task_dir="$(mktemp -d -t weaponpaints-sqlite.XXXXXX)"
trap 'rm -rf "$task_dir"' EXIT

curl -fsSL --retry 2 https://ziglang.org/download/0.14.1/zig-x86_64-linux-0.14.1.tar.xz -o "$task_dir/zig.tar.xz"
curl -fsSL --retry 2 https://www.sqlite.org/2026/sqlite-amalgamation-3530300.zip -o "$task_dir/sqlite.zip"
printf '%s  %s\n' \
  24aeeec8af16c381934a6cd7d95c807a8cb2cf7df9fa40d359aa884195c4716c "$task_dir/zig.tar.xz" \
  646421e12aac110282ef8cc68f1a62d4bb15fc7b8f09da0b53e29ee690500431 "$task_dir/sqlite.zip" | sha256sum --check -
tar -xf "$task_dir/zig.tar.xz" -C "$task_dir"
unzip -q "$task_dir/sqlite.zip" -d "$task_dir"
mkdir -p "$script_dir/linux-x64"
zig_cc="$task_dir/zig-x86_64-linux-0.14.1/zig"
"$zig_cc" cc -target x86_64-linux-gnu.2.28 -O2 -fPIC -shared -s \
  -Wl,-soname,libe_sqlite3.so \
  -DSQLITE_THREADSAFE=1 -DSQLITE_USE_URI=1 -DSQLITE_ENABLE_COLUMN_METADATA \
  -DSQLITE_ENABLE_FTS3 -DSQLITE_ENABLE_FTS4 -DSQLITE_ENABLE_FTS5 -DSQLITE_ENABLE_RTREE \
  -DSQLITE_ENABLE_DBSTAT_VTAB -DSQLITE_ENABLE_MATH_FUNCTIONS -DSQLITE_ENABLE_STAT4 \
  -DSQLITE_ENABLE_SNAPSHOT -DSQLITE_ENABLE_PREUPDATE_HOOK -DSQLITE_ENABLE_SESSION \
  -DSQLITE_ENABLE_NORMALIZE "$task_dir/sqlite-amalgamation-3530300/sqlite3.c" \
  -o "$task_dir/libe_sqlite3.so" -lpthread -ldl -lm
bash "$repo_dir/tests/verify-sqlite-abi.sh" "$task_dir/libe_sqlite3.so"
"$zig_cc" cc -target x86_64-linux-gnu.2.28 "$repo_dir/tests/sqlite-smoke.c" -o "$task_dir/sqlite-smoke" -ldl
"$task_dir/sqlite-smoke" "$task_dir/libe_sqlite3.so"
cp "$task_dir/libe_sqlite3.so" "$script_dir/linux-x64/libe_sqlite3.so"
