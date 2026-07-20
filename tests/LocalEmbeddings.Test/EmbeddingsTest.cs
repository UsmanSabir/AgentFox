// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assert = LocalEmbeddings.Test.AssertExtensions;
using System.Numerics.Tensors;

namespace LocalEmbeddings.Test;

[TestClass]
public class EmbeddingsTest
{
    [TestMethod]
    public async Task CanComputeEmbeddings()
    {
        var embeddingGenerator = new LocalEmbedder();

        var cat = await embeddingGenerator.EmbedAsync("cat");
        string[] sentences = [
            "dog",
            "kitten!",
            "Cats are good",
            "Cats are bad",
            "Tiger",
            "Wolf",
            "Grimsby Town FC",
            "Elephants are here",
        ];
        var sentenceEmbeddings = await embeddingGenerator.GenerateEmbeddingsAsync(sentences);
        var sentencesWithEmbeddings = sentences.Zip(sentenceEmbeddings, (s, e) => (Sentence: s, Embedding: e)).ToArray();

        var sentencesRankedBySimilarity = sentencesWithEmbeddings
            .OrderByDescending(s => TensorPrimitives.CosineSimilarity(cat.Values.Span, s.Embedding.Span))
            .Select(s => s.Sentence)
            .ToArray();

        Assert.Equals(new[] {
            "Cats are good",
            "kitten!",
            "Cats are bad",
            "Tiger",
            "dog",
            "Wolf",
            "Elephants are here",
            "Grimsby Town FC",
        }, sentencesRankedBySimilarity.ToList());
    }

    [TestMethod]
    public async Task IsCaseInsensitiveByDefault()
    {
        var embeddingGenerator = new LocalEmbedder();

        var catLower = await embeddingGenerator.EmbedAsync("cat");
        var catUpper = await embeddingGenerator.EmbedAsync("CAT");
        var similarity = TensorPrimitives.CosineSimilarity(catLower.Values.Span, catUpper.Values.Span);
        Assert.Equal(1, MathF.Round(similarity, 3));
    }

    [TestMethod]
    public async Task CanBeConfiguredAsCaseSensitive()
    {
        var embeddingGenerator = new LocalEmbedder(caseSensitive: true);

        var catLower = await embeddingGenerator.EmbedAsync("cat");
        var catUpper = await embeddingGenerator.EmbedAsync("CAT");
        var similarity = TensorPrimitives.CosineSimilarity(catLower.Values.Span, catUpper.Values.Span);
        Assert.NotEqual(1, MathF.Round(similarity, 3));
    }
}
