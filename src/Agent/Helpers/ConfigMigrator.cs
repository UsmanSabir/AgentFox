using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentFox.Helpers;

/// <summary>
/// Applies explicit, sequential migrations to the user-owned configuration. Adding a new
/// default does not require a migration because appsettings.defaults.json is layered underneath
/// the user file; migrations are reserved for breaking key/type changes.
/// </summary>
public static class ConfigMigrator
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaVersionProperty = "ConfigSchemaVersion";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static ConfigMigrationResult Validate(string path)
    {
        if (!File.Exists(path))
            return ConfigMigrationResult.Fail($"Configuration file does not exist: {path}");

        try
        {
            var root = ReadObject(path);
            var version = ReadSchemaVersion(root);
            if (version < 0)
                return ConfigMigrationResult.Fail($"{SchemaVersionProperty} must be a non-negative integer.");
            if (version > CurrentSchemaVersion)
                return ConfigMigrationResult.Fail(
                    $"Configuration schema {version} is newer than this AgentFox build supports ({CurrentSchemaVersion}).");

            return ConfigMigrationResult.Ok(version, changed: false, backupPath: null,
                $"Configuration is valid (schema {version}).");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ConfigMigrationResult.Fail($"Configuration is invalid: {ex.Message}");
        }
    }

    public static ConfigMigrationResult Migrate(
        string path,
        int targetVersion = CurrentSchemaVersion,
        bool createBackup = true)
    {
        if (!File.Exists(path))
            return ConfigMigrationResult.Fail($"Configuration file does not exist: {path}");
        if (targetVersion < 0 || targetVersion > CurrentSchemaVersion)
            return ConfigMigrationResult.Fail(
                $"Target schema {targetVersion} is unsupported; this build supports 0-{CurrentSchemaVersion}.");

        try
        {
            var root = ReadObject(path);
            var version = ReadSchemaVersion(root);
            if (version < 0)
                return ConfigMigrationResult.Fail($"{SchemaVersionProperty} must be a non-negative integer.");
            if (version > CurrentSchemaVersion)
                return ConfigMigrationResult.Fail(
                    $"Configuration schema {version} is newer than this AgentFox build supports ({CurrentSchemaVersion}).");
            if (version > targetVersion)
                return ConfigMigrationResult.Fail(
                    $"Downgrading configuration from schema {version} to {targetVersion} is not supported.");

            var originalVersion = version;
            while (version < targetVersion)
            {
                ApplyMigration(root, version);
                version++;
                root[SchemaVersionProperty] = version;
            }

            if (version == originalVersion)
                return ConfigMigrationResult.Ok(version, changed: false, backupPath: null,
                    $"Configuration is already at schema {version}.");

            var backupPath = createBackup ? CreateBackup(path) : null;
            WriteAtomically(path, root);
            return ConfigMigrationResult.Ok(version, changed: true, backupPath,
                $"Migrated configuration from schema {originalVersion} to {version}.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ConfigMigrationResult.Fail($"Configuration migration failed: {ex.Message}");
        }
    }

    private static JsonObject ReadObject(string path)
    {
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text, nodeOptions: null, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        }) as JsonObject ?? throw new JsonException("The root value must be a JSON object.");
    }

    private static int ReadSchemaVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue(SchemaVersionProperty, out var value) || value is null)
            return 0;
        return value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var version)
            ? version
            : -1;
    }

    private static void ApplyMigration(JsonObject root, int fromVersion)
    {
        switch (fromVersion)
        {
            case 0:
                // Legacy appsettings.json already has the current shape. Schema 1 establishes
                // ownership/versioning without changing any user key, model, account, or array.
                return;
            default:
                throw new InvalidOperationException($"No configuration migration exists from schema {fromVersion}.");
        }
    }

    private static string CreateBackup(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backup = $"{path}.backup-{stamp}";
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    private static void WriteAtomically(string path, JsonObject root)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, root.ToJsonString(WriteOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}

public sealed record ConfigMigrationResult(
    bool Success,
    int SchemaVersion,
    bool Changed,
    string? BackupPath,
    string Message)
{
    public static ConfigMigrationResult Ok(int version, bool changed, string? backupPath, string message)
        => new(true, version, changed, backupPath, message);

    public static ConfigMigrationResult Fail(string message)
        => new(false, -1, false, null, message);
}

/// <summary>Minimal command handler used by installers before the full application is built.</summary>
public static class ConfigMigrationCommand
{
    public static bool TryRun(string[] args, string defaultPath, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 2 || !args[0].Equals("config", StringComparison.OrdinalIgnoreCase))
            return false;

        var operation = args[1].ToLowerInvariant();
        if (operation is not ("validate" or "migrate"))
        {
            Console.Error.WriteLine("Usage: agentfox config <validate|migrate> [--config <path>] [--target-version <n>]");
            exitCode = 2;
            return true;
        }

        var path = ReadOption(args, "--config") ?? defaultPath;
        var targetText = ReadOption(args, "--target-version");
        var target = ConfigMigrator.CurrentSchemaVersion;
        if (targetText is not null && !int.TryParse(targetText, out target))
        {
            Console.Error.WriteLine($"Invalid --target-version value: {targetText}");
            exitCode = 2;
            return true;
        }

        var result = operation == "validate"
            ? ConfigMigrator.Validate(path)
            : ConfigMigrator.Migrate(path, target);

        var output = result.Success ? Console.Out : Console.Error;
        output.WriteLine(result.Message);
        if (result.BackupPath is not null)
            output.WriteLine($"Backup: {result.BackupPath}");
        exitCode = result.Success ? 0 : 1;
        return true;
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
        }
        return null;
    }
}
