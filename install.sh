#!/usr/bin/env bash
set -euo pipefail

REPO_URL="${AGENTFOX_REPO_URL:-https://github.com/UsmanSabir/AgentFox.git}"
BRANCH="${AGENTFOX_BRANCH:-}"
INSTALL_DIR="${AGENTFOX_INSTALL_DIR:-$HOME/.agentfox}"
BINARY_URL="${AGENTFOX_BINARY_URL:-}"
BUILD_FROM_SOURCE="${AGENTFOX_BUILD_FROM_SOURCE:-0}"
WITH_TRADING=1
if [ "${AGENTFOX_NO_TRADING:-0}" = "1" ]; then
  WITH_TRADING=0
fi
SKIP_ONBOARDING="${AGENTFOX_SKIP_ONBOARDING:-0}"

for arg in "$@"; do
  case "$arg" in
    --no-trading) WITH_TRADING=0 ;;
    --with-trading) WITH_TRADING=1 ;;
    --skip-onboarding) SKIP_ONBOARDING=1 ;;
    *)
      echo "Unknown option: $arg (supported: --no-trading, --with-trading, --skip-onboarding)" >&2
      exit 1
      ;;
  esac
done

info() {
  echo "==> $*"
}

get_arch_suffix() {
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os" in
    Darwin)
      case "$arch" in
        arm64|aarch64) echo "osx-arm64" ;;
        x86_64|amd64) echo "osx-x64" ;;
        *) echo "osx-x64" ;;
      esac
      ;;
    *)
      case "$arch" in
        x86_64|amd64) echo "linux-x64" ;;
        aarch64|arm64) echo "linux-arm64" ;;
        *) echo "linux-x64" ;;
      esac
      ;;
  esac
}

default_binary_url() {
  rid="$1"
  if printf '%s' "$REPO_URL" | grep -Eq 'github\.com[:/]+[^/]+/[^/.]+'; then
    slug="$(printf '%s' "$REPO_URL" | sed -E 's#.*github\.com[:/]+([^/]+)/([^/.]+).*#\1/\2#')"
    echo "https://github.com/$slug/releases/latest/download/agentfox-$rid.tar.gz"
  fi
}

try_download_prebuilt() {
  rid="$1"
  dest="$2"
  url="${BINARY_URL:-$(default_binary_url "$rid")}"
  if [ -z "$url" ]; then
    info "Could not derive a prebuilt binary URL; building from source."
    return 1
  fi

  info "Looking for a prebuilt binary at $url"
  archive="/tmp/agentfox-$rid.tar.gz"
  if ! curl -fsSL "$url" -o "$archive"; then
    info "No prebuilt binary available. Building from source instead."
    return 1
  fi

  info "Downloaded prebuilt binary. Extracting ..."
  extract="/tmp/agentfox-prebuilt-$rid"
  rm -rf "$extract"
  mkdir -p "$extract"
  tar -xzf "$archive" -C "$extract"

  binary="$(find "$extract" -type f \( -name 'AgentFox' -o -name 'AgentFox.dll' \) | head -n 1)"
  if [ -z "$binary" ]; then
    info "Archive did not contain an AgentFox binary; building from source instead."
    return 1
  fi

  cp -R "$(dirname "$binary")"/. "$dest"/
  info "Prebuilt binary installed."
  return 0
}

ensure_git() {
  if command -v git >/dev/null 2>&1; then
    info "Found git $(git --version | head -n 1)"
    return
  fi

  info "Installing Git ..."
  if [ "$(uname -s)" = "Darwin" ]; then
    if command -v brew >/dev/null 2>&1; then
      brew install git
    else
      echo "Git not found. Install it with 'xcode-select --install' or Homebrew, then re-run." >&2
      exit 1
    fi
    return
  fi

  if [ "$(id -u)" -eq 0 ]; then
    SUDO=""
  elif command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
  else
    echo "sudo is required to install Git." >&2
    exit 1
  fi

  if command -v apt-get >/dev/null 2>&1; then
    $SUDO apt-get update
    $SUDO apt-get install -y git curl ca-certificates
  elif command -v dnf >/dev/null 2>&1; then
    $SUDO dnf install -y git curl ca-certificates
  elif command -v yum >/dev/null 2>&1; then
    $SUDO yum install -y git curl ca-certificates
  elif command -v apk >/dev/null 2>&1; then
    $SUDO apk add --no-cache git curl ca-certificates
  else
    echo "Unsupported Linux distribution for automatic Git installation." >&2
    exit 1
  fi
}

ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    version="$(dotnet --version | cut -d. -f1)"
    if [ "$version" -ge 10 ]; then
      info "Found dotnet $(dotnet --version)"
      return
    fi
  fi

  info "Installing .NET SDK 10.0 ..."
  DOTNET_DIR="${HOME}/.dotnet"
  mkdir -p "$DOTNET_DIR"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR" --quality GA

  export DOTNET_ROOT="$DOTNET_DIR"
  export PATH="$DOTNET_DIR:$PATH"
  case "${SHELL:-}" in
    *zsh) PROFILE="$HOME/.zshrc" ;;
    *) PROFILE="$HOME/.bashrc" ;;
  esac
  if ! grep -q 'DOTNET_ROOT' "$PROFILE" 2>/dev/null; then
    echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> "$PROFILE"
    echo 'export PATH="$HOME/.dotnet:$PATH"' >> "$PROFILE"
  fi
}

