# Codex provider (the owner's own ChatGPT subscription)

The second subscription provider beside [Claude Code](claude-code-provider.md): an agent's owner signs in with their
own ChatGPT plan, through OpenAI's official **Codex CLI**, and chooses per purpose which AI answers
(**Settings → AI routing**: configured AI, Claude Code or Codex).

It is for the owner of that one agent, never a shared backend.

## Why not "Sign in with ChatGPT" directly

OpenAI also offers apps a direct "Sign in with ChatGPT" OAuth flow against the Responses API. It is **not built**:
OpenAI offers plan usage that way to "open-source tools and personal projects that run locally", a paid or remotely
hosted app joins a waitlist, and the terms forbid sharing or transferring plan usage or tokens and require token
storage to be local and under the user's control. This product is paid and hosted, so it does not qualify today.
The Codex CLI route needs none of that: it is OpenAI's own program, signed in by the owner.

## What it does

Every purpose in `AiPurposes` can be sent to Codex. **Text and JSON only.** Codex is a coding agent with a shell, so
every tool feature is switched off (`CodexCommand.DisabledFeatures`) and the read-only sandbox stays on as a second
layer. Measured 2026-11-25 on CLI 0.172.0: asked to read a canary file with its default tools, Codex ran `cat` on
it (a sandbox that happened not to start in that container was the only thing in the way); with the tools off it
attempted nothing.

Every call also reports an `error` item, "Code Mode is unavailable … install `codex-code-mode-host`". The npm package
carries that helper, and the image deliberately leaves it out: Code Mode gives the model a code runtime, a way to act.
Switching `code_mode` or `code_mode_host` off only rewords the notice, so `CodexStream` drops it, instead of letting it
become the reason shown for an unrelated failure such as a timeout.

```
codex exec --json --ephemeral --ignore-user-config --ignore-rules --skip-git-repo-check --sandbox read-only
           -c approval_policy="never" --disable shell_tool --disable unified_exec --disable plugins ... [-m <model>]
           [-c model_reasoning_effort="<effort>"] [--output-schema <file>] -
```

- The prompt goes on **stdin**. Codex has no system-prompt option, so the caller's instructions travel inside the
  message as an `<instructions>` block. Codex keeps its own coding-agent instructions too: a one-word answer costs
  about **14.5k input tokens** of the plan.
- **No text streaming.** Codex sends whole messages, and a turn can carry a preamble ("I'll read the file…") before
  the answer, so only the **last** `agent_message` is the reply.
- `error` events saying "Reconnecting… n/5" are Codex retrying its own connection; only a turn with no answer is a
  failure (`turn.failed`). Nothing in AgentFox retries.
- The environment is the shared allow-list (`CliEnvironment.Base`): `OPENAI_API_KEY`/`CODEX_API_KEY` can never switch
  a call to API billing, and `CODEX_HOME`, `HOME` and the working folder are private, under `{workspace}/codex`.
  Variable names are matched without regard to case: a child without `SystemRoot` cannot open a connection on
  Windows, and Git Bash spells it `SYSTEMROOT` (measured).

Measured per call: 75–103 MB peak on Windows, 177–244 MB on Linux; 6.6–19 s.

## Signing in

**Sign in with ChatGPT** on the Codex card runs the official `codex login --device-auth` and shows OpenAI's link
(`https://auth.openai.com/codex/device`) and one-time code. The owner approves in their own ChatGPT account; the CLI
finishes by itself ("Successfully logged in") and the card refreshes. Works on Windows and Linux, no terminal needed.

- **Device sign-in must first be enabled in ChatGPT's security settings.** Otherwise the attempt just waits out its
  15 minutes.
- The credential is the CLI's own `auth.json` in the private `CODEX_HOME`. The CLI refreshes it; AgentFox never reads,
  parses or returns it. **Sign out** runs `codex logout`.
- From a terminal instead: `CODEX_HOME=<workspace>/codex/codex-home codex login --device-auth` (in a container:
  `docker exec -it -u <app user> <container> env CODEX_HOME=/data/codex/codex-home /opt/codex/codex login --device-auth`).

## Endpoints

`GET /api/ai/codex` (viewer; `?refresh=true` re-reads `--version` and `login status`, no model call). Administrator:
`PUT /settings` (`model`, `reasoningEffort`), `POST /test` (one tiny model call), `GET|POST|DELETE /signin`,
`POST /signout`. Routing: `GET|PUT /api/ai/routing`.

## Hosted image

Included and pinned by default since 2026-11-25: premium `deploy/hosted/build.ps1 -CodexVersion` (default 0.172.0)
adds the binary (OpenAI's npm per-platform package, sha512-checked) at `/opt/codex/codex`; `none` leaves it out. The
binary is ~307 MB. The CLI's folders live on `/data/codex` (the workspace), so a sign-in survives a release. Hosted accounts need the operator's grant: CopyHub's `codex_enabled`
(migration 0064, off for every account, switched on /admin) reaches the agent as `features.codex`, and premium's
`ISubscriptionAccessPolicy` reads it per provider, so Codex can be off for an account that has Claude Code on.

## Removing it

- **One account (hosted):** turn its `codex_enabled` grant off on /admin. Every purpose goes back to the configured AI;
  nothing saved is deleted.
- **The owner's sign-in:** Settings → Codex → Sign out (`codex logout` deletes `auth.json`).
- **The program:** a hosted release with `-CodexVersion none`; on a desktop, uninstall the CLI. A sign-in left behind
  does no harm: the router sends a purpose to Codex only while the program is installed (`CliExecutable`, checked at
  most once a minute), so the purpose falls back and the routing card says "not installed".
- **Leftovers:** `{workspace}/codex` (logs and work files swept after a day; `auth.json` until signed out).

The operator's runbook (enabling per account, upgrading the pin, removing) is premium's
`docs/subscription-ai-clis.md`.

## Retention

| Data | Kept | Removed by |
| --- | --- | --- |
| `plugin-configs/codex.json` (model, reasoning effort; no credential) | one file, replaced whole; no age limit | the account's data deletion |
| `plugin-configs/ai-routing.json` (which provider each purpose uses) | one file, replaced whole; no age limit | the account's data deletion |
| `{workspace}/codex/codex-home/auth.json` (the CLI's sign-in) | until signed out | Sign out (`codex logout`); the account's data deletion |
| `{workspace}/codex/codex-home/log`, `sessions/`, `work/` (CLI leftovers, schema files) | 1 day | `SubscriptionHousekeeping`, at most hourly from a call that is starting anyway |
| Sign-in attempt (link, code, output) | memory, one attempt, ≤ 15 min | its deadline, completion, failure or cancel |
| Activity (last success/error/test) | memory, a fixed handful of values | a restart |

## Not done yet

- Exact Codex wordings for an expired sign-in and an exhausted plan are not yet measured; the classifier matches
  broadly and otherwise reports the (redacted) text.
- Not yet run on a hosted agent: memory inside the 512 MB container (177–244 MB per call measured in a Linux
  container, on top of the agent's ~140–180 MB), and a Codex call overlapping a Claude Code one.
