// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using FastBertTokenizer;
using Microsoft.Extensions.ObjectPool;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LocalEmbeddings;

public record BertOnnxOptions
{
    public bool CaseSensitive { get; init; }
    public int MaximumTokens { get; init; } = 512;
}

public sealed partial class LocalEmbedder : IDisposable
{
    private sealed class BertTokenizerPooledObjectPolicy : IPooledObjectPolicy<BertTokenizer>
    {
        private readonly string _vocabPath;
        private readonly bool _caseSensitive;

        public BertTokenizerPooledObjectPolicy(string vocabPath, bool caseSensitive)
        {
            _vocabPath = vocabPath;
            _caseSensitive = caseSensitive;
        }

        public BertTokenizer Create()
        {
            var tokenizer = new BertTokenizer();
            using var vocabReader = File.OpenText(_vocabPath);
            tokenizer.LoadVocabulary(vocabReader, !_caseSensitive);
            return tokenizer;
        }

        public bool Return(BertTokenizer obj) => true;
    }

    private readonly InferenceSession _session;
    private readonly ObjectPool<BertTokenizer> _tokenizerPool;
    private readonly BertOnnxOptions _options;

    public IReadOnlyDictionary<string, object?> Attributes => new Dictionary<string, object?>(); // Empty for now

    public LocalEmbedder(string modelName = "default", bool caseSensitive = false, int maximumTokens = 512)
        : this(modelName, new BertOnnxOptions { CaseSensitive = caseSensitive, MaximumTokens = maximumTokens })
    {
    }

    public LocalEmbedder(BertOnnxOptions options)
        : this("default", options)
    {
    }

    public LocalEmbedder(string modelName, BertOnnxOptions options)
    {
        _options = options;
        var modelPath = GetFullPathToModelFile(modelName, "model.onnx");
        var vocabPath = GetFullPathToModelFile(modelName, "vocab.txt");
        
        _session = new InferenceSession(modelPath);
        _tokenizerPool = new DefaultObjectPool<BertTokenizer>(new BertTokenizerPooledObjectPolicy(vocabPath, _options.CaseSensitive));
    }

    // A valid model file is well above this; build-time placeholder/dummy files (used when the
    // download is blocked, e.g. behind a proxy) are 0–4 bytes. Treat those as "not present" so we
    // fall through to extraction/download instead of handing a corrupt file to OnnxRuntime.
    private const long MinValidModelFileBytes = 1024;

