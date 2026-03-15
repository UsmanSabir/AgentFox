# Agent Heartbeat Configuration

> 🫀 Heartbeat monitoring system for agent health tracking. Stores and manages periodic health checks.

## Active Heartbeats

| Name | Task | Interval (s) | Max Missed | Status | Last Check |
|------|------|-------------|-----------|--------|------------|
| (none configured) | - | - | - | - | - |

## Configuration Format

Each heartbeat entry includes:
- **Name**: Unique identifier for the heartbeat
- **Task**: Command or script to execute for health check
- **Interval**: Seconds between checks
- **MaxMissed**: Number of missed checks before alert
- **Status**: current | paused 
- **LastCheck**: ISO 8601 timestamp

## Event Hooks Integration

Heartbeats integrate with the OpenClaw event hooks system:

- `on_heartbeat_triggered` - When a beat executes successfully
- `on_heartbeat_missed` - When a beat fails or is missed
- `on_heartbeat_added` - When a new beat is registered
- `on_heartbeat_removed` - When a beat is unregistered

## Management Commands

Use these commands with the HeartbeatService:

```
# Add a new heartbeat
add_heartbeat <name> <task> [interval_seconds] [max_missed]

# Remove a heartbeat
remove_heartbeat <name>

# Pause a heartbeat
pause_heartbeat <name>

# Resume a heartbeat
resume_heartbeat <name>

# List all heartbeats
list_heartbeats

# Get heartbeat status
get_heartbeat_status <name>

# Update heartbeat
update_heartbeat <name> [task] [interval_seconds] [max_missed]
```

## Example Heartbeats

```markdown
### System Health Monitor
- Task: `check_system_resources`
- Interval: 60 seconds
- Max Missed: 3
- Status: active

### API Connectivity Check
- Task: `test_api_endpoint`
- Interval: 30 seconds
- Max Missed: 2
- Status: active
```

## See Also
- [Runtime Scheduling Guide](../Runtime)
- [SubAgent Lane System](../Agents/SUBAGENT_LANE_SYSTEM_DESIGN.md)
- [Event Hooks](../OPENCLAW_EVENT_HOOKS_GUIDE.md)
