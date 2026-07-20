// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Assert = LocalEmbeddings.Test.AssertExtensions;

namespace LocalEmbeddings.Test;

[TestClass]
public class LocalEmbedderFindClosestTest
{
    [TestMethod]
    public void CanFindClosestMatches_Static()
    {
        using var embedder = new LocalEmbedder();
        string[] candidates = ["Tea", "Latte", "Coffee", "Cherryade", "David Hasselhoff"];
        var embeddedCandidates = embedder.EmbedRange(candidates);

        var closest = LocalEmbedder.FindClosest(embedder.Embed("beans"), embeddedCandidates, 2);
        var closestWithScore = LocalEmbedder.FindClosestWithScore(embedder.Embed("beans"), embeddedCandidates, 2);

        Assert.Equals(new[] { "Coffee", "Latte" }, closest.Take(2).ToList());
        Assert.Collection(closestWithScore.Take(2),
            result => { Assert.Equal("Coffee", result.Item); Assert.InRange(result.Similarity, 0, 1.01f); },
            result => { Assert.Equal("Latte", result.Item); Assert.InRange(result.Similarity, 0, 1.01f); });
    }

    [TestMethod]
    public void CanFindClosestMatches_StaticName()
    {
        using var embedder = new LocalEmbedder();
        string[] candidates = ["Tea", "Latte", "Coffee", "Cherryade", "My name is Usman"];
        var embeddedCandidates = embedder.EmbedRange(candidates);

        var query = "tell me about me";
        var closest = LocalEmbedder.FindClosest(embedder.Embed(query), embeddedCandidates, 1);
        var closestWithScore = LocalEmbedder.FindClosestWithScore(embedder.Embed(query), embeddedCandidates, 1);

        Assert.Equals(new[] { "My name is Usman", "Tea" }, closest.Take(1).ToList());
        Assert.Collection(closestWithScore.Take(1),
            result => { Assert.Equal("My name is Usman", result.Item); Assert.InRange(result.Similarity, 0, 1.01f); }
            );
    }

    [TestMethod]
    public void CanFindClosestMatches_Instance()
    {
        using var embedder = new LocalEmbedder();
        string[] candidates = ["Tea", "Latte", "Coffee", "Cherryade", "David Hasselhoff"];
        var embeddedCandidates = embedder.EmbedRange(candidates);

        var closest = embedder.FindClosest(new() { SearchText = "beans", MaxResults = 2 }, embeddedCandidates);
        var closestWithScore = embedder.FindClosestWithScore(new() { SearchText = "beans", MaxResults = 2 }, embeddedCandidates);

        Assert.Equals(new[] { "Coffee", "Latte" }, closest.Take(2).ToList());
        Assert.Collection(closestWithScore.Take(2),
            result => { Assert.Equal("Coffee", result.Item); Assert.InRange(result.Similarity, 0, 1.01f); },
            result => { Assert.Equal("Latte", result.Item); Assert.InRange(result.Similarity, 0, 1.01f); });
    }

    [TestMethod]
    public void CanSpecifySimilarityThreshold_Static()
    {
        using var embedder = new LocalEmbedder();
        string[] candidates = ["Tea", "Latte", "Coffee", "Cherryade", "David Hasselhoff"];
        var embeddedCandidates = embedder.EmbedRange(candidates);

        var closest = LocalEmbedder.FindClosest(embedder.Embed("coffee"), embeddedCandidates, 2, minSimilarity: 0.95f);
        var closestWithScore = LocalEmbedder.FindClosestWithScore(embedder.Embed("coffee"), embeddedCandidates, 2, minSimilarity: 0.95f);

        Assert.Equals(new[] { "Coffee" }, closest.ToList());
        Assert.Collection(closestWithScore,
            result => { Assert.Equal("Coffee", result.Item); Assert.InRange(result.Similarity, 0.95f, 1.01f); });
    }
}
