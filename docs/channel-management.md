# Managing agent channels

Open **Channels** in the agent's Advanced interface. **Add channel** offers the providers registered
by the host and enabled plugins, with their required fields. Choose a unique name and topics, then
**Add & activate**. A connection failure keeps the saved channel visible as **Disconnected** and
offers **Activate** to retry. No restart is required.

**Deactivate** stops notification routing and inbound commands while keeping credentials and topics.
**Activate** reconnects the channel. **Remove** asks for confirmation, then deletes its saved entry
and disconnects it. Disabled channels remain visible, and their topics can still be edited.
An operation already in flight can finish; deactivation does not cancel an existing agent turn.

Stockji WhatsApp's **Active** state means its local adapter is ready. The hub checks the linked phone
and STOP/START state when sending; an active adapter is not evidence of successful phone delivery.

## API

Paths are relative to the host's authenticated `/api` group. Reads require ManagementViewer;
configuration writes require ManagementAdministrator. The existing management API key header applies.

| Method | Path | Body |
| --- | --- | --- |
| GET | `/channels` | — |
| GET | `/channels/providers` | — |
| POST | `/channels` | `{ "type": "telegram", "name": "my-alerts", "config": { "BotToken": "…", "ChatId": "…" }, "subscribe": "trading.order.>, hitl.>" }` |
| PUT | `/channels/{id}/enabled` | `{ "enabled": false }` |
| PUT | `/channels/{id}/subscriptions` | `{ "subscribe": "hitl.>" }` |
| DELETE | `/channels/{id}` | — |

Mutation responses include `id`, `message`, `persisted`, `persistError` and `warning`.
Adding a valid configuration that cannot connect succeeds with a connection warning and a saved,
disconnected channel. A failed activation returns an error and leaves a disabled channel disabled.
A failed enable/disable/topic save reports `persisted: false`: the live change works but reverts on
restart. Removal requires saving the deletion before disconnecting, so a failed save keeps the entry.

`manage_channel` uses the same lifecycle with `add`, `remove`, `activate`, `deactivate` and `subscribe`.
Use the unique channel id; a type is accepted only when it identifies exactly one configured channel.
HTTP mutations always require an exact id, preventing a stale name from targeting a different bot.

## Identity, configuration and retention

`ChannelConfigStore` is the single locked writer of the existing user configuration (`appsettings.user.json`,
or `AGENTFOX_CONFIG_FILE`). Its async management gate serializes lifecycle actions across web requests
and agent tools. Writes use a temporary sibling followed by an atomic replacement; the temporary file
is removed in `finally`. No new history, credential store or database is created.

An explicit user `Channels` array is authoritative, including an empty array. Startup reads that same
list, avoiding configuration providers' per-index merging that could resurrect deleted default entries.
If the user has not saved a Channels list, the release defaults file (`appsettings.defaults.json`)
supplies the initial list, and the first change copies it into the user file. Only that file is read:
never environment variables, command-line switches or hosted secrets, so a credential supplied only
through the environment is never written to disk. Channels are therefore configured in the files
(or from the Channels page), not through `Channels__N__…` environment variables.
Legacy object keys remain channel names. Unnamed array entries receive non-secret names (`channel-0`,
etc.) before disabled entries are filtered, and every write pins those names before indices can shift.

Channel settings follow existing configuration retention: they remain until explicitly removed.
Provider fields are never returned as saved values by the API. Secret fields are masked in the add
form; provider exceptions are not returned because they can contain credential-bearing URLs.
Provider schemas can mark additional fields `Secret`; token, password and secret keys are masked
automatically. The add form clears credentials after submission, type changes, cancellation, expiry
and unmount. Unsaved forms expire after 10 minutes; write requests time out after 65 seconds and are
aborted on unmount. Drafts are never written to browser storage. Existing test-channel outboxes keep
their provider's capacity and lifecycle.

## Verification

`ChannelManagementTests` exercises the actual route mapper with TestServer: authenticated lifecycle,
disabled topics, restart persistence, same-provider identity, legacy normalization, concurrent adds,
connection cleanup, secret-safe responses and viewer restrictions. The standard channel suite covers
subscription routing and mandatory notification fallback. `PR tests` also checks and builds the host UI.
