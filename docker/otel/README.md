# Local OpenTelemetry setup

Everything needed to see AgentFox's traces and metrics on a development machine: an
OpenTelemetry Collector, Jaeger for traces, Prometheus for metrics.

## 1. Start the backend

```bash
docker compose -f docker/otel/docker-compose.yml up -d
```

| Service | URL | What it is |
| --- | --- | --- |
| Jaeger UI | http://localhost:16686 | trace search — the one you will actually use |
| Prometheus | http://localhost:9090 | metric queries (token usage, durations) |
| Collector OTLP gRPC | `localhost:4317` | where AgentFox exports **by default** |
| Collector OTLP HTTP | `localhost:4318` | only if you set `OtlpProtocol` (see below) |

## 2. Configure AgentFox

Telemetry is **off by default** and the settings live in the `Telemetry` section. Put them in
`src/Agent/appsettings.user.json` (gitignored) rather than `appsettings.json`, so a collector
endpoint from one machine never ships in a release:

```jsonc
{
  "Telemetry": {
    "Enabled": true,
    "ServiceName": "AgentFox",
    "OtlpEndpoint": "http://localhost:4317",
    "ConsoleExporter": false,
    "CaptureMessageContent": false
  }
}
```

Environment variables work too, and are the better fit for a container or a service host — the
standard OTLP names are honoured, so nothing AgentFox-specific is needed:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc            # optional; grpc is the default
```

`Telemetry:OtlpEndpoint` wins over `OTEL_EXPORTER_OTLP_ENDPOINT` when both are set.

### The two settings that decide whether this works

**Protocol must match the port.** gRPC is 4317, HTTP/protobuf is 4318, and gRPC is the default.
Crossing them fails *silently* — the SDK retries in the background and the application never
notices, so the only symptom is a collector that receives nothing. To use HTTP:

```jsonc
"OtlpEndpoint": "http://localhost:4318",
"OtlpProtocol": "http/protobuf"
```

**`CaptureMessageContent` exports prompts, responses and tool arguments.** It is false by default
and should stay that way unless the collector is somewhere you would be willing to paste a
conversation — anything a tool read into context leaves the process when it is on. Startup logs a
warning when it is enabled, and another when telemetry is enabled with no exporter configured at
all.

## 3. Confirm it is working

Start AgentFox. It logs one line at startup naming the sources and exporters:

```
Telemetry enabled for 'AgentFox'. Sources: AgentFox.Agent, AgentFox.Harness, AgentFox.Trading. Exporters: OTLP -> http://localhost:4317/.
```

Send the agent a message, then open http://localhost:16686, pick service **AgentFox**, and Find
Traces. You should see spans like `channel.message`, `command.execute Main`, `agent.run`,
`tool.execute shell` and — if a trade was attempted — `trading.execute`.

To confirm export independently of the UI, watch what the collector itself receives:

```bash
docker compose -f docker/otel/docker-compose.yml logs -f otel-collector
```

That distinguishes the two failures that look identical from the outside: *AgentFox is not
exporting* versus *the backend is not displaying*.

### Automated smoke test

`TelemetryCollectorSmokeTests` exports a real span through the real registration code. It skips
unless an endpoint is given, so it is safe to leave in the suite:

```bash
OTEL_SMOKE_ENDPOINT=http://localhost:4317 dotnet test                                   # gRPC
OTEL_SMOKE_ENDPOINT=http://localhost:4318 OTEL_SMOKE_PROTOCOL=http/protobuf dotnet test # HTTP
```

Both paths were verified this way against this compose file: the span reaches the collector and is
queryable in Jaeger with its `agentfox.correlation_id` attribute intact. That verification is the
only proof available for the HTTP signal path — the SDK's `AppendSignalPathToEndpoint` switch is
internal, so `TelemetryRegistration.ConfigureOtlp` appends `/v1/traces` and `/v1/metrics` itself
and the behaviour cannot be asserted from code.

## What to look for in a trace

| Span | Answers |
| --- | --- |
| `channel.message` | when a message arrived and which command it became |
| `command.execute {lane}` | how long it queued (`queue_wait_ms`) before a lane freed up |
| `agent.run {agent}` | the turn, end to end |
| `specialist.delegate {id}` | delegation, including `gate_wait_ms` on the concurrency gate |
| `tool.execute {tool}` | each tool call, and whether the gate refused it |
| `approval.request {trigger}` | **how long a human took** — the number that says whether the gate is a safeguard or a bottleneck |
| `trading.execute` | a broker submission, and the refusal reason when nothing was placed |
| `trading.reconcile` | a reconciliation pass, and `healthy=false` when it is what paused submission |

Two conventions worth knowing when reading them:

- **A refusal is an error status with a reason, not a successful span.** For a system whose safe
  behaviour is to decline, "why did nothing happen" is the question asked most.
- **`agentfox.correlation_id` is on every span**, and is the same value written to `correlation_id`
  on `trade_proposals`, `trading_executions`, `trading_order_events` and `reconciliation_runs` —
  see [Joining a trace to the ledger](#joining-a-trace-to-the-ledger) below.

## Joining a trace to the ledger

Jaeger only stores spans. It has no SQL console and cannot query the trading database, so the join
is manual and goes in one direction.

**1. Get the correlation id out of Jaeger.** Open a trace, click any span, and read
`agentfox.correlation_id` in the Tags panel. To search the other way — find the trace for a
correlation id you already have — put this in Jaeger's **Tags** search box on the left:

```
agentfox.correlation_id=8f3c1a90b2d74e05
```

**2. Run the SQL against the trading SQLite database**, in a terminal — not in Jaeger.

The database lives at `<workspace>/trading/trading.db` (`Plugins:TradingAgent:DatabasePath`
resolved against the first configured `Workspaces` entry). On this machine that is
`C:\Users\hisab\Desktop\Temp\trad\trading.db`.

```bash
sqlite3 "C:/Users/hisab/Desktop/Temp/trad/trading.db" \
  "SELECT execution_id, state, policy_version, created_utc
     FROM trading_executions
    WHERE correlation_id = '8f3c1a90b2d74e05';"
