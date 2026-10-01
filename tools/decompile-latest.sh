#!/usr/bin/env bash
# 全量反编译当前安装的太吾绘卷游戏自身程序集。
# 仅反编译游戏自身 (Assembly-CSharp*, GameData*, TaiwuModdingLib),不含 Unity/System/三方引擎。
set -uo pipefail

TOOLDLL="/c/Users/jushi/.dotnet/tools/.store/ilspycmd/8.2.0.7535/ilspycmd/8.2.0.7535/tools/net6.0/any/ilspycmd.dll"
export DOTNET_ROLL_FORWARD=LatestMajor

# BUILDID 自动从 Steam 清单读取(游戏更新后直接重跑本脚本即可,无需手改);DECDATE 取当天。
APPID="838350"
GAME="/d/software/steam/steamapps/common/The Scroll Of Taiwu"
ACF="/d/software/steam/steamapps/appmanifest_$APPID.acf"
BUILDID=$(grep -E '"buildid"' "$ACF" 2>/dev/null | grep -oE '[0-9]+' | head -1)
[ -z "$BUILDID" ] && { echo "ERROR: 无法从 $ACF 读取 buildid(游戏未安装或路径变了)"; exit 1; }
DECDATE=$(date +%F)
MAN="$GAME/The Scroll of Taiwu_Data/Managed"
BACK="$GAME/Backend"
OUT_ROOT="${JHYL_DECOMPILED_ROOT:-/d/decompiled}"
OUT="$OUT_ROOT/taiwu_decomp_b$BUILDID"

rm -f "$OUT/_COMPLETE" 2>/dev/null
mkdir -p "$OUT"
{
  echo "buildid = $BUILDID"
  echo "game_name = 太吾绘卷:天幕心帷"
  echo "decompiled_at = $DECDATE"
  echo "tool = ilspycmd 8.2.0.7535 (DOTNET_ROLL_FORWARD=LatestMajor -> net8 runtime)"
  echo "source = $GAME"
  echo "scope = game-authored assemblies only (Assembly-CSharp*, GameData*, TaiwuModdingLib)"
} > "$OUT/VERSION.txt"

decompile() {
  local dll="$1" side="$2"
  local name; name=$(basename "$dll" .dll)
  local odir="$OUT/$side/$name"
  mkdir -p "$odir"
  local sz; sz=$(stat -c%s "$dll" 2>/dev/null)
  echo "[START] $side/$name ($sz bytes)"
  dotnet "$TOOLDLL" "$dll" -p -o "$odir" > "$odir/_ilspy.log" 2>&1
  local rc=$?
  local n; n=$(find "$odir" -name '*.cs' 2>/dev/null | wc -l)
  echo "[DONE rc=$rc] $side/$name -> $n cs files"
}

echo "================= FRONTEND (Unity/Mono) ================="
# 显式列出 GameData.dll(glob GameData.*.dll 不匹配无中缀的 GameData.dll)
for dll in "$MAN"/Assembly-CSharp.dll "$MAN"/Assembly-CSharp-firstpass.dll "$MAN"/TaiwuModdingLib.dll "$MAN"/GameData.dll "$MAN"/GameData.*.dll; do
  [ -f "$dll" ] && decompile "$dll" Frontend
done

echo "================= BACKEND (.NET 8) ================="
for dll in "$BACK"/GameData.dll "$BACK"/GameData.*.dll "$BACK"/TaiwuModdingLib.dll; do
  [ -f "$dll" ] && decompile "$dll" Backend
done

TOTAL=$(find "$OUT" -name '*.cs' 2>/dev/null | wc -l)
echo "================= ALL DONE: $TOTAL cs files total ================="
touch "$OUT/_COMPLETE"