resolve_source_root() {
  if [ -f "$PWD/src/Agent/AgentFox.csproj" ]; then
    echo "$PWD"
    return
  fi

  if [ -f "$(dirname "$0")/src/Agent/AgentFox.csproj" ]; then
    echo "$(cd "$(dirname "$0")" && pwd)"
    return
  fi

  if [ -z "$REPO_URL" ]; then
    echo "Could not find the AgentFox source tree and AGENTFOX_REPO_URL was not provided." >&2
    exit 1
  fi

  WORK_ROOT="/tmp/agentfox-source"
  rm -rf "$WORK_ROOT"
  info "Cloning AgentFox from $REPO_URL"
  if [ -n "$BRANCH" ]; then
    git clone --branch "$BRANCH" --depth 1 "$REPO_URL" "$WORK_ROOT"
  else
    git clone --depth 1 "$REPO_URL" "$WORK_ROOT"
  fi
  echo "$WORK_ROOT"
}

profile_file() {
  case "${SHELL:-}" in
    *zsh) echo "$HOME/.zshrc" ;;
    *) echo "$HOME/.bashrc" ;;
  esac
}

add_to_path() {
  dir="$1"
  export PATH="$dir:$PATH"        # current session
  profile="$(profile_file)"
  # Idempotent: only append if this exact install dir isn't already wired in.
  if ! grep -qs "# AgentFox PATH ($dir)" "$profile" 2>/dev/null; then
    {
      echo ""
      echo "# AgentFox PATH ($dir)"
      echo "export PATH=\"$dir:\$PATH\""
    } >> "$profile"
    info "Added $dir to your PATH via $profile"
  else
    info "$dir is already on your PATH ($profile)"
  fi
}

mkdir -p "$INSTALL_DIR"

# The framework-dependent binary needs the .NET runtime whether it was prebuilt or built here.
ensure_dotnet
RID="$(get_arch_suffix)"

INSTALLED=0
if [ "$BUILD_FROM_SOURCE" != "1" ]; then
  if try_download_prebuilt "$RID" "$INSTALL_DIR"; then
    INSTALLED=1
  fi
fi

if [ "$INSTALLED" -eq 0 ]; then
  ensure_git
  SOURCE_ROOT="$(resolve_source_root)"
  PROJECT_PATH="$SOURCE_ROOT/src/Agent/AgentFox.csproj"

  if [ ! -f "$PROJECT_PATH" ]; then
    echo "Could not find $PROJECT_PATH" >&2
    exit 1
  fi

  info "Publishing AgentFox to $INSTALL_DIR"
  dotnet publish "$PROJECT_PATH" -c Release -r "$RID" --self-contained false -p:PublishSingleFile=false -p:UseAppHost=true --verbosity minimal

  PUBLISH_DIR="$SOURCE_ROOT/src/Agent/bin/Release/net10.0/$RID/publish"
  if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Publish output was not created at $PUBLISH_DIR" >&2
    exit 1
  fi

  cp -R "$PUBLISH_DIR"/. "$INSTALL_DIR"/

  # Publish the Trading plugin into plugins/ so the runtime plugin loader discovers it.
  if [ "$WITH_TRADING" = "1" ]; then
    PLUGIN_PROJECT="$SOURCE_ROOT/src/Plugins/TradingAgent/TradingAgent.csproj"
    if [ -f "$PLUGIN_PROJECT" ]; then
      info "Publishing Trading plugin into plugins/TradingAgent"
      dotnet publish "$PLUGIN_PROJECT" -c Release -r "$RID" --self-contained false -o "$INSTALL_DIR/plugins/TradingAgent" --verbosity minimal
    fi
  fi
fi

# The prebuilt archive bundles the Trading plugin; strip it for a core-only install.
if [ "$WITH_TRADING" != "1" ] && [ -d "$INSTALL_DIR/plugins/TradingAgent" ]; then
  info "Removing Trading plugin (--no-trading)"
  rm -rf "$INSTALL_DIR/plugins/TradingAgent"
fi

# Ensure the native launcher is executable (prebuilt archives may not preserve the bit).
if [ -f "$INSTALL_DIR/AgentFox" ]; then
  chmod +x "$INSTALL_DIR/AgentFox" 2>/dev/null || true
fi

cat > "$INSTALL_DIR/agentfox" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "$0")" && pwd)"
if [ -f "$DIR/AgentFox" ]; then
  "$DIR/AgentFox" "$@"
