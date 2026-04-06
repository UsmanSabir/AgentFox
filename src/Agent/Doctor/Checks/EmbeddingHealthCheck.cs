namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.Memory;
using Microsoft.Extensions.Configuration;

public class EmbeddingHealthCheck : IHealthCheckable
{
    private readonly IEmbeddingService _embeddingService;
    private readonly SqliteLongTermMemory? _sqliteMemory;
    private readonly IConfiguration _config;

    public string ComponentName => "Embedding Service";

    public EmbeddingHealthCheck(
        IEmbeddingService embeddingService,
        SqliteLongTermMemory? sqliteMemory,
        IConfiguration config)
    {
        _embeddingService = embeddingService;
        _sqliteMemory = sqliteMemory;
        _config = config;
    }

    public async Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();

        // 1. Null provider = vector search disabled
        if (_embeddingService is NullEmbeddingService)
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Warning, "Embedding Service",
                "No embedding model configured — vector search disabled. Set Models:Embedding in appsettings.json"));
            return results;
        }

        // 2. Ping: generate a test vector
        ReadOnlyMemory<float> testVector;
        try
        {
            testVector = await _embeddingService.GenerateAsync("health check probe", ct);
            results.Add(new HealthCheckResult(
                HealthStatus.Healthy, "Embedding Service",
                $"Embedding service reachable — vector dimension: {testVector.Length}"));
        }
        catch (Exception ex)
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Critical, "Embedding Service",
                $"Embedding service unreachable: {ex.Message}"));
            return results;
        }

        // 3. Check dimension mismatch against stored metadata
        if (_sqliteMemory == null) return results;

        var storedDimStr = _sqliteMemory.GetMetadata("embedding_dimension");
        var storedModel  = _sqliteMemory.GetMetadata("embedding_model");

        if (string.IsNullOrWhiteSpace(storedDimStr))
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Healthy, "Embedding Service",
                "No stored embeddings yet — dimension metadata will be written on first memory entry"));
            return results;
        }

        if (int.TryParse(storedDimStr, out var storedDim) && storedDim != testVector.Length)
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Critical, "Embedding Service",
                $"Dimension mismatch: stored vectors use dim={storedDim} (model: {storedModel ?? "unknown"}), " +
                $"current model produces dim={testVector.Length}. Vector search will fail.",
                CanAutoFix: true,
                FixDescription: "Regenerate embeddings or wipe and rebuild memory"));
        }
        else
        {
            results.Add(new HealthCheckResult(
                HealthStatus.Healthy, "Embedding Service",
                $"Stored dimension ({storedDim}) matches current model ({testVector.Length})"));
        }

        return results;
    }

    public async Task<FixResult> TryFixAsync(HealthCheckResult result, CancellationToken ct = default)
    {
        if (_sqliteMemory == null)
            return new FixResult(false, "No SQLite memory instance available");

        var choice = DoctorUI.ChooseFix(
            "How would you like to fix the embedding dimension mismatch?",
            new[]
            {
                "Regenerate embeddings from stored text (preserves all memories)",
                "Wipe all memories and start fresh (destructive)"
            });

        if (choice.StartsWith("Wipe"))
        {
            if (!DoctorUI.ConfirmDestructive(
                "Wipe all long-term memories",
                "This will permanently delete ALL stored memories and embeddings. This cannot be undone."))
                return new FixResult(false, "Cancelled by user");

            try
            {
                await _sqliteMemory.ClearAsync();
                _sqliteMemory.SetMetadata("embedding_dimension", "");
                _sqliteMemory.SetMetadata("embedding_model", "");
                return new FixResult(true, "All memories wiped. Embeddings will be regenerated on next memory write.", RequiresRestart: false);
            }
            catch (Exception ex)
            {
                return new FixResult(false, $"Wipe failed: {ex.Message}");
            }
        }
        else
        {
            // Regenerate embeddings from stored text
            return await RegenerateEmbeddingsAsync(ct);
        }
    }

    private async Task<FixResult> RegenerateEmbeddingsAsync(CancellationToken ct)
    {
        try
        {
            var entries = await _sqliteMemory!.GetAllAsync();
            if (entries.Count == 0)
                return new FixResult(true, "No entries to regenerate");

            int updated = 0;
            foreach (var entry in entries)
            {
                // Re-adding updates the vector via AddAsync's upsert logic
                await _sqliteMemory.AddAsync(entry);
                updated++;
            }

            return new FixResult(true, $"Regenerated embeddings for {updated} memory entries");
        }
        catch (Exception ex)
        {
            return new FixResult(false, $"Regeneration failed: {ex.Message}");
        }
    }
}