```

No `sqlite3` on PATH? Any SQLite client works — DB Browser for SQLite, or the VS Code SQLite
extension. **Open it read-only if the agent is running**: this is a live database with an active
writer, and the reconciliation and retention workers write to it on a timer.

Everything one request touched, across all four correlated tables:

```sql
SELECT 'execution' AS kind, execution_id AS id, state,      created_utc AS at
  FROM trading_executions   WHERE correlation_id = '8f3c1a90b2d74e05'
UNION ALL
SELECT 'proposal',  proposal_id,       status,     created_utc
  FROM trade_proposals      WHERE correlation_id = '8f3c1a90b2d74e05'
UNION ALL
SELECT 'event',     execution_id,      event_type, created_utc
  FROM trading_order_events WHERE correlation_id = '8f3c1a90b2d74e05'
UNION ALL
SELECT 'reconcile', reconciliation_id, state,      started_utc
  FROM reconciliation_runs  WHERE correlation_id = '8f3c1a90b2d74e05'
ORDER BY at;
```

**`correlation_id` is NULL on rows written before this shipped, and on any row written outside a
correlation.** That is deliberate — a row with no cause to point at says so, rather than carrying a
minted id that would look like real evidence to whoever queries by it later. An empty result
therefore means "not correlated", never "did not happen".

## Sending it somewhere else

The reason to run a collector rather than exporting straight to a backend: the destination is a
collector config change, not an application change. Add an exporter to
`otel-collector-config.yaml` and list it in the relevant pipeline — AgentFox's own settings never
move. For example, Azure Monitor:

```yaml
exporters:
  azuremonitor:
    connection_string: "${APPLICATIONINSIGHTS_CONNECTION_STRING}"

service:
  pipelines:
    traces:
      exporters: [debug, otlp/jaeger, azuremonitor]
```

## Shutting down

```bash
docker compose -f docker/otel/docker-compose.yml down
```

Jaeger's all-in-one image keeps traces **in memory**, so they are gone after this — expected for a
development setup, but worth knowing before wondering where yesterday's trace went.

## Not a production deployment

No auth, no TLS, no persistence, ports bound to the host, `0.0.0.0` receivers. This is a place for
spans to land while developing.
