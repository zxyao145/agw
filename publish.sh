#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FRONTEND_DIR="$ROOT_DIR/src/frontend/web"
BACKEND_PROJECT="$ROOT_DIR/src/backend/Agw.Host/Agw.Host.csproj"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-$ROOT_DIR/artifacts/publish}"
IMAGE_NAME="${IMAGE_NAME:-agw:latest}"
CONFIGURATION="${CONFIGURATION:-Release}"
PUBLISH_MODE="${PUBLISH_MODE:-docker}"
APP_NAME="${APP_NAME:-agw}"
APP_VERSION="${APP_VERSION:-local}"
BACKEND_PORT="${BACKEND_PORT:-5015}"
WEB_PORT="${WEB_PORT:-3000}"

FRONTEND_BUILD_DIR="$ARTIFACTS_DIR/frontend"
BACKEND_PUBLISH_DIR="$ARTIFACTS_DIR/backend"
CONTEXT_DIR="$ARTIFACTS_DIR/image-context"
APP_DIR="$ARTIFACTS_DIR/app/$APP_NAME"
APP_ARCHIVE="$ARTIFACTS_DIR/${APP_NAME}-${APP_VERSION}-app.tar.gz"

usage() {
    cat <<USAGE
Usage: PUBLISH_MODE=<docker|app|all> [IMAGE_NAME=agw:latest] [APP_VERSION=local] ./publish.sh

Modes:
  docker  Build a Docker image that runs the backend and frontend together.
  app     Build an installable local app archive that starts backend and frontend services.
  all     Build both the Docker image and the installable app archive.

App mode ports:
  WEB_PORT=$WEB_PORT          Browser entrypoint for the installed app.
  BACKEND_PORT=$BACKEND_PORT  Local backend API port used by the frontend proxy.
USAGE
}

case "$PUBLISH_MODE" in
    docker|app|all) ;;
    -h|--help|help)
        usage
        exit 0
        ;;
    *)
        echo "Unsupported PUBLISH_MODE: $PUBLISH_MODE" >&2
        usage >&2
        exit 1
        ;;
esac

rm -rf "$ARTIFACTS_DIR"
mkdir -p "$FRONTEND_BUILD_DIR" "$BACKEND_PUBLISH_DIR"

publish_backend() {
    echo "[backend] Publish backend"
    dotnet publish "$BACKEND_PROJECT" -c "$CONFIGURATION" -o "$BACKEND_PUBLISH_DIR"
}

build_frontend_export() {
    echo "[frontend] Build static export"
    (
        cd "$FRONTEND_DIR"
        pnpm install --frozen-lockfile
        NEXT_OUTPUT_MODE=export pnpm build
        cp -R out/. "$FRONTEND_BUILD_DIR/"
    )
}

build_frontend_standalone() {
    echo "[frontend] Build standalone app server"
    (
        cd "$FRONTEND_DIR"
        pnpm install --frozen-lockfile
        BACKEND_API_BASE_URL="http://localhost:${BACKEND_PORT}" NEXT_OUTPUT_MODE=standalone pnpm build
    )
}

build_docker_image() {
    build_frontend_standalone
    publish_backend

    echo "[docker] Prepare image context"
    rm -rf "$CONTEXT_DIR"
    mkdir -p "$CONTEXT_DIR/backend" "$CONTEXT_DIR/frontend/.next"
    cp "$ROOT_DIR/Dockerfile.publish" "$CONTEXT_DIR/Dockerfile"
    cp -R "$BACKEND_PUBLISH_DIR"/. "$CONTEXT_DIR/backend/"
    cp -R "$FRONTEND_DIR/.next/standalone"/. "$CONTEXT_DIR/frontend/"
    cp -R "$FRONTEND_DIR/.next/static" "$CONTEXT_DIR/frontend/.next/static"
    if [[ -d "$FRONTEND_DIR/public" ]]; then
        cp -R "$FRONTEND_DIR/public" "$CONTEXT_DIR/frontend/public"
    fi
    cat > "$CONTEXT_DIR/docker-entrypoint.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
BACKEND_PORT="${BACKEND_PORT:-5015}"
WEB_PORT="${WEB_PORT:-8080}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:${BACKEND_PORT}}"
export PORT="$WEB_PORT"
export HOSTNAME="${HOSTNAME:-0.0.0.0}"

cd /app/backend
dotnet Agw.Host.dll &
BACKEND_PID="$!"

cd /app/frontend
node server.js &
FRONTEND_PID="$!"

trap 'kill "$FRONTEND_PID" "$BACKEND_PID" 2>/dev/null || true' INT TERM EXIT
wait -n "$BACKEND_PID" "$FRONTEND_PID"
SCRIPT
    chmod +x "$CONTEXT_DIR/docker-entrypoint.sh"

    echo "[docker] Build image: $IMAGE_NAME"
    docker build -t "$IMAGE_NAME" "$CONTEXT_DIR"
    echo "Docker image ready. Run with: docker run --rm -p ${WEB_PORT}:8080 $IMAGE_NAME"
}

