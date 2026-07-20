using AgentFox.Plugins.Research;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ResearchReferenceScopeTests
{
    [TestMethod]
    public void Current_IsNull_OutsideAnyScope()
    {
        Assert.IsNull(ResearchReferenceScope.Current);
    }

    [TestMethod]
    public void Add_DedupesByNormalizedUrl_FirstWins()
    {
        using (ResearchReferenceScope.Begin())
        {
            ResearchReferenceScope.Current!.Add("https://Example.com/a/", "First", "SrcA");
            ResearchReferenceScope.Current!.Add("https://example.com/a", "Second", "SrcB");

            var snap = ResearchReferenceScope.Current!.Snapshot();
            Assert.AreEqual(1, snap.Count);
            Assert.AreEqual("First", snap[0].Title);
            Assert.AreEqual("SrcA", snap[0].Source);
        }
    }

    [TestMethod]
    public void Add_SkipsMalformedAndNonHttpUrls()
    {
        using (ResearchReferenceScope.Begin())
        {
            ResearchReferenceScope.Current!.Add(null);
            ResearchReferenceScope.Current!.Add("   ");
            ResearchReferenceScope.Current!.Add("not a url");
            ResearchReferenceScope.Current!.Add("ftp://example.com/x");
            ResearchReferenceScope.Current!.Add("javascript:alert(1)");
            ResearchReferenceScope.Current!.Add("https://ok.example.com/y");

            var snap = ResearchReferenceScope.Current!.Snapshot();
            Assert.AreEqual(1, snap.Count);
            Assert.AreEqual("https://ok.example.com/y", snap[0].Url);
        }
    }

    [TestMethod]
    public void Begin_Nested_RestoresPreviousScopeOnDispose()
    {
        using (ResearchReferenceScope.Begin())
        {
            var outer = ResearchReferenceScope.Current!;
            outer.Add("https://example.com/outer");

            using (ResearchReferenceScope.Begin())
            {
                Assert.AreNotSame(outer, ResearchReferenceScope.Current);
                ResearchReferenceScope.Current!.Add("https://example.com/inner");
                Assert.AreEqual(1, ResearchReferenceScope.Current!.Snapshot().Count);
            }

            Assert.AreSame(outer, ResearchReferenceScope.Current);
            Assert.AreEqual(1, ResearchReferenceScope.Current!.Snapshot().Count);
        }
        Assert.IsNull(ResearchReferenceScope.Current);
    }

    [TestMethod]
    public async Task Current_PropagatesAcrossAwait()
    {
        using (ResearchReferenceScope.Begin())
        {
            await Task.Yield();
            await NestedAddAsync();
            Assert.AreEqual(1, ResearchReferenceScope.Current!.Snapshot().Count);
        }

        static async Task NestedAddAsync()
        {
            await Task.Delay(1);
            ResearchReferenceScope.Current!.Add("https://example.com/deep");
        }
    }
}