    private static bool IsUsableModelFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length >= MinValidModelFileBytes;
    }

    private static string GetFullPathToModelFile(string modelName, string fileName)
    {
        // 1. Loose file next to the app (dev / framework-dependent publish — Content copy).
        var localPath = Path.Combine(AppContext.BaseDirectory, "LocalEmbeddingsModel", modelName, fileName);
        if (IsUsableModelFile(localPath))
            return localPath;

        // 2. Per-user writable data dir (single-file publish self-extract, or doctor repair).
        var dataPath = Path.Combine(GetModelDataDirectory(modelName), fileName);
        if (IsUsableModelFile(dataPath))
            return dataPath;

        // 3. Extract from the copy embedded in this assembly (single exe — first run).
        if (TryExtractEmbeddedModelFile(modelName, fileName, dataPath, force: false))
            return dataPath;

        throw new InvalidOperationException(
            $"Required embedding model file '{fileName}' for model '{modelName}' was not found. " +
            $"Looked next to the app ('{localPath}') and in the data directory ('{dataPath}'), " +
            $"and no embedded copy is available. Run 'AgentFox doctor --fix' to download it.");
    }

    /// <summary>
    /// Per-user writable directory the model is extracted to when it can't be found next to the app
    /// (the single-file-exe case). e.g. %LOCALAPPDATA%\AgentFox\LocalEmbeddingsModel\&lt;modelName&gt;.
    /// </summary>
    public static string GetModelDataDirectory(string modelName = "default")
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
            root = AppContext.BaseDirectory;
        return Path.Combine(root, "AgentFox", "LocalEmbeddingsModel", modelName);
    }

    /// <summary>
    /// True when both model files for <paramref name="modelName"/> can be resolved (extracting the
    /// embedded copy if needed). Lets callers (e.g. the doctor) probe without constructing a session.
    /// </summary>
    public static bool TryEnsureModelFiles(string modelName = "default")
    {
        try
        {
            GetFullPathToModelFile(modelName, "model.onnx");
            GetFullPathToModelFile(modelName, "vocab.txt");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Re-extracts the embedded model copy to the data directory, overwriting any existing files.
    /// Returns true only if an embedded copy exists and both files were written. Used by doctor repair.
    /// </summary>
    public static bool ExtractEmbeddedModel(string modelName = "default", bool force = true)
    {
        var dir = GetModelDataDirectory(modelName);
        var model = TryExtractEmbeddedModelFile(modelName, "model.onnx", Path.Combine(dir, "model.onnx"), force);
        var vocab = TryExtractEmbeddedModelFile(modelName, "vocab.txt", Path.Combine(dir, "vocab.txt"), force);
        return model && vocab;
    }

    private static bool TryExtractEmbeddedModelFile(string modelName, string fileName, string destPath, bool force)
    {
        if (!force && IsUsableModelFile(destPath))
            return true;

        var asm = typeof(LocalEmbedder).Assembly;
        var resourceName = $"LocalEmbeddingsModel.{modelName}.{fileName}";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Write to a temp file then move, so an interrupted extraction can't leave a half-written
        // model that later looks valid.
        var tmpPath = destPath + ".tmp";
        using (var fs = File.Create(tmpPath))
            stream.CopyTo(fs);

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tmpPath, destPath);

        // Reject an embedded placeholder (e.g. a proxy-blocked build that bundled a dummy).
        return IsUsableModelFile(destPath);
    }

    public EmbeddingF32 Embed(string inputText)
        => Embed<EmbeddingF32>(inputText, null);

    public Task<EmbeddingF32> EmbedAsync(string inputText)
        => EmbedAsync<EmbeddingF32>(inputText, null);

    // This synchronous overload is for back-compat with older versions of LocalEmbeddings. It actually performs the same
    // at present since the underlying BertOnnxTextEmbeddingGenerationService completes synchronously in all cases (though
    // that's not guaranteed to remain the same forever).
    public TEmbedding Embed<TEmbedding>(string inputText, Memory<byte>? outputBuffer = default)
        where TEmbedding : IEmbedding<TEmbedding>
        => EmbedAsync<TEmbedding>(inputText, outputBuffer).Result;

    public ReadOnlyMemory<float> GenerateEmbedding(string inputText)
    {
        var tokenizer = _tokenizerPool.Get();
        try
        {
            // Tokenize the input
            var (inputIdsMem, attentionMaskMem, tokenTypeIdsMem) = tokenizer.Encode(inputText);
            
            var tokensCount = inputIdsMem.Length;
            if (tokensCount > _options.MaximumTokens)
            {
                tokensCount = _options.MaximumTokens;
            }
            
            // Create input tensors
            var inputIds = new DenseTensor<long>(new[] { 1, tokensCount });
            var attentionMask = new DenseTensor<long>(new[] { 1, tokensCount });
            var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokensCount });
            
            for (int i = 0; i < tokensCount; i++)
            {
                inputIds[0, i] = inputIdsMem.Span[i];
                attentionMask[0, i] = attentionMaskMem.Span[i];
                tokenTypeIds[0, i] = tokenTypeIdsMem.Span[i];
            }
            
            // Create inputs
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
            };
            
            // Run inference
            using var results = _session.Run(inputs);
            
            // Get the output tensor (assuming "last_hidden_state")
            var outputTensor = results.First(r => r.Name == "last_hidden_state").AsTensor<float>();
            
            // Mean pooling across sequence dimension
            var embeddingSize = outputTensor.Dimensions[2];
            var embedding = new float[embeddingSize];
            
            for (int i = 0; i < embeddingSize; i++)
            {
                float sum = 0;
                for (int j = 0; j < tokensCount; j++)
                {
                    sum += outputTensor[0, j, i];
                }
                embedding[i] = sum / tokensCount;
            }
            
            return embedding;
        }
        finally
        {
            _tokenizerPool.Return(tokenizer);
        }
    }

    public Task<TEmbedding> EmbedAsync<TEmbedding>(string inputText, Memory<byte>? outputBuffer = default)
        where TEmbedding : IEmbedding<TEmbedding>
    {
        var embedding = GenerateEmbedding(inputText);
        return Task.FromResult(TEmbedding.FromModelOutput(embedding.Span, outputBuffer ?? new byte[TEmbedding.GetBufferByteLength(embedding.Length)]));
    }

    // Note that all the following materialize the result as a list, even though the return type is IEnumerable<T>.
    // We don't want to recompute the embeddings every time the list is enumerated.

    public IList<(string Item, EmbeddingF32 Embedding)> EmbedRange(
        IEnumerable<string> items)
        => items.Select(item => (item, Embed<EmbeddingF32>(item))).ToList();

    public IEnumerable<(string Item, TEmbedding Embedding)> EmbedRange<TEmbedding>(
        IEnumerable<string> items)
        where TEmbedding : IEmbedding<TEmbedding>
        => items.Select(item => (item, Embed<TEmbedding>(item))).ToList();

    public IEnumerable<(TItem Item, EmbeddingF32 Embedding)> EmbedRange<TItem>(
        IEnumerable<TItem> items,
        Func<TItem, string> textRepresentation)
        => items.Select(item => (item, Embed<EmbeddingF32>(textRepresentation(item)))).ToList();

    public IEnumerable<(TItem Item, TEmbedding Embedding)> EmbedRange<TItem, TEmbedding>(
        IEnumerable<TItem> items,
        Func<TItem, string> textRepresentation)
        where TEmbedding : IEmbedding<TEmbedding>
        => items.Select(item => (item, Embed<TEmbedding>(textRepresentation(item)))).ToList();

    public void Dispose()
    {
        _session.Dispose();
    }

    public Task<IList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IList<string> data, CancellationToken cancellationToken = default)
    {
        var results = new List<ReadOnlyMemory<float>>();
        foreach (var text in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(GenerateEmbedding(text));
        }
        return Task.FromResult<IList<ReadOnlyMemory<float>>>(results);
    }
}