write_app_scripts() {
    local scripts_dir="$APP_DIR/scripts"
    mkdir -p "$scripts_dir"

    cat > "$scripts_dir/run-backend.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
APP_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKEND_PORT="${BACKEND_PORT:-5015}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:${BACKEND_PORT}}"
cd "$APP_ROOT/backend"
exec dotnet Agw.Host.dll
SCRIPT

    cat > "$scripts_dir/run-frontend.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
APP_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WEB_PORT="${WEB_PORT:-3000}"
export HOSTNAME="${HOSTNAME:-127.0.0.1}"
export PORT="$WEB_PORT"
cd "$APP_ROOT/frontend"
exec node server.js
SCRIPT

    cat > "$scripts_dir/start.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
APP_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BACKEND_PID_FILE="$APP_ROOT/backend.pid"
FRONTEND_PID_FILE="$APP_ROOT/frontend.pid"
BACKEND_LOG_FILE="$APP_ROOT/backend.log"
FRONTEND_LOG_FILE="$APP_ROOT/frontend.log"
WEB_PORT="${WEB_PORT:-3000}"

start_process() {
    local name="$1"
    local pid_file="$2"
    local log_file="$3"
    local command="$4"

    if [[ -f "$pid_file" ]] && kill -0 "$(cat "$pid_file")" 2>/dev/null; then
        echo "$name is already running."
        return
    fi

    nohup "$command" > "$log_file" 2>&1 &
    echo "$!" > "$pid_file"
    echo "$name started. Logs: $log_file"
}

start_process "Agw backend" "$BACKEND_PID_FILE" "$BACKEND_LOG_FILE" "$APP_ROOT/scripts/run-backend.sh"
start_process "Agw frontend" "$FRONTEND_PID_FILE" "$FRONTEND_LOG_FILE" "$APP_ROOT/scripts/run-frontend.sh"
echo "Open http://localhost:$WEB_PORT in your browser."
SCRIPT

    cat > "$scripts_dir/stop.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
APP_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

stop_process() {
    local name="$1"
    local pid_file="$2"
    if [[ ! -f "$pid_file" ]]; then
        echo "$name is not running."
        return
    fi

    local pid
    pid="$(cat "$pid_file")"
    if kill -0 "$pid" 2>/dev/null; then
        kill "$pid"
    fi
    rm -f "$pid_file"
    echo "$name stopped."
}

stop_process "Agw frontend" "$APP_ROOT/frontend.pid"
stop_process "Agw backend" "$APP_ROOT/backend.pid"
SCRIPT

    cat > "$APP_DIR/install.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
APP_NAME="agw"
SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/share/$APP_NAME}"
BIN_DIR="${BIN_DIR:-$HOME/.local/bin}"
SERVICE_DIR="$HOME/.config/systemd/user"
BACKEND_PORT="${BACKEND_PORT:-5015}"
WEB_PORT="${WEB_PORT:-3000}"

mkdir -p "$BIN_DIR"
if [[ "$SOURCE_DIR" != "$INSTALL_DIR" ]]; then
    rm -rf "$INSTALL_DIR"
    mkdir -p "$INSTALL_DIR"
    cp -a "$SOURCE_DIR"/. "$INSTALL_DIR"/
fi
ln -sf "$INSTALL_DIR/scripts/start.sh" "$BIN_DIR/agw-start"
ln -sf "$INSTALL_DIR/scripts/stop.sh" "$BIN_DIR/agw-stop"
ln -sf "$INSTALL_DIR/scripts/run-backend.sh" "$BIN_DIR/agw-run-backend"
ln -sf "$INSTALL_DIR/scripts/run-frontend.sh" "$BIN_DIR/agw-run-frontend"

