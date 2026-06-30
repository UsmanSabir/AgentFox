# Plugin Observability & Configuration API

This document describes the web API endpoints for monitoring plugin execution and managing plugin configurations dynamically.

## Overview

AgentFox provides two complementary observability features:

1. **Plugin Session Tracking** — Real-time audit trail of plugin tool invocations, results, and errors
2. **Plugin Configuration Management** — Dynamic runtime configuration updates without requiring code changes or restarts

Both are exposed via REST endpoints and can be viewed/controlled through the web UI.

---

## Plugin Session Tracking

Monitor plugin execution with a complete audit trail of tool invocations, successes, and failures.

### Endpoints

#### `GET /api/plugin-sessions`
List all active plugin sessions across all plugins.

**Response:**
```json
[
  {
    "pluginName": "trading-agent",
    "sessionId": "default",
    "createdAt": "2026-06-30T11:30:00Z",
    "lastActivityAt": "2026-06-30T11:35:45Z",
    "toolCount": 12,
    "successfulToolCount": 11,
    "failedToolCount": 1
  }
]
```

#### `GET /api/plugin-sessions/{pluginName}`
List active sessions for a specific plugin.

**Parameters:**
- `pluginName` (string, required) — Plugin identifier (e.g., `trading-agent`)

**Response:**
```json
[
  {
    "pluginName": "trading-agent",
    "sessionId": "default",
    "createdAt": "2026-06-30T11:30:00Z",
    "lastActivityAt": "2026-06-30T11:35:45Z",
    "toolCount": 12,
    "successfulToolCount": 11,
    "failedToolCount": 1
  }
]
```

#### `GET /api/plugin-sessions/{pluginName}/{sessionId}`
Get detailed audit trail for a specific session, including all tool executions.

**Parameters:**
- `pluginName` (string, required) — Plugin identifier
- `sessionId` (string, required) — Session ID

**Response:**
```json
{
  "pluginName": "trading-agent",
  "sessionId": "default",
  "createdAt": "2026-06-30T11:30:00Z",
  "lastActivityAt": "2026-06-30T11:35:45Z",
  "toolCount": 12,
  "successfulToolCount": 11,
  "failedToolCount": 1,
  "executions": [
    {
      "executionId": "exec-001",
      "toolName": "parse_signal",
      "arguments": {
        "message": "BUY SSGC @ 360-370"
      },
      "startedAt": "2026-06-30T11:30:15Z",
      "completedAt": "2026-06-30T11:30:16Z",
      "executionTimeMs": 1200,
      "status": "Completed",
      "result": "{\"is_signal\": true, \"count\": 1, \"signals\": [{\"symbol\": \"SSGC\", \"action\": \"BUY\", ...}]}"
    },
    {
      "executionId": "exec-002",
      "toolName": "check_market",
      "arguments": {},
      "startedAt": "2026-06-30T11:30:17Z",
      "completedAt": "2026-06-30T11:30:18Z",
      "executionTimeMs": 850,
      "status": "Completed",
      "result": "{\"is_open\": true, \"time\": \"11:35:45\", ...}"
    },
    {
      "executionId": "exec-003",
      "toolName": "place_order",
      "arguments": {
        "symbol": "SSGC",
        "action": "BUY",
        "price": 365,
        "target": 380
      },
      "startedAt": "2026-06-30T11:30:19Z",
      "completedAt": "2026-06-30T11:30:35Z",
      "executionTimeMs": 16200,
      "status": "Completed",
      "result": "{\"order_id\": \"ORD-12345\", \"status\": \"accepted\", ...}"
    }
  ]
}
```

#### `GET /api/plugin-sessions/{pluginName}/stats`
Get aggregate statistics for a plugin across all sessions.

**Parameters:**
- `pluginName` (string, required) — Plugin identifier

**Response:**
```json
{
  "pluginName": "trading-agent",
  "activeSessionCount": 1,
  "totalToolInvocations": 47,
  "successfulInvocations": 45,
  "failedInvocations": 2,
  "successRate": 0.957
}
```

---

## Plugin Configuration Management

Dynamically update plugin configurations at runtime. Changes apply immediately to system prompts and tool behavior without requiring restarts.

### Endpoints

#### `GET /api/plugin-config`
List all plugin configurations.

**Response:**
```json
[
  {
    "pluginName": "trading-agent",
    "config": {
      "autoExecute": false,
      "minConfidence": "HIGH",
      "duplicateWindowMinutes": 60
    },
    "lastUpdatedAt": "2026-06-30T10:00:00Z",
    "isDefault": false
  }
]
```

#### `GET /api/plugin-config/{pluginName}`
Get current configuration for a plugin.

**Parameters:**
- `pluginName` (string, required) — Plugin identifier

