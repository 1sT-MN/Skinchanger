The Linux x64 `libe_sqlite3.so` here is SQLite 3.53.3, built from the official
amalgamation using Zig 0.14.1 with target `x86_64-linux-gnu.2.28`.
SQLite source is public domain: https://www.sqlite.org/copyright.html.

SQLitePCLRaw 2.1.13 also uses SQLite 3.53.3, but its Linux x64 binary requires
glibc 2.34. `build-all.sh` substitutes this compatible build in the PlayerSettings
runtime directory, retaining the managed provider and the SQLite version.
Other platforms continue using the package's native binaries.

Reproduce the build on Linux x64 with:

```sh
bash 3rd_party/sqlite/build-compat.sh
```

Inputs are pinned by URL and SHA-256 in that script. It verifies the maximum
glibc requirement and performs an in-memory SQL write/read check before replacing
the bundled library. `tests/sqlite-smoke.c` can also be compiled for glibc 2.28
and run in a Debian Buster container to verify loading on that actual libc.