else
  dotnet "$DIR/AgentFox.dll" "$@"
fi
EOF
chmod +x "$INSTALL_DIR/agentfox"

# ── PATH registration ────────────────────────────────────────────────────────
# So users can run `agentfox` from anywhere instead of cd-ing into the install dir.
add_to_path "$INSTALL_DIR"

# ── Uninstaller ──────────────────────────────────────────────────────────────
cat > "$INSTALL_DIR/uninstall.sh" <<'EOF'
#!/usr/bin/env bash
# Uninstall AgentFox: remove the service, drop the PATH entry, delete this folder.
set -u
DIR="$(cd "$(dirname "$0")" && pwd)"
echo "==> Uninstalling AgentFox from $DIR"

if [ -x "$DIR/agentfox" ]; then
  echo "==> Removing the AgentFox service (if installed) ..."
  "$DIR/agentfox" --uninstall-service 2>/dev/null || \
    sudo "$DIR/agentfox" --uninstall-service 2>/dev/null || true
fi

# Strip the PATH line we appended (matched by the marker comment) from both profiles.
for profile in "$HOME/.bashrc" "$HOME/.zshrc"; do
  if [ -f "$profile" ] && grep -qs "# AgentFox PATH ($DIR)" "$profile"; then
    tmp="$(mktemp)"
    grep -v -F -e "# AgentFox PATH ($DIR)" -e "export PATH=\"$DIR:\$PATH\"" "$profile" > "$tmp" && mv "$tmp" "$profile"
    echo "==> Removed AgentFox from PATH in $profile"
  fi
done

cd "$HOME"
rm -rf "$DIR" && echo "==> AgentFox removed. Open a new terminal for the PATH change to take effect." \
  || echo "==> Could not delete $DIR (a process may be running). Stop AgentFox and delete it manually."
EOF
chmod +x "$INSTALL_DIR/uninstall.sh"

# ── Updater ──────────────────────────────────────────────────────────────────
UPDATE_BRANCH="${BRANCH:-main}"
RAW_INSTALL_URL=""
if printf '%s' "$REPO_URL" | grep -Eq 'github\.com[:/]+[^/]+/[^/.]+'; then
  slug="$(printf '%s' "$REPO_URL" | sed -E 's#.*github\.com[:/]+([^/]+)/([^/.]+).*#\1/\2#')"
  RAW_INSTALL_URL="https://raw.githubusercontent.com/$slug/$UPDATE_BRANCH/install.sh"
fi
cat > "$INSTALL_DIR/update.sh" <<EOF
#!/usr/bin/env bash
# Update AgentFox in place to the latest release.
set -euo pipefail
export AGENTFOX_INSTALL_DIR="\$(cd "\$(dirname "\$0")" && pwd)"
export AGENTFOX_SKIP_ONBOARDING=1
echo "==> Updating AgentFox to the latest release ..."
curl -fsSL "$RAW_INSTALL_URL" | bash
EOF
chmod +x "$INSTALL_DIR/update.sh"

echo
echo 'AgentFox installed successfully.'
echo "Install directory: $INSTALL_DIR"
echo
if [ "$WITH_TRADING" = "1" ]; then
  echo 'Trading plugin is enabled for LIVE auto-execution (AutoExecute=true, ExecutionMode=BoundedAuto).'
  echo 'The setup wizard can switch it to Paper mode and collect AHK credentials, PIN and allowed symbols.'
else
  echo 'Trading plugin NOT installed (--no-trading). Re-run the installer without --no-trading to add it.'
fi

# ── Onboarding ────────────────────────────────────────────────────────────────
# The wizard configures the LLM, plugin credentials, and (optionally) the system
# service. It offers to start the agent when done — if it installs and starts the
# service, the gateway is already listening and no second instance is launched.
# With `curl | bash` stdin is the pipe, so the wizard reads from /dev/tty instead;
# without any usable terminal (CI), print the commands and exit.
if [ "$SKIP_ONBOARDING" != "1" ] && { [ -t 0 ] || [ -t 1 ]; } && [ -e /dev/tty ]; then
  echo
  info "Starting the AgentFox setup wizard (re-run any time with: agentfox --onboarding) ..."
  "$INSTALL_DIR/agentfox" --onboarding < /dev/tty > /dev/tty 2>&1 || true
else
  echo
  echo 'Next steps:'
  echo '  agentfox --onboarding    # interactive setup (LLM, plugin credentials, service)'
  echo '  agentfox                 # start the agent (web UI on port 8080 by default)'
fi

echo
echo "'agentfox' is now on your PATH — open a NEW terminal (or run: source $(profile_file)),"
echo "then run it from anywhere."
echo 'Manage this install:'
echo "  $INSTALL_DIR/update.sh       # update to the latest release"
echo "  $INSTALL_DIR/uninstall.sh    # remove AgentFox (service + PATH + files)"
