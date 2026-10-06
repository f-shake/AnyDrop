#!/usr/bin/env bash
# 构建前端并生成内嵌资源清单（Linux / macOS）。与 scripts/build-web.ps1 等价，
# 让纯 Linux 环境也能走完 DEPLOY.md 里的发布流程。
#
# 用法：SKIP_INSTALL=1 SKIP_TESTS=1 ./scripts/build-web.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WEB="$ROOT/web"
SERVER_DIR="$ROOT/src/AnyDrop.Server"
WWWROOT="$SERVER_DIR/wwwroot"
PROPS="$SERVER_DIR/wwwroot.g.props"

trap 'echo "前端构建未完成：wwwroot 与 wwwroot.g.props 仍是上一次成功构建的内容，此时不要发布。" >&2' ERR

cd "$WEB"
if [ "${SKIP_INSTALL:-0}" != "1" ]; then
  echo "==> npm ci"
  npm ci
fi
echo "==> vue-tsc 类型检查"
npm run typecheck
if [ "${SKIP_TESTS:-0}" != "1" ]; then
  echo "==> vitest"
  npm run test
fi
echo "==> vite build"
npm run build
cd "$ROOT"

rm -rf "$WWWROOT"
mkdir -p "$WWWROOT"
cp -R "$WEB/dist/." "$WWWROOT/"

{
  echo '<?xml version="1.0" encoding="utf-8"?>'
  echo '<!-- 由 scripts/build-web.sh 生成，请勿手工编辑。 -->'
  echo '<Project>'
  echo '  <ItemGroup>'
  find "$WWWROOT" -type f | LC_ALL=C sort | while read -r file; do
    rel="${file#"$WWWROOT"/}"
    win_rel="${rel//\//\\}"
    printf '    <EmbeddedResource Include="wwwroot\\%s" LogicalName="web/%s" />\n' "$win_rel" "$rel"
  done
  echo '  </ItemGroup>'
  echo '</Project>'
} > "$PROPS"

count="$(find "$WWWROOT" -type f | wc -l | tr -d ' ')"
echo "已登记 $count 个前端文件到 $PROPS"
