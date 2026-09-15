#!/usr/bin/env bash
# Run from any directory, with either system Go or the local ignored toolchain.
set -euo pipefail
site_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$site_dir"
if ! command -v go >/dev/null 2>&1 && [[ -x "$site_dir/.cache/go/bin/go" ]]; then
    export PATH="$site_dir/.cache/go/bin:$PATH"
fi
if ! command -v go >/dev/null 2>&1; then
    echo 'Go 1.27 is required. Install it from https://go.dev/dl/ and retry.' >&2
    exit 1
fi
export GOWORK=off
export HUGO_MODULE_WORKSPACE=off
export HUGO_CACHEDIR="$site_dir/.cache/hugo"
if [[ "${1:-}" == server ]]; then
    export HUGO_CACHEDIR="$site_dir/.cache/hugo-preview"
    export HUGO_RESOURCEDIR="$site_dir/.cache/preview-resources"
fi
exec hugo "$@"
