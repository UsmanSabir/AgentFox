using System.Text.Json.Nodes;
using AgentFox.Helpers;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ConfigMigratorTests
{
    private string _tempDirectory = null!;

    [TestInitialize]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"agentfox-config-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [TestMethod]
    public void LegacyMigrationPreservesModelsAccountsArraysAndUnknownKeys()
    {
        var path = Path.Combine(_tempDirectory, "appsettings.user.json");
        File.WriteAllText(path, """
            {
              // Legacy JSONC remains accepted.
              "Models": { "Primary": { "Provider": "OpenAI", "Model": "gpt-custom", "ApiKey": "secret" } },
              "Accounts": [{ "Name": "personal", "Token": "account-secret" }],
              "Channels": [{ "Type": "Discord", "Enabled": true }],
              "CustomExtension": { "KeepMe": 42 },
            }
            """);

        var result = ConfigMigrator.Migrate(path);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(result.Changed);
        Assert.IsNotNull(result.BackupPath);
        Assert.IsTrue(File.Exists(result.BackupPath));

        var migrated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.AreEqual(ConfigMigrator.CurrentSchemaVersion,
            migrated[ConfigMigrator.SchemaVersionProperty]!.GetValue<int>());
        Assert.AreEqual("gpt-custom", migrated["Models"]!["Primary"]!["Model"]!.GetValue<string>());
        Assert.AreEqual("secret", migrated["Models"]!["Primary"]!["ApiKey"]!.GetValue<string>());
        Assert.AreEqual("account-secret", migrated["Accounts"]![0]!["Token"]!.GetValue<string>());
        Assert.AreEqual(42, migrated["CustomExtension"]!["KeepMe"]!.GetValue<int>());
    }

    [TestMethod]
    public void MigrationIsIdempotentAndDoesNotCreateAnotherBackup()
    {
        var path = Path.Combine(_tempDirectory, "appsettings.user.json");
        File.WriteAllText(path, $$"""{ "ConfigSchemaVersion": {{ConfigMigrator.CurrentSchemaVersion}}, "LLM": { "Model": "mine" } }""");

        var result = ConfigMigrator.Migrate(path);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsFalse(result.Changed);
        Assert.IsNull(result.BackupPath);
        Assert.AreEqual(0, Directory.GetFiles(_tempDirectory, "*.backup-*").Length);
    }

    [TestMethod]
    public void ValidationRejectsAConfigurationFromANewerSchema()
    {
        var path = Path.Combine(_tempDirectory, "appsettings.user.json");
        File.WriteAllText(path, $$"""{ "ConfigSchemaVersion": {{ConfigMigrator.CurrentSchemaVersion + 1}} }""");

        var result = ConfigMigrator.Validate(path);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "newer");
    }

    [TestMethod]
    public void UserConfigurationOverridesDefaultsWithoutNeedingAMerge()
    {
        var defaults = Path.Combine(_tempDirectory, "appsettings.defaults.json");
        var user = Path.Combine(_tempDirectory, "appsettings.user.json");
        File.WriteAllText(defaults, """{ "LLM": { "Model": "release-default", "TimeoutSeconds": 30 }, "NewFeature": { "Enabled": true } }""");
        File.WriteAllText(user, """{ "LLM": { "Model": "user-model" } }""");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(defaults, optional: false)
            .AddJsonFile(user, optional: false)
            .Build();

        Assert.AreEqual("user-model", configuration["LLM:Model"]);
        Assert.AreEqual("30", configuration["LLM:TimeoutSeconds"]);
        Assert.AreEqual("True", configuration["NewFeature:Enabled"]);
    }
}
