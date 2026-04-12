# AgentFox Frontend

SvelteKit + Tailwind v4 UI for the AgentFox multi-agent framework.

## Dev (two terminals)

```bash
# Terminal 1 — .NET backend (defaults to :5000)
dotnet run --project src/Agent

# Terminal 2 — Vite dev server (:5173, proxies /api → backend)
cd src/frontend
npm install
npm run dev
```

The Vite proxy target is read from `BACKEND_URL` in `.env`:

| Scenario | URL |
|---|---|
| `dotnet run` (default) | `http://localhost:5000` |
| Installed as service | `http://localhost:8080` (Services.Port in appsettings.json) |

To override locally (not committed):
```bash
echo "BACKEND_URL=http://localhost:5000" > src/frontend/.env.local
```

## Build for production

```bash
# 1. Build frontend → src/Agent/wwwroot/
cd src/frontend && npm run build

# 2a. Framework-dependent single-file exe (wwwroot stays alongside exe)
dotnet publish src/Agent -c Release -r win-x64 -p:PublishSingleFile=true

# 2b. True single exe (wwwroot embedded inside the binary):
#     1. Uncomment <EmbeddedResource> in src/Agent/AgentFox.csproj
#     2. Run the publish command above
```

## How the proxy / same-origin flow works

```
Dev mode                         Production (embedded)
─────────────────                ──────────────────────────
Browser :5173                    Browser
  │                                │
  │  /api/*                        │  /api/*
  ▼                                ▼
Vite proxy  ──────►  .NET :5000  .NET (any port)
                     serves API   serves API + wwwroot
```

In production, the frontend is served as static files from the same .NET host
that handles the API — so `/api` calls are same-origin, no proxy needed.
