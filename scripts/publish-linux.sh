#!/usr/bin/env bash
# 在 Linux 上发布 AOT 单文件（必须先执行 scripts/build-web.ps1 生成内嵌资源，
# 或在 Linux 上先跑 npm ci && npm run build 再执行本脚本）。
set -euo pipefail

RUNTIME="${1:-linux-x64}"
OUTPUT="${2:-publish}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ ! -f "$ROOT/src/AnyDrop.Server/wwwroot.g.props" ]; then
  echo "缺少 wwwroot.g.props：请先构建前端并生成内嵌资源清单" >&2
  exit 1
fi

echo "==> dotnet publish -c Release -r $RUNTIME"
dotnet publish "$ROOT/src/AnyDrop.Server/AnyDrop.Server.csproj" \
  -c Release -r "$RUNTIME" -o "$ROOT/$OUTPUT" --nologo

chmod +x "$ROOT/$OUTPUT/AnyDrop.Server"
echo "完成：$ROOT/$OUTPUT/AnyDrop.Server"
