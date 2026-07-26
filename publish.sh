#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-$ROOT_DIR/artifacts/publish}"
PUBLISH_MODE="${PUBLISH_MODE:-docker}"
IMAGE_NAME="${IMAGE_NAME:-agw:latest}"
IMAGE_TAGS="${IMAGE_TAGS:-$IMAGE_NAME}"
APP_VERSION="${APP_VERSION:-0.1.0-local}"
RIDS="${RIDS:-win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64}"
DOCKER_PLATFORMS="${DOCKER_PLATFORMS:-linux/amd64,linux/arm64}"
DOCKER_PUSH="${DOCKER_PUSH:-false}"
WEB_DIR="$ROOT_DIR/src/clients"
WEB_OUTPUT="$WEB_DIR/web/out"
HOST_PROJECT="$ROOT_DIR/src/server/Agw.Host/Agw.Host.csproj"

case "$PUBLISH_MODE" in
  docker|portable|all) ;;
  *) echo "PUBLISH_MODE must be docker, portable, or all" >&2; exit 2 ;;
esac

build_web() {
  (
    cd "$WEB_DIR"
    pnpm install --frozen-lockfile --filter @agw/clients --filter @agw/web...
    NEXT_OUTPUT_MODE=export pnpm exec turbo run build --filter=@agw/web...
  )
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
    cp -R "$WEB_OUTPUT/." "$output/wwwroot/"
    if [[ "$rid" == win-* ]]; then
      (cd "$ARTIFACTS_DIR/portable" && zip -qr "$(basename "$output").zip" "$(basename "$output")")
    else
      tar -czf "$output.tar.gz" -C "$(dirname "$output")" "$(basename "$output")"
    fi
  done
}

build_docker() {
  local -a image_tags tag_args
  local image_tag
  while IFS= read -r image_tag; do
    [[ -n "$image_tag" ]] && image_tags+=("$image_tag")
  done < <(printf '%s\n' "$IMAGE_TAGS" | tr ', ' '\n\n')

  if [[ "${#image_tags[@]}" -eq 0 ]]; then
    echo "IMAGE_TAGS must contain at least one image tag" >&2
    exit 2
  fi

  for image_tag in "${image_tags[@]}"; do
    tag_args+=(--tag "$image_tag")
  done

  if [[ "$DOCKER_PUSH" == "true" ]]; then
    docker buildx build -f "$ROOT_DIR/Dockerfile" \
      --platform "$DOCKER_PLATFORMS" \
      --build-arg "APP_VERSION=$APP_VERSION" \
      --label "org.opencontainers.image.version=$APP_VERSION" \
      "${tag_args[@]}" --push "$ROOT_DIR"
    return
  fi

  local image_repository image_tag platform architecture output
  image_tag="${image_tags[0]}"
  image_repository="${image_tag%:*}"
  local image_version="${image_tag##*:}"
  if [[ "$image_repository" == "$image_tag" || "$image_version" == */* ]]; then
    image_repository="$image_tag"
    image_version="latest"
  fi

  mkdir -p "$ARTIFACTS_DIR/docker"
  IFS=',' read -r -a platforms <<< "$DOCKER_PLATFORMS"
  for platform in "${platforms[@]}"; do
    architecture="${platform##*/}"
    output="$ARTIFACTS_DIR/docker/agw-server-$APP_VERSION-linux-$architecture.tar"
    docker buildx build -f "$ROOT_DIR/Dockerfile" \
      --platform "$platform" \
      --build-arg "APP_VERSION=$APP_VERSION" \
      --label "org.opencontainers.image.version=$APP_VERSION" \
      --tag "$image_repository:$image_version-$architecture" \
      --output "type=docker,dest=$output" "$ROOT_DIR"
  done
}

case "$PUBLISH_MODE" in
  docker) build_docker ;;
  portable) build_portable ;;
  all) build_docker; build_portable ;;
esac
