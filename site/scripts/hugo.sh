#!/usr/bin/env bash
# 使用系统或本地 Go 构建指定站点。Build the selected site with system or local Go.
set -euo pipefail
site_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$site_dir"
target="${1:-}"
case "$target" in
    home) config="hugo.yaml,home/hugo.yaml" ;;
    docs) config="hugo.yaml" ;;
    *) echo 'Usage: hugo.sh <home|docs> [Hugo arguments]' >&2; exit 1 ;;
esac
shift
if ! command -v go >/dev/null 2>&1 && [[ -x "$site_dir/.cache/go/bin/go" ]]; then
    export PATH="$site_dir/.cache/go/bin:$PATH"
fi
if ! command -v go >/dev/null 2>&1; then
    echo 'Go 1.27 is required. Install it from https://go.dev/dl/ and retry.' >&2
    exit 1
fi
export GOWORK=off
cache_dir="$site_dir/.cache/hugo"
if [[ "${1:-}" == server ]]; then
    cache_dir="$site_dir/.cache/hugo-preview"
    config="$config,preview.yaml"
fi
exec hugo "$@" --config "$config" --cacheDir "$cache_dir"
