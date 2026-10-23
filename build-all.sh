#!/usr/bin/env bash
#
# build-all.sh — build & package the unified Skinchanger stack for a CS2 /
# CounterStrikeSharp 1.0.377 (.NET 10) dedicated server.
#
# Produces a ready-to-copy CounterStrikeSharp layout under ./dist:
#
#   dist/addons/counterstrikesharp/
#     shared/PlayerSettingsApi/PlayerSettingsApi.dll   (capability interface, loaded once)
#     shared/MenuManagerApi/MenuManagerApi.dll         (capability interface, loaded once)
#     plugins/PlayerSettings/      PlayerSettings.dll + AnyBaseLib.dll + DB drivers
#     plugins/MenuManagerCore/     MenuManagerCore.dll
#     plugins/WeaponPaints/        WeaponPaints.dll + Dapper/MySqlConnector/Newtonsoft + lang/ + data/
#     gamedata/weaponpaints.json
#
# The server already ships CounterStrikeSharp.API.dll and the whole
# Microsoft.Extensions.* / Serilog / McMaster host, so every assembly that
# CounterStrikeSharp.API itself drags in is pruned from the plugin folders
# ("exactly once"). The capability interface assemblies (PlayerSettingsApi,
# MenuManagerApi) are placed in shared/ so every plugin resolves the *same*
# ISettingsApi / IMenuApi type across plugin load contexts.
#
# Fails (set -e) on any restore/build error.

set -euo pipefail

CONFIG="${1:-Release}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKIN_DIR="$SCRIPT_DIR"
STACK_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SLN="$SKIN_DIR/SkinchangerStack.sln"
DIST="$SKIN_DIR/dist"
STAGE="$SKIN_DIR/.stage"
CSS_VERSION="1.0.377"

PS_API="$STACK_ROOT/PlayerSettingsCS2/PlayerSettingsApi/PlayerSettingsApi.csproj"
PS_CORE="$STACK_ROOT/PlayerSettingsCS2/PlayerSettings/PlayerSettings.csproj"
MM_API="$STACK_ROOT/MenuManagerCS2/MenuManagerApi/MenuManagerApi.csproj"
MM_CORE="$STACK_ROOT/MenuManagerCS2/MenuManagerCore/MenuManagerCore.csproj"
WP="$SKIN_DIR/WeaponPaints.csproj"

echo "==> Stack root : $STACK_ROOT"
echo "==> Config     : $CONFIG"

# --- 1. restore + build the whole solution (verification gate) ----------------
echo "==> Restoring solution"
dotnet restore "$SLN"
echo "==> Building solution ($CONFIG)"
dotnet build "$SLN" -c "$CONFIG" --no-restore
dotnet run --project "$SKIN_DIR/tests/StartupChecks.csproj" -c "$CONFIG"

rm -rf "$DIST" "$STAGE"
mkdir -p "$DIST/addons/counterstrikesharp/shared" \
         "$DIST/addons/counterstrikesharp/plugins" \
         "$DIST/addons/counterstrikesharp/gamedata"

# --- 2. compute the set of assemblies the server already supplies -------------
# (the transitive closure of CounterStrikeSharp.API itself)
echo "==> Computing CounterStrikeSharp-provided assembly set"
PROBE="$STAGE/_probe"
mkdir -p "$PROBE"
cat > "$PROBE/probe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CounterStrikeSharp.API" Version="$CSS_VERSION" />
  </ItemGroup>
</Project>
EOF
dotnet build "$PROBE/probe.csproj" -c Release -o "$PROBE/out" >/dev/null
CSS_SET="$STAGE/css_provided.txt"
( cd "$PROBE/out" && ls *.dll 2>/dev/null | grep -iv '^probe\.dll$' ) | sort -u > "$CSS_SET"
echo "    $(wc -l < "$CSS_SET") server-provided assemblies will be pruned from plugin folders"

