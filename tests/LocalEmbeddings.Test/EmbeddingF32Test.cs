// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assert = LocalEmbeddings.Test.AssertExtensions;
using System.Numerics.Tensors;
using System.Text.Json;

namespace LocalEmbeddings.Test;

[TestClass]
public class EmbeddingF32Test
{
    [TestMethod]
    public void ByteRepresentationIsSameAsFloatExceptScaled()
    {
        using var embedder = new LocalEmbedder();
        var testSentence = "This is my test sentence";
        var floats = embedder.Embed(testSentence);
        var bytes = embedder.Embed<EmbeddingF32>(testSentence);

        // Check it's the same length
        Assert.Equal(floats.Values.Length, bytes.Values.Length);
        Assert.Equal(bytes.Buffer.Length, bytes.Values.Length + 4); // 1 byte per value, plus 4 for magnitude
        Assert.Equal(bytes.Buffer.Length, EmbeddingF32.GetBufferByteLength(floats.Values.Length));

        // Work out how we expect the floats to be scaled
        var expectedScaleFactor = sbyte.MaxValue / Math.Abs(TensorPrimitives.MaxMagnitude(floats.Values.Span));
        var scaledFloats = new float[floats.Values.Length];
        TensorPrimitives.Multiply(floats.Values.Span, expectedScaleFactor, scaledFloats);

        // Check the bytes match this. We'll allow up to 1 off due to rounding differences.
        for (var i = 0; i < floats.Values.Length; i++)
        {
            var actualByte = bytes.Values.Span[i];
            var expectedByte = (sbyte)scaledFloats[i];
            Assert.InRange(actualByte, expectedByte - 1, expectedByte + 1);
        }
    }

    [TestMethod]
    public void Similarity_ItemsAreExactlyRelatedToThemselves()
    {
        using var embedder = new LocalEmbedder();
        var testSentence = "This is my test sentence";
        var values = embedder.Embed<EmbeddingF32>(testSentence);
        Assert.Equal(1, MathF.Round(LocalEmbedder.Similarity(values, values), 3));
    }


    [TestMethod]
    public void Similarity_CanSwapInputOrderAndGetSameResults()
    {
        using var embedder = new LocalEmbedder();
        var cat = embedder.Embed<EmbeddingF32>("cat");
        var dog = embedder.Embed<EmbeddingF32>("dog");
        Assert.Equal(
            LocalEmbedder.Similarity(cat, dog),
            LocalEmbedder.Similarity(dog, cat));
    }


    [TestMethod]
    public void Similarity_ProducesExpectedResults()
    {
        using var embedder = new LocalEmbedder();

        var cat = embedder.Embed<EmbeddingF32>("cat");
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
        var sentencesRankedBySimilarity = sentences.OrderByDescending(
            s => LocalEmbedder.Similarity(cat, embedder.Embed<EmbeddingF32>(s))).ToArray();

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
    public void CanRoundTripThroughJson()
    {
        using var embedder = new LocalEmbedder();
        var cat = embedder.Embed<EmbeddingF32>("cat");
        var json = JsonSerializer.Serialize(cat);
        var deserializedCat = JsonSerializer.Deserialize<EmbeddingF32>(json);

        Assert.Equals(cat.Values.ToArray(), deserializedCat.Values.ToArray());
        Assert.Equal(1, MathF.Round(LocalEmbedder.Similarity(cat, deserializedCat), 3));
    }


    [TestMethod]
    public void CanRoundTripThroughByteBuffer()
    {
        using var embedder = new LocalEmbedder();
        var cat1 = embedder.Embed<EmbeddingF32>("cat");
        var cat2 = new EmbeddingF32(cat1.Buffer.ToArray());

        Assert.Equals(cat1.Buffer.ToArray(), cat2.Buffer.ToArray());
        Assert.Equal(1, MathF.Round(LocalEmbedder.Similarity(cat1, cat2), 3));
    }
}
