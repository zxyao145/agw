#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-$ROOT_DIR/artifacts/publish}"
PUBLISH_MODE="${PUBLISH_MODE:-docker}"
IMAGE_NAME="${IMAGE_NAME:-agw:latest}"
APP_VERSION="${APP_VERSION:-0.1.0-local}"
RIDS="${RIDS:-win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64}"
DOCKER_PLATFORMS="${DOCKER_PLATFORMS:-linux/amd64,linux/arm64}"
DOCKER_PUSH="${DOCKER_PUSH:-false}"
WEB_DIR="$ROOT_DIR/src/frontend/web"
HOST_PROJECT="$ROOT_DIR/src/server/Agw.Host/Agw.Host.csproj"

case "$PUBLISH_MODE" in
  docker|portable|all) ;;
  *) echo "PUBLISH_MODE must be docker, portable, or all" >&2; exit 2 ;;
esac

build_web() {
  cd "$WEB_DIR"
  pnpm install --frozen-lockfile
  NEXT_OUTPUT_MODE=export pnpm build
}

build_portable() {
  build_web
  mkdir -p "$ARTIFACTS_DIR/portable"
  for rid in $RIDS; do
    output="$ARTIFACTS_DIR/portable/agw-server-$APP_VERSION-$rid"
    rm -rf "$output"
    dotnet publish "$HOST_PROJECT" -c Release -r "$rid" --self-contained true \
      -p:Version="$APP_VERSION" -o "$output"
    mkdir -p "$output/wwwroot"
    cp -R "$WEB_DIR/out/." "$output/wwwroot/"
    if [[ "$rid" == win-* ]]; then
      (cd "$ARTIFACTS_DIR/portable" && zip -qr "$(basename "$output").zip" "$(basename "$output")")
    else
      tar -czf "$output.tar.gz" -C "$(dirname "$output")" "$(basename "$output")"
    fi
  done
}

build_docker() {
  if [[ "$DOCKER_PUSH" == "true" ]]; then
    docker buildx build -f "$ROOT_DIR/Dockerfile.publish" \
      --platform "$DOCKER_PLATFORMS" --tag "$IMAGE_NAME" --push "$ROOT_DIR"
    return
  fi

  local image_repository image_tag platform architecture output
  image_repository="${IMAGE_NAME%:*}"
  image_tag="${IMAGE_NAME##*:}"
  if [[ "$image_repository" == "$IMAGE_NAME" || "$image_tag" == */* ]]; then
    image_repository="$IMAGE_NAME"
    image_tag="latest"
  fi

  mkdir -p "$ARTIFACTS_DIR/docker"
  IFS=',' read -r -a platforms <<< "$DOCKER_PLATFORMS"
  for platform in "${platforms[@]}"; do
    architecture="${platform##*/}"
    output="$ARTIFACTS_DIR/docker/agw-server-$APP_VERSION-linux-$architecture.tar"
    docker buildx build -f "$ROOT_DIR/Dockerfile.publish" \
      --platform "$platform" \
      --tag "$image_repository:$image_tag-$architecture" \
      --output "type=docker,dest=$output" "$ROOT_DIR"
  done
}

case "$PUBLISH_MODE" in
  docker) build_docker ;;
  portable) build_portable ;;
  all) build_docker; build_portable ;;
esac