# --- 3. publish helper --------------------------------------------------------
# publish a plugin, drop into dist/.../plugins/<name>, prune server-provided
# assemblies and any shared-API assembly (those live in shared/).
publish_plugin() {
  local proj="$1" name="$2"
  local out="$DIST/addons/counterstrikesharp/plugins/$name"
  echo "==> Publishing plugin $name"
  dotnet publish "$proj" -c "$CONFIG" -o "$out" --no-restore >/dev/null

  # prune server-provided framework assemblies
  while IFS= read -r dll; do
    rm -f "$out/$dll"
  done < "$CSS_SET"
  rm -f "$out/CounterStrikeSharp.API.dll"

  # shared capability interfaces must NOT be duplicated in plugin folders
  rm -f "$out/PlayerSettingsApi.dll" "$out/MenuManagerApi.dll"

  # drop debug symbols
  find "$out" -name '*.pdb' -delete
}

# --- 4. shared capability interface assemblies -------------------------------
echo "==> Staging shared capability interfaces"
mkdir -p "$DIST/addons/counterstrikesharp/shared/PlayerSettingsApi" \
         "$DIST/addons/counterstrikesharp/shared/MenuManagerApi"
PS_API_DLL="$(dirname "$PS_API")/bin/$CONFIG/net10.0/PlayerSettingsApi.dll"
MM_API_DLL="$(dirname "$MM_API")/bin/$CONFIG/net10.0/MenuManagerApi.dll"
cp "$PS_API_DLL" "$DIST/addons/counterstrikesharp/shared/PlayerSettingsApi/"
cp "$MM_API_DLL" "$DIST/addons/counterstrikesharp/shared/MenuManagerApi/"

# --- 5. plugins ---------------------------------------------------------------
publish_plugin "$PS_CORE" "PlayerSettings"
publish_plugin "$MM_CORE" "MenuManagerCore"
publish_plugin "$WP"      "WeaponPaints"

# SQLitePCLRaw 2.1.13 ships a Linux x64 library requiring glibc 2.34. Keep the
# same SQLite version but use our reproducible glibc 2.28 build on older hosts.
SQLITE_COMPAT="$SKIN_DIR/3rd_party/sqlite/linux-x64/libe_sqlite3.so"
if [ ! -s "$SQLITE_COMPAT" ]; then
  echo "ERROR: Build the compatible SQLite library with bash 3rd_party/sqlite/build-compat.sh" >&2
  exit 1
fi
SQLITE_OUT="$DIST/addons/counterstrikesharp/plugins/PlayerSettings/runtimes/linux-x64/native/libe_sqlite3.so"
cp "$SQLITE_COMPAT" "$SQLITE_OUT"
bash "$SKIN_DIR/tests/verify-sqlite-abi.sh" "$SQLITE_OUT"
dotnet run --project "$SKIN_DIR/tests/stack/StackChecks.csproj" -c "$CONFIG" -- "$SQLITE_OUT"

# --- 6. gamedata --------------------------------------------------------------
echo "==> Copying gamedata"
cp "$SKIN_DIR/gamedata/weaponpaints.json" "$DIST/addons/counterstrikesharp/gamedata/"

# --- 7. Skinchanger weapon catalog data --------------------------------------
# English catalogs are copied by dotnet publish and embedded in WeaponPaints.dll.
# An explicit WEAPONPAINTS_DATA directory can add translations or override them.
WP_OUT="$DIST/addons/counterstrikesharp/plugins/WeaponPaints"
if [ -n "${WEAPONPAINTS_DATA:-}" ]; then
  DATA_SRC="$WEAPONPAINTS_DATA"
  if [ ! -d "$DATA_SRC" ]; then
    echo "ERROR: WEAPONPAINTS_DATA directory does not exist: $DATA_SRC" >&2
    exit 1
  fi
  echo "==> Copying weapon catalog data from $DATA_SRC"
  cp -R "$DATA_SRC/." "$WP_OUT/data/"
fi
for catalog in skins gloves agents music collectibles; do
  if [ ! -s "$WP_OUT/data/${catalog}_en.json" ]; then
    echo "ERROR: Packaged catalog is missing: $WP_OUT/data/${catalog}_en.json" >&2
    exit 1
  fi
done

rm -rf "$STAGE"
echo
echo "==> Done. Deployment tree:"
find "$DIST" -maxdepth 4 -type d | sed "s#$DIST#dist#"
echo
echo "Copy the contents of dist/ into your CS2 server 'game/csgo/' directory."
