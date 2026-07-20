# Building & Publishing AgentFox Release Binaries

This produces the prebuilt archives that `install.ps1` / `install.sh` download:

| RID | OS / Arch | Archive |
|---|---|---|
| `win-x64` | Windows x64 | `agentfox-win-x64.zip` |
| `win-arm64` | Windows ARM64 | `agentfox-win-arm64.zip` |
| `linux-x64` | Linux x64 | `agentfox-linux-x64.tar.gz` |
| `linux-arm64` | Linux ARM64 | `agentfox-linux-arm64.tar.gz` |
| `osx-x64` | macOS Intel | `agentfox-osx-x64.tar.gz` |
| `osx-arm64` | macOS Apple Silicon | `agentfox-osx-arm64.tar.gz` |

Each archive contains the AgentFox binary **plus the Trading plugin** under `plugins/TradingAgent/`.

> **Automated path (recommended):** push a tag and let CI build/publish everything:
> ```bash
> git tag v1.0.0
> git push origin v1.0.0
> ```
> The [`.github/workflows/release.yml`](.github/workflows/release.yml) workflow builds all six
> targets and creates the GitHub Release. The rest of this file is the manual equivalent.

---

## Prerequisites

- .NET SDK 10.0 — https://dot.net
- [GitHub CLI](https://cli.github.com/) (`gh`) authenticated: `gh auth login`
- Run all commands from the **repository root**.

---

## 1. Build one target

`<rid>` is one of the RIDs in the table above.

**Publish the app:**

```bash
dotnet publish src/Agent/AgentFox.csproj \
  -c Release -r <rid> \
  --self-contained false \
  -p:PublishSingleFile=false -p:UseAppHost=true \
  -o staging/<rid>
```

**Publish the Trading plugin into the plugin folder:**

```bash
dotnet publish src/Plugins/TradingAgent/TradingAgent.csproj \
  -c Release -r <rid> \
  --self-contained false \
  -o staging/<rid>/plugins/TradingAgent
```

> For a fully standalone binary that does not need the .NET runtime on the target
> machine, add `--self-contained true` to both commands (larger archive).

---

## 2. Package the archive

**Linux / macOS (`.tar.gz`):**

```bash
tar -czf agentfox-<rid>.tar.gz -C staging/<rid> .
```

**Windows (`.zip`, PowerShell):**

```powershell
Compress-Archive -Path staging/<rid>/* -DestinationPath agentfox-<rid>.zip -Force
```

---

## 3. Build every target in one pass

### Linux / macOS (bash)

```bash
set -e
APP="src/Agent/AgentFox.csproj"
PLUGIN="src/Plugins/TradingAgent/TradingAgent.csproj"

# Only cross-build targets .NET can produce from this host. Framework-dependent
# publishes for any RID work cross-platform (they don't need to execute here).
for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
  echo "==> Building $rid"
  dotnet publish "$APP"    -c Release -r "$rid" --self-contained false -p:UseAppHost=true -o "staging/$rid"
  dotnet publish "$PLUGIN" -c Release -r "$rid" --self-contained false -o "staging/$rid/plugins/TradingAgent"
  case "$rid" in
    win-*) ( cd "staging/$rid" && zip -qr "../../agentfox-$rid.zip" . ) ;;
    *)     tar -czf "agentfox-$rid.tar.gz" -C "staging/$rid" . ;;
  esac
done
```

### Windows (PowerShell)

```powershell
$ErrorActionPreference = 'Stop'
$app    = 'src/Agent/AgentFox.csproj'
$plugin = 'src/Plugins/TradingAgent/TradingAgent.csproj'

foreach ($rid in 'win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64') {
  Write-Host "==> Building $rid"
  dotnet publish $app    -c Release -r $rid --self-contained false -p:UseAppHost=true -o "staging/$rid"
  dotnet publish $plugin -c Release -r $rid --self-contained false -o "staging/$rid/plugins/TradingAgent"
  if ($rid -like 'win-*') {
    Compress-Archive -Path "staging/$rid/*" -DestinationPath "agentfox-$rid.zip" -Force
  } else {
    tar -czf "agentfox-$rid.tar.gz" -C "staging/$rid" .
  }
}
```

---

## 4. Publish to a GitHub Release

Create the release and upload every archive (the filenames must match what the
installers expect — do not rename them):

```bash
gh release create v1.0.0 \
  agentfox-*.zip agentfox-*.tar.gz \
  --title "AgentFox v1.0.0" \
  --generate-notes
```

To add or replace assets on an existing release:

```bash
gh release upload v1.0.0 agentfox-*.zip agentfox-*.tar.gz --clobber
```

Once published, `install.ps1` / `install.sh` will download
`https://github.com/<owner>/<repo>/releases/latest/download/agentfox-<rid>.<ext>`
automatically and skip the source build.
