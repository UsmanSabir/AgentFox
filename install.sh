#!/usr/bin/env bash
set -euo pipefail

REPO_URL="${AGENTFOX_REPO_URL:-https://github.com/UsmanSabir/AgentFox.git}"
BRANCH="${AGENTFOX_BRANCH:-}"
INSTALL_DIR="${AGENTFOX_INSTALL_DIR:-$HOME/.agentfox}"
BINARY_URL="${AGENTFOX_BINARY_URL:-}"
BUILD_FROM_SOURCE="${AGENTFOX_BUILD_FROM_SOURCE:-0}"
TRADING_CHOICE_EXPLICIT=0
WITH_TRADING=1
if [ "${AGENTFOX_NO_TRADING:-0}" = "1" ] && [ "${AGENTFOX_WITH_TRADING:-0}" = "1" ]; then
  echo "Set only one of AGENTFOX_NO_TRADING or AGENTFOX_WITH_TRADING." >&2
  exit 1
fi
if [ "${AGENTFOX_NO_TRADING:-0}" = "1" ]; then
  WITH_TRADING=0
  TRADING_CHOICE_EXPLICIT=1
elif [ "${AGENTFOX_WITH_TRADING:-0}" = "1" ]; then
  TRADING_CHOICE_EXPLICIT=1
fi
SKIP_ONBOARDING="${AGENTFOX_SKIP_ONBOARDING:-0}"

for arg in "$@"; do
  case "$arg" in
    --no-trading) WITH_TRADING=0; TRADING_CHOICE_EXPLICIT=1 ;;
    --with-trading) WITH_TRADING=1; TRADING_CHOICE_EXPLICIT=1 ;;
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

# The published AgentFox binary is framework-dependent (--self-contained false), so at runtime
# it needs the ASP.NET Core 10 shared runtime. `dotnet --list-runtimes` reports the installed
# shared frameworks; an installed SDK also carries the runtime. Building from source additionally
# needs the SDK (see dotnet_has_sdk).
dotnet_has_runtime() {
  command -v dotnet >/dev/null 2>&1 || return 1
  dotnet --list-runtimes 2>/dev/null | grep -Eq '^Microsoft\.AspNetCore\.App 10\.'
}

dotnet_has_sdk() {
  command -v dotnet >/dev/null 2>&1 || return 1
  major="$(dotnet --version 2>/dev/null | cut -d. -f1)"
  [ -n "$major" ] && [ "$major" -ge 10 ] 2>/dev/null
}