if command -v systemctl >/dev/null 2>&1; then
    mkdir -p "$SERVICE_DIR"
    cat > "$SERVICE_DIR/agw.service" <<SERVICE
[Unit]
Description=Agw local web app
After=network.target

[Service]
Type=forking
WorkingDirectory=$INSTALL_DIR
Environment=BACKEND_PORT=$BACKEND_PORT
Environment=WEB_PORT=$WEB_PORT
ExecStart=$INSTALL_DIR/scripts/start.sh
ExecStop=$INSTALL_DIR/scripts/stop.sh
RemainAfterExit=yes

[Install]
WantedBy=default.target
SERVICE
    systemctl --user daemon-reload
    systemctl --user enable --now agw.service
    echo "Agw installed and started as a user service."
else
    "$INSTALL_DIR/scripts/start.sh"
    echo "Agw installed and started in the background."
fi

echo "Open http://localhost:$WEB_PORT in your browser."
echo "Commands: agw-start, agw-stop, agw-run-backend, agw-run-frontend"
SCRIPT

    cat > "$APP_DIR/uninstall.sh" <<'SCRIPT'
#!/usr/bin/env bash
set -euo pipefail
APP_NAME="agw"
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/share/$APP_NAME}"
BIN_DIR="${BIN_DIR:-$HOME/.local/bin}"
SERVICE_FILE="$HOME/.config/systemd/user/agw.service"

if command -v systemctl >/dev/null 2>&1 && [[ -f "$SERVICE_FILE" ]]; then
    systemctl --user disable --now agw.service || true
    rm -f "$SERVICE_FILE"
    systemctl --user daemon-reload || true
elif [[ -x "$INSTALL_DIR/scripts/stop.sh" ]]; then
    "$INSTALL_DIR/scripts/stop.sh" || true
fi

rm -f "$BIN_DIR/agw-start" "$BIN_DIR/agw-stop" "$BIN_DIR/agw-run-backend" "$BIN_DIR/agw-run-frontend"
rm -rf "$INSTALL_DIR"
echo "Agw uninstalled."
SCRIPT

    chmod +x "$scripts_dir"/*.sh "$APP_DIR/install.sh" "$APP_DIR/uninstall.sh"
}

build_app_archive() {
    build_frontend_standalone
    publish_backend

    echo "[app] Prepare installable app"
    rm -rf "$APP_DIR"
    mkdir -p "$APP_DIR/backend" "$APP_DIR/frontend/.next"
    cp -R "$BACKEND_PUBLISH_DIR"/. "$APP_DIR/backend/"
    cp -R "$FRONTEND_DIR/.next/standalone"/. "$APP_DIR/frontend/"
    cp -R "$FRONTEND_DIR/.next/static" "$APP_DIR/frontend/.next/static"
    if [[ -d "$FRONTEND_DIR/public" ]]; then
        cp -R "$FRONTEND_DIR/public" "$APP_DIR/frontend/public"
    fi
    write_app_scripts

    cat > "$APP_DIR/README.txt" <<README
Agw local app package

Install and start:
  ./install.sh

After installation, open:
  http://localhost:${WEB_PORT}

The install script starts two local services:
  Frontend: http://localhost:${WEB_PORT}
  Backend:  http://localhost:${BACKEND_PORT}

Useful commands added to ~/.local/bin:
  agw-start
  agw-stop
  agw-run-backend
  agw-run-frontend

Override defaults during install:
  WEB_PORT=${WEB_PORT} BACKEND_PORT=${BACKEND_PORT} INSTALL_DIR=~/.local/share/agw ./install.sh
README

    tar -czf "$APP_ARCHIVE" -C "$ARTIFACTS_DIR/app" "$APP_NAME"
    echo "Installable app archive ready: $APP_ARCHIVE"
    echo "Install with: tar -xzf $APP_ARCHIVE && cd $APP_NAME && ./install.sh"
}

case "$PUBLISH_MODE" in
    docker)
        build_docker_image
        ;;
    app)
        build_app_archive
        ;;
    all)
        build_docker_image
        build_app_archive
        ;;
esac
