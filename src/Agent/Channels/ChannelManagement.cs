using System.Text.RegularExpressions;
using AgentFox.Plugins.Channels;

namespace AgentFox.Channels;

public sealed record ChannelManagementResult(
    bool Ok, string? Id = null, string? Error = null, bool Persisted = true,
    string? PersistError = null, string? Message = null, int StatusCode = 200,
    IReadOnlyList<string>? Subscriptions = null, bool? ReceivesAll = null, string? Previous = null,
    string? Warning = null);

/// <summary>
/// Shared, serialized lifecycle for HTTP and manage_channel. Uses the existing configuration only:
/// settings remain until explicitly removed; no additional credential store or history is created.
/// Public projections contain identities, subscriptions and state, never provider configuration.
/// </summary>
public sealed class ChannelManagement(ChannelManager manager, ChannelProviderCatalog catalog, ChannelConfigStore store)
{
    public object Snapshot()
    {
        var saved = store.Read();
        if (saved.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Duplicate configured channel names.");
        var live = manager.Channels;
        var rows = saved.Select(e => Project(e.Name!, e.Type, e.Enabled, e.SubscribeSpec,
            live.GetValueOrDefault(e.Name!))).ToList();
        var ids = saved.Select(e => e.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.AddRange(live.Values.Where(c => !ids.Contains(c.ChannelId))
            .Select(c => Project(c.ChannelId, c.Type, c.IsEnabled, c.Subscriptions.ToString(), c)));
        return new
        {
            ready = true, channels = rows, total = rows.Count,
            connected = live.Values.Count(c => c.IsEnabled && c.IsConnected),
            topics = NotificationTopics.Known.Select(t => new { name = t.Name, description = t.Description, mandatory = t.Mandatory })
        };
    }

    private object Project(string id, string type, bool enabled, string? spec, Channel? channel)
    {
        enabled = channel?.IsEnabled ?? enabled;
        var connected = enabled && channel?.IsConnected == true;
        var subscription = channel?.Subscriptions ?? TopicSubscription.Parse(spec);
        catalog.TryGetProvider(type, out var provider);
        return new
        {
            id, name = provider?.DisplayName ?? channel?.Name ?? type, type, enabled,
            isConnected = connected, status = !enabled ? "disabled" : connected ? "connected" : "disconnected",
            subscriptions = subscription.Filters, receivesAll = subscription.IsCatchAll,
            recordsMessages = channel is IInspectableChannel,
            receivedCount = (channel as IInspectableChannel)?.RecentMessages.Count ?? 0,
            providerAvailable = provider != null,
            // Stockji ConnectAsync means the local adapter is ready, not that the hub has a phone.
            deliveryStatus = type == "stockji-whatsapp" ? "Phone link checked when sending" : null
        };
    }

    public async Task<ChannelManagementResult> AddAsync(string? type, string? name,
        Dictionary<string, string>? fields, string? subscribe)
    {
        await store.ManagementGate.WaitAsync();
        try
        {
            type = type?.Trim().ToLowerInvariant();
            if (type == null || !catalog.TryGetProvider(type, out var provider) || provider == null)
                return Invalid("Choose an available channel type.");
            var schema = new Dictionary<string, ChannelConfigField>(provider.GetConfigSchema(), StringComparer.OrdinalIgnoreCase);
            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in fields ?? [])
            {
                if (!schema.ContainsKey(key)) return Invalid($"Unsupported configuration field: {key}.");
                if (value == null || value.Length > 8192) return Invalid("A configuration value is invalid or too long.");
                config[key] = value;
            }
            foreach (var (key, field) in schema)
                if (field.Required && (!config.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
                    return Invalid($"{key} is required.");
            name = string.IsNullOrWhiteSpace(name) ? $"{type}-{Guid.NewGuid().ToString("N")[..8]}" : name.Trim();
            if (!Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$"))
                return Invalid("Use 1–64 letters, numbers, dots, underscores or hyphens for the name.");
            if (subscribe?.Length > 4096) return Invalid("Topic filters are too long.");
            if (!TopicSubscription.TryParse(subscribe, out var subscription, out var errors))
                return Invalid(string.Join(" ", errors));
            var saved = store.Read();
            if (saved.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) || manager.Channels.ContainsKey(name))
                return new(false, Error: "A channel already uses this name.", StatusCode: 409);
            config["Name"] = name;
            config["Subscribe"] = subscription.ToString();
            Channel? channel;
            try { (channel, _) = catalog.Create(type, config); }
            catch { return Invalid("The channel configuration is invalid."); }
            if (channel == null) return Invalid("The channel configuration is invalid.");
            channel.ChannelId = name;
            channel.Subscriptions = subscription;
            var error = store.Add(type, config);
            if (error != null) return SaveFailed();
            manager.AddChannel(channel);
            var connection = await ConnectAsync(channel);
            return connection.Ok ? connection : new(true, name, Message: "Channel saved.", Warning: connection.Error);
        }
        catch { return Invalid("Could not read or update channel configuration."); }
        finally { store.ManagementGate.Release(); }
    }

    public async Task<ChannelManagementResult> SetEnabledAsync(string id, bool enabled)
    {
        await store.ManagementGate.WaitAsync();
        try
        {
            var entry = FindSaved(id);
            var channel = Exact(id);
            if (entry == null && channel == null) return Missing();
            if (!enabled)
            {
                string? warning = null;
                // Gate routing before awaiting disconnect; a failed transport cannot dispatch commands.
                if (channel != null)
                {
                    channel.IsEnabled = false;
                    try { await channel.DisconnectAsync(); }
                    catch { warning = "Delivery is disabled, but the transport reported a disconnect error."; }
                }
                var error = store.SetEnabled(id, false);
                return new(true, id, Persisted: error == null, PersistError: error, Message: "Channel deactivated. Configuration kept.", Warning: warning);
            }
            if (channel == null)
            {
                if (entry == null) return Missing();
                try { (channel, _) = catalog.Create(entry.Type, entry.Config); }
                catch { return Invalid("Could not create this channel. Check its configuration and provider."); }
                if (channel == null) return Invalid("Could not create this channel. Check its configuration and provider.");
                channel.ChannelId = id;
                channel.Subscriptions = TopicSubscription.Parse(entry.SubscribeSpec);
                channel.IsEnabled = false;
                manager.AddChannel(channel);
            }
            if (!channel.IsConnected)
            {
                var result = await ConnectAsync(channel);
                if (!result.Ok) return result;
            }
            channel.IsEnabled = true;
            var persistError = store.SetEnabled(id, true);
            return new(true, id, Persisted: persistError == null, PersistError: persistError, Message: "Channel activated.");
        }
        catch { return Invalid("Could not read or update channel configuration."); }
        finally { store.ManagementGate.Release(); }
    }

    public async Task<ChannelManagementResult> RemoveAsync(string id)
    {
        await store.ManagementGate.WaitAsync();
        try
        {
            var entry = FindSaved(id);
            var channel = Exact(id);
            if (entry == null && channel == null) return Missing();
            // Require the deletion to be saved, so a failed save never resurrects a removed channel.
            if (entry != null && store.Remove(id) != null) return SaveFailed();
            try { await manager.RemoveChannelAsync(id); }
            catch { return new(true, id, Message: "Channel removed. Its transport reported a disconnect error."); }
            return new(true, id, Message: "Channel removed.");
        }
        catch { return Invalid("Could not read or update channel configuration."); }
        finally { store.ManagementGate.Release(); }
    }

    public async Task<ChannelManagementResult> SubscribeAsync(string id, string? spec)
    {
        if (spec?.Length > 4096) return Invalid("Topic filters are too long.");
        if (!TopicSubscription.TryParse(spec, out var subscription, out var errors)) return Invalid(string.Join(" ", errors));
        await store.ManagementGate.WaitAsync();
        try
        {
            var entry = FindSaved(id);
            var channel = Exact(id);
            if (entry == null && channel == null) return Missing();
            var previous = channel?.Subscriptions.ToString() ?? TopicSubscription.Parse(entry?.SubscribeSpec).ToString();
            if (channel != null) channel.Subscriptions = subscription;
            var error = store.SetSubscription(id, subscription);
            return new(true, id, Persisted: error == null, PersistError: error, Message: "Topics updated.",
                Subscriptions: subscription.Filters, ReceivesAll: subscription.IsCatchAll, Previous: previous);
        }
        catch { return Invalid("Could not read or update channel configuration."); }
        finally { store.ManagementGate.Release(); }
    }

    private Channel? Exact(string id) => manager.Channels.GetValueOrDefault(id);
    private ChannelConfigurationEntry? FindSaved(string id)
    {
        var matches = store.Read().Where(e => string.Equals(e.Name, id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new InvalidOperationException("Ambiguous channel identity.");
        return matches.SingleOrDefault();
    }

    private static async Task<ChannelManagementResult> ConnectAsync(Channel channel)
    {
        try
        {
            if (await channel.ConnectAsync()) return new(true, channel.ChannelId, Message: "Channel connected.");
        }
        catch { /* Never return provider exceptions: an HTTP URL may contain a bot token. */ }
        try { await channel.DisconnectAsync(); } catch { }
        return new(false, channel.ChannelId, "Could not connect. Check credentials and service availability, then retry Activate.", StatusCode: 502);
    }

    private static ChannelManagementResult Invalid(string error) => new(false, Error: error, StatusCode: 400);
    private static ChannelManagementResult Missing() => new(false, Error: "Channel not found.", StatusCode: 404);
    private static ChannelManagementResult SaveFailed() => new(false, Error: "Could not save channel configuration. No change was made.", StatusCode: 500);
}