# Downloads dotnet-install.sh and installs either the ASP.NET Core runtime or the full SDK.
# $1 = runtime|sdk
install_dotnet() {
  DOTNET_DIR="${HOME}/.dotnet"
  mkdir -p "$DOTNET_DIR"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  if [ "$1" = "runtime" ]; then
    info "Installing .NET ASP.NET Core Runtime 10.0 ..."
    bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir "$DOTNET_DIR" --quality GA
  else
    info "Installing .NET SDK 10.0 ..."
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR" --quality GA
  fi

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

# Prebuilt (framework-dependent) install: the runtime is enough; an existing SDK also works.
ensure_dotnet_runtime() {
  if dotnet_has_runtime; then info "Found .NET 10 ASP.NET Core runtime"; return; fi
  if dotnet_has_sdk; then info "Found .NET 10 SDK (provides the runtime)"; return; fi
  install_dotnet runtime
  info ".NET runtime installed."
}

# Building from source: the full SDK is required.
ensure_dotnet_sdk() {
  if dotnet_has_sdk; then info "Found dotnet $(dotnet --version)"; return; fi
  install_dotnet sdk
  info "dotnet SDK installed."
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

IS_UPDATE=0
if [ -f "$INSTALL_DIR/AgentFox" ] || [ -f "$INSTALL_DIR/AgentFox.dll" ]; then
  IS_UPDATE=1
fi

# Retain the existing feature set unless the caller explicitly changes it.
if [ "$IS_UPDATE" = "1" ] && [ "$TRADING_CHOICE_EXPLICIT" = "0" ]; then
  if { [ -f "$INSTALL_DIR/install-state.json" ] &&
       grep -Eq '"TradingInstalled"[[:space:]]*:[[:space:]]*false' "$INSTALL_DIR/install-state.json"; } ||
     { [ ! -f "$INSTALL_DIR/install-state.json" ] && [ ! -d "$INSTALL_DIR/plugins/TradingAgent" ]; }; then
    WITH_TRADING=0
  fi
fi

STAGE_DIR="$(mktemp -d "${TMPDIR:-/tmp}/agentfox-stage.XXXXXX")"
cleanup_stage() {
  if [ -n "${STAGE_DIR:-}" ] && [ -d "$STAGE_DIR" ]; then
    rm -rf "$STAGE_DIR"
  fi
}
trap cleanup_stage EXIT

# Try the prebuilt download first (no dotnet needed to fetch/extract), then provision the
# smallest .NET that satisfies the chosen path: the runtime for a prebuilt binary, the full
# SDK only when we actually have to compile from source.
RID="$(get_arch_suffix)"

INSTALLED=0
if [ "$BUILD_FROM_SOURCE" != "1" ]; then
  if try_download_prebuilt "$RID" "$STAGE_DIR"; then
    INSTALLED=1
  fi
fi

if [ "$INSTALLED" -eq 1 ]; then
  # Prebuilt binaries are framework-dependent (--self-contained false) — runtime is enough.
  ensure_dotnet_runtime
else
  ensure_dotnet_sdk
  ensure_git
  SOURCE_ROOT="$(resolve_source_root)"
  PROJECT_PATH="$SOURCE_ROOT/src/Agent/AgentFox.csproj"

  if [ ! -f "$PROJECT_PATH" ]; then
    echo "Could not find $PROJECT_PATH" >&2
    exit 1
  fi

  info "Publishing AgentFox to staging directory"
  dotnet publish "$PROJECT_PATH" -c Release -r "$RID" --self-contained false -p:PublishSingleFile=false -p:UseAppHost=true --verbosity minimal

  PUBLISH_DIR="$SOURCE_ROOT/src/Agent/bin/Release/net10.0/$RID/publish"
  if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Publish output was not created at $PUBLISH_DIR" >&2
    exit 1
  fi

  cp -R "$PUBLISH_DIR"/. "$STAGE_DIR"/

  # Publish the Trading plugin into plugins/ so the runtime plugin loader discovers it.
  if [ "$WITH_TRADING" = "1" ]; then
    PLUGIN_PROJECT="$SOURCE_ROOT/src/Plugins/TradingAgent/TradingAgent.csproj"
    if [ -f "$PLUGIN_PROJECT" ]; then
      info "Publishing Trading plugin into plugins/TradingAgent"
      dotnet publish "$PLUGIN_PROJECT" -c Release -r "$RID" --self-contained false -o "$STAGE_DIR/plugins/TradingAgent" --verbosity minimal
    fi
  fi

  # Publish the default bundled plugins into plugins/ so the runtime loader discovers them.
  # Each lands in its own plugins/<Name> folder with its .deps.json + dependencies. They are
  # enabled via the "Modules" list in appsettings.json; the key-only search plugins
  # (Brave/Tavily) stay inert until their API key is configured.
  for spec in \
    "src/Plugins/PageAgent/PageAgent.csproj:PageAgent" \
    "src/Plugins/AgentFox.BraveSearch/AgentFox.BraveSearch.csproj:BraveSearch" \
    "src/Plugins/AgentFox.TavilySearch/AgentFox.TavilySearch.csproj:TavilySearch" \
    "src/Plugins/AgentFox.DuckDuckGoSearch/AgentFox.DuckDuckGoSearch.csproj:DuckDuckGoSearch"; do
    proj="$SOURCE_ROOT/${spec%%:*}"
    dir="${spec##*:}"
    if [ -f "$proj" ]; then
      info "Publishing default plugin into plugins/$dir"
      dotnet publish "$proj" -c Release -r "$RID" --self-contained false -o "$STAGE_DIR/plugins/$dir" --verbosity minimal
    fi
  done
fi

# The prebuilt archive bundles the Trading plugin; strip it for a core-only install.
if [ "$WITH_TRADING" != "1" ] && [ -d "$STAGE_DIR/plugins/TradingAgent" ]; then
  info "Removing Trading plugin (--no-trading)"
  rm -rf "$STAGE_DIR/plugins/TradingAgent"
fi

# Defence in depth for old/pre-existing publish folders: only release defaults may ship.
for settings_file in "$STAGE_DIR"/appsettings*.json; do
  if [ -f "$settings_file" ] && [ "$(basename "$settings_file")" != "appsettings.defaults.json" ]; then
    rm -f "$settings_file"
  fi
done

# Ensure the native launcher is executable (prebuilt archives may not preserve the bit).
if [ -f "$STAGE_DIR/AgentFox" ]; then
  chmod +x "$STAGE_DIR/AgentFox" 2>/dev/null || true
fi

# ── Configuration migration and staged deployment ───────────────────────────
USER_CONFIG="${AGENTFOX_CONFIG_FILE:-$INSTALL_DIR/appsettings.user.json}"
LEGACY_CONFIG="$INSTALL_DIR/appsettings.json"
SOURCE_CONFIG=""
if [ -f "$USER_CONFIG" ]; then
  SOURCE_CONFIG="$USER_CONFIG"
elif [ -f "$LEGACY_CONFIG" ]; then
  SOURCE_CONFIG="$LEGACY_CONFIG"
fi
CANDIDATE_CONFIG="$STAGE_DIR/.appsettings.user.candidate.json"
SERVICE_WAS_STOPPED=0

if [ -n "$SOURCE_CONFIG" ]; then
  cp "$SOURCE_CONFIG" "$CANDIDATE_CONFIG"
  info "Validating and migrating existing configuration ..."
  if [ -x "$STAGE_DIR/AgentFox" ]; then
    "$STAGE_DIR/AgentFox" config migrate --config "$CANDIDATE_CONFIG"
  else
    dotnet "$STAGE_DIR/AgentFox.dll" config migrate --config "$CANDIDATE_CONFIG"
  fi
fi

if [ "$IS_UPDATE" = "1" ] && [ -x "$INSTALL_DIR/agentfox" ]; then
  if "$INSTALL_DIR/agentfox" --stop-service >/dev/null 2>&1; then
    SERVICE_WAS_STOPPED=1
  fi
fi

mkdir -p "$INSTALL_DIR"
if [ -n "$SOURCE_CONFIG" ]; then
  BACKUP_DIR="$INSTALL_DIR/backups"
  mkdir -p "$BACKUP_DIR"
  cp "$SOURCE_CONFIG" "$BACKUP_DIR/appsettings.user.$(date -u +%Y%m%d%H%M%S).$$.json"
  info "Configuration backup created in $BACKUP_DIR"
fi

info "Deploying staged AgentFox release ..."
# Shell globs intentionally exclude the dot-prefixed migration candidate and its temporary backup.
cp -R "$STAGE_DIR"/* "$INSTALL_DIR"/
if [ -f "$CANDIDATE_CONFIG" ]; then
  cp "$CANDIDATE_CONFIG" "$USER_CONFIG"
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

# Record update-relevant choices separately from user configuration.
if [ -x "$INSTALL_DIR/AgentFox" ]; then
  INSTALLED_VERSION="$("$INSTALL_DIR/AgentFox" --version 2>/dev/null || printf 'unknown')"
else
  INSTALLED_VERSION="$(dotnet "$INSTALL_DIR/AgentFox.dll" --version 2>/dev/null || printf 'unknown')"
fi
if [ "$SERVICE_WAS_STOPPED" = "1" ]; then
  "$INSTALL_DIR/agentfox" --start-service >/dev/null 2>&1 || true
fi
cat > "$INSTALL_DIR/install-state.json" <<EOF
{
  "InstalledVersion": "$INSTALLED_VERSION",
  "ConfigSchemaVersion": 1,
  "TradingInstalled": $([ "$WITH_TRADING" = "1" ] && printf 'true' || printf 'false'),
  "InstallSource": "$([ "$INSTALLED" = "1" ] && printf 'release' || printf 'source')",
  "RepoUrl": "$REPO_URL",
  "Branch": "${BRANCH:-main}",
  "ConfigFile": "$USER_CONFIG"
}
EOF

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
export AGENTFOX_REPO_URL="$REPO_URL"
export AGENTFOX_BRANCH="$UPDATE_BRANCH"
unset AGENTFOX_NO_TRADING AGENTFOX_WITH_TRADING
if [ -n "${AGENTFOX_CONFIG_FILE:-}" ]; then
  export AGENTFOX_CONFIG_FILE="$AGENTFOX_CONFIG_FILE"
fi
export $([ "$WITH_TRADING" = "1" ] && printf 'AGENTFOX_WITH_TRADING=1' || printf 'AGENTFOX_NO_TRADING=1')
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
