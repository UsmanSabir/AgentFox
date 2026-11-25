# Claude Code provider (the owner's own subscription)

An agent's owner can let some of its AI work run on **their own Claude Pro or Max subscription**, through the
official Claude Code CLI, instead of the configured provider. It is chosen per purpose under **Settings → Claude Code**.

It is for the owner of that one agent. It is not a shared backend: every agent signs in with its own owner's
subscription, and no operator ever handles the token (Anthropic's terms forbid routing other people's requests through
a subscription, or a developer collecting Claude.ai credentials). On a hosted agent the owner signs in from their own
agent's screen; nothing goes through the hub.

## What uses it

| Purpose (`AiPurposes`) | Default once signed in | Notes |
| --- | --- | --- |
| `stock-assessment` | on | `StockAssessmentService` |
| `rebuy-advice` | on | the Rebuy panel's "Ask AI" card (premium) |
| `chat` | off | the main chat agent only. **Text only**: no tools, so it cannot read the account, look up prices or trade |

Sub-agents, specialists and every other caller keep the configured client: they exist to call tools, and the CLI can
never hand a tool call back (it runs its own tool loop). A caller opts in by asking `IAiClientRouter.For(purpose)`;
the routing is decided on every call, so a change in Settings applies at once.

## How a call runs

`ClaudeCodeChatClient` starts one CLI process per call (stateless; the host owns the conversation), one at a time:

```
claude -p --model <m> --output-format stream-json --verbose --include-partial-messages
       --tools "" --strict-mcp-config --no-session-persistence --system-prompt-file <tmp>
       [--json-schema <schema>]          # only when the caller supplies a real schema
```

- The prompt goes on **stdin**; the system prompt in a temporary file (Windows caps a command line at ~32K).
- The child's environment is an **allow-list** (`ClaudeCodeCommand.Environment`). Auth precedence puts
  `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY` and `apiKeyHelper` above the subscription token, and in `-p` mode an API
  key is always used when present — so nothing is inherited that could switch a call to paid billing.
- A **private** `CLAUDE_CONFIG_DIR` and `HOME` under `{workspace}/claude-code`. With the owner's own `~/.claude` a call
  loads their skills, plugins and auto-memory even with `--tools ""` (measured). Bundled skills, CLAUDE.md files and
  auto-memory are switched off by environment variable.
- **Never** `--bare` (it does not read the subscription token at all, and the docs say it will become the default for
  `-p` — so the CLI version is pinned and auto-update is off) and never `--dangerously-skip-permissions`.
- A JSON request without a schema (what Rebuy sends) is asked for in the system prompt and any code fence stripped.
- Nothing retries. A refused sign-in or a usage limit is reported (`AiProviderException`, `Retryable: false`).

Measured on CLI 2.1.258: a call peaks at **~210–235 MB** (startup dominates) and takes 3–10 s.

## Signing in

| Way | Where | How |
| --- | --- | --- |
| **Sign in with Claude** | Linux (hosted, Docker) | the agent runs `claude setup-token` under `script` and relays the link; the owner approves on claude.ai and pastes back the code |
| **Paste a token** | anywhere | the owner runs `claude setup-token` on a computer with Claude Code and pastes the `sk-ant-oat01-…` token. It is tested before it replaces a saved one |

Facts the relay depends on (2026-11-25): `setup-token` prints nothing without a terminal (hence `script`; Windows would
need ConPTY, so Windows offers paste only); the code and Enter must be **separate** writes; after an error the CLI's
retry starts over with a new link, so the attempt ends and the owner starts again; the printed token is **wrapped**
with terminal codes between the pieces (`ClaudeCodeTerminalText`). The token only grants `user:inference` and lasts
one year; expiry is shown as an estimate counted from when it was saved.

The token is encrypted with the plugin-secrets key in `plugin-configs/claude-code.json` and goes nowhere but the CLI's
environment. No API response carries it; the raw sign-in terminal output (which contains it) is never logged.

## Endpoints (`/api/ai/claude-code`)

`GET` status (viewer; `?refresh=true` re-reads the version and `auth status` — no model call). Administrator only:
`PUT /settings`, `PUT /token`, `DELETE /token`, `POST /test` (one tiny model call), `GET|POST|DELETE /signin`,
`POST /signin/code`.

## Hosted access

`IClaudeCodeAccessPolicy` (default: allowed) lets an edition switch it off per account. An unreadable policy means
"not allowed" — the configured provider keeps working. A per-account hub flag is planned, pending memory measurements
on the hosting VM.

## Retention

| Data | Kept | Removed by |
| --- | --- | --- |
| `plugin-configs/claude-code.json` (encrypted token, model, choices) | one file, replaced whole; no age limit (configuration) | Remove sign-in; the account's data deletion |
| `{workspace}/claude-code/config` — the CLI's own `.claude.json` backups, `projects/`, `sessions/`; `work/` prompt files | 1 day | `ClaudeCodeHousekeeping`, at most hourly from a call that is starting anyway |
| Sign-in attempt (link, terminal output) | memory, one attempt, ≤ `SignInTimeoutSeconds` (600) | its deadline, completion, failure or cancel |
| Activity (last success/error/test, usage reading) | memory, a fixed handful of values | a restart |

## Not done yet

- Image: install the pinned CLI in the hosted Dockerfile (npm per-platform package, hash checked), and set
  `ClaudeCode__DataDirectory=/data/claude-code` — the default `{workspace}/claude-code` is under `/app` there, which
  the agent cannot write. (`plugin-configs/` is already linked to `/data`, so the token file is fine as it is.)
- Hosted per-account switch (CopyHub flag) implementing `IClaudeCodeAccessPolicy`.
- Chat on Claude Code still advertises attachments if the configured model accepts them; the attachment becomes a
  "cannot read attachments" note in the prompt rather than being refused up front.
