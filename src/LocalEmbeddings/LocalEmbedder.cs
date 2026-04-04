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

    private static string GetFullPathToModelFile(string modelName, string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var fullPath = Path.Combine(baseDir, "LocalEmbeddingsModel", modelName, fileName);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Required file {fullPath} does not exist");
        }

        return fullPath;
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

    private ReadOnlyMemory<float> GenerateEmbedding(string inputText)
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