**Response:**
```json
{
  "pluginName": "trading-agent",
  "config": {
    "autoExecute": false,
    "minConfidence": "HIGH",
    "duplicateWindowMinutes": 60
  },
  "lastUpdatedAt": "2026-06-30T10:00:00Z",
  "isDefault": false
}
```

#### `POST /api/plugin-config/{pluginName}`
Update plugin configuration. Changes apply immediately.

**Parameters:**
- `pluginName` (string, required) — Plugin identifier

**Request Body:**
```json
{
  "config": {
    "autoExecute": true,
    "minConfidence": "MEDIUM"
  },
  "merge": true
}
```

**Fields:**
- `config` (object, required) — Configuration object with key-value pairs
- `merge` (boolean, default=true) — If true, merge with existing config; if false, replace entirely

**Response:**
```json
{
  "success": true,
  "message": "Configuration updated"
}
```

#### `DELETE /api/plugin-config/{pluginName}`
Delete a plugin's custom configuration (reverts to defaults).

**Parameters:**
- `pluginName` (string, required) — Plugin identifier

**Response:**
```json
{
  "success": true
}
```

---

## Usage Examples

### Example 1: Monitor TradingAgent Session

```bash
# Get all trading agent sessions
curl http://localhost:8080/api/plugin-sessions/trading-agent

# Get detailed audit trail for a session
curl http://localhost:8080/api/plugin-sessions/trading-agent/default

# Get success rate statistics
curl http://localhost:8080/api/plugin-sessions/trading-agent/stats
```

### Example 2: Disable Auto-Execution Temporarily

```bash
# Update trading agent config to disable auto-execution
curl -X POST http://localhost:8080/api/plugin-config/trading-agent \
  -H "Content-Type: application/json" \
  -d '{
    "config": {
      "autoExecute": false
    },
    "merge": true
  }'
```

The agent's system prompt immediately picks up the new setting. Next trade signal will be parsed and evaluated but NOT executed unless manually approved.

### Example 3: Raise Confidence Threshold

```bash
# Require HIGH confidence for auto-execution instead of MEDIUM
curl -X POST http://localhost:8080/api/plugin-config/trading-agent \
  -H "Content-Type: application/json" \
  -d '{
    "config": {
      "minConfidence": "HIGH"
    },
    "merge": true
  }'
```

Weak signals (LOW/MEDIUM confidence) are now logged but not executed.

### Example 4: Audit Trail for Debugging

```bash
# Fetch the complete session with all tool executions
curl http://localhost:8080/api/plugin-sessions/trading-agent/default | jq '.executions[] | {tool: .toolName, status: .status, timeMs: .executionTimeMs}'
```

Output:
```json
{
  "tool": "parse_signal",
  "status": "Completed",
  "timeMs": 1200
}
{
  "tool": "check_market",
  "status": "Completed",
  "timeMs": 850
}
{
  "tool": "place_order",
  "status": "Failed",
  "timeMs": 3500
}
```

---

## Implementation in Plugins

To enable session tracking and configuration management in your plugin:

### 1. Register Tool Hooks

In `IAgentAwareModule.OnAgentReadyAsync`:

```csharp
var sessionStore = _services!.GetRequiredService<AgentFox.Plugins.PluginSessionStore>();
var configMgr = _services!.GetRequiredService<AgentFox.Plugins.PluginConfigManager>();

// Track tool execution
context.OnToolPreExecute(async (toolName, args, executionId) =>
{
    sessionStore.OnToolStart("my-plugin", "default", toolName, args, executionId);
    await Task.CompletedTask;
});

context.OnToolPostExecute(async (toolName, result, ms, executionId) =>
{
    sessionStore.OnToolComplete("my-plugin", "default", toolName, result, ms, executionId);
    await Task.CompletedTask;
});

context.OnToolError(async (toolName, error, ms, executionId) =>
{
    sessionStore.OnToolError("my-plugin", "default", toolName, error, ms, executionId);
    await Task.CompletedTask;
});
```

### 2. Use Dynamic Configuration in System Prompt

```csharp
context.ContributeToSystemPrompt(
    contributorId: "my-plugin",
    fragmentProvider: () =>
    {
        var config = configMgr.GetConfig("my-plugin");
        var autoExecute = (config.ContainsKey("autoExecute") && config["autoExecute"] is bool b)
            ? b
            : defaultAutoExecute;
        
        return $"""
            Your instructions here.
            AutoExecute: {autoExecute}
            """;
    });
```

Configuration changes automatically refresh the prompt for the next LLM turn.

---

## Storage

- **Sessions** — Stored in-memory (ConcurrentDictionary). Survives across conversations within a single process but clears on app restart.
- **Configurations** — Persisted to disk in `{workspace}/plugin-configs/{pluginName}.plugin-config.json`. Survives restarts.

---

## Thread Safety

Both `PluginSessionStore` and `PluginConfigManager` are thread-safe and designed for concurrent access from multiple sessions and channels.
