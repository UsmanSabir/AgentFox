using System.Net;
using LocalEmbeddings;
using Spectre.Console;

namespace AgentFox.Memory;

/// <summary>
/// Runtime acquisition/repair of the local embedding model. Used both by the first-run
/// setup prompt and the doctor's auto-fix. Tries the cheap path first (re-extract the copy
/// embedded in the single-file exe), then falls back to downloading from Hugging Face —
/// the same source the build-time MSBuild targets use.
/// </summary>
public static class ModelSetup
{
    // Mirrors LocalEmbeddings.targets (bge-micro-v2, quantized). Pinned to the same revision
    // so downloaded vectors match what the embedded/build-time model produces.
    private const string ModelUrl = "https://huggingface.co/SmartComponents/bge-micro-v2/resolve/72908b7/onnx/model_quantized.onnx";
    private const string VocabUrl = "https://huggingface.co/SmartComponents/bge-micro-v2/resolve/72908b7/vocab.txt";

    /// <summary>True when the model can already be loaded (loose, extracted, or embedded).</summary>
    public static bool IsAvailable(string modelName = "default")
        => LocalEmbedder.TryEnsureModelFiles(modelName);

    /// <summary>
    /// Ensures the model is present. Returns true if it's available afterwards.
    /// Order: re-extract embedded copy → download. No-op (returns true) if already present.
    /// </summary>
    public static async Task<bool> EnsureAsync(string modelName = "default", CancellationToken ct = default)
    {
        if (IsAvailable(modelName))
            return true;

        // Cheapest repair: the single-file exe carries an embedded copy.
        if (LocalEmbedder.ExtractEmbeddedModel(modelName, force: true) && IsAvailable(modelName))
        {
            AnsiConsole.MarkupLine("[green]✓[/] Restored embedding model from the bundled copy.");
            return true;
        }

        return await DownloadAsync(modelName, ct);
    }

    /// <summary>Downloads model.onnx + vocab.txt into the per-user data directory, with progress.</summary>
    public static async Task<bool> DownloadAsync(string modelName = "default", CancellationToken ct = default)
    {
        var dir = LocalEmbedder.GetModelDataDirectory(modelName);
        Directory.CreateDirectory(dir);

        try
        {
            // Use the system proxy and send the logged-in user's credentials — corporate
            // proxies otherwise reject the request with 407 Proxy Authentication Required.
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.GetSystemWebProxy(),
                DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

            await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn())
                .StartAsync(async ctx =>
                {
                    await DownloadFileAsync(http, ctx, "model.onnx", ModelUrl, Path.Combine(dir, "model.onnx"), ct);
                    await DownloadFileAsync(http, ctx, "vocab.txt", VocabUrl, Path.Combine(dir, "vocab.txt"), ct);
                });

            if (IsAvailable(modelName))
            {
                AnsiConsole.MarkupLine("[green]✓[/] Embedding model downloaded.");
                return true;
            }

            AnsiConsole.MarkupLine("[red]✗[/] Download finished but the model still can't be loaded.");
            return false;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗ Failed to download embedding model:[/] {ex.Message}");
            return false;
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient http, ProgressContext ctx, string label, string url, string destPath, CancellationToken ct)
    {
        var task = ctx.AddTask($"[dodgerblue1]{label}[/]");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        task.MaxValue = total ?? 1;

        // Download to a temp file then move, so an interrupted download isn't mistaken for a valid model.
        var tmpPath = destPath + ".tmp";
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmpPath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                if (total.HasValue)
                    task.Increment(read);
            }
        }

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tmpPath, destPath);

        task.Value = task.MaxValue;
    }
}
