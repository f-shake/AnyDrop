#!/usr/bin/env bash
# 把本文件与 AnyDrop.Server、anydrop.json 放在同一目录，chmod +x run.sh 后执行。
set -euo pipefail
cd "$(dirname "$0")"
export ANYDROP__SERVER__URLS="${ANYDROP__SERVER__URLS:-http://127.0.0.1:8790}"
exec ./AnyDrop.Server "$@"
