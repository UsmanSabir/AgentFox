using AgentFox.Agents;

namespace AgentFox.ChannelTests.Evals;

/// <summary>
/// How the system prompt is assembled from its contributors, asserted against the REAL
/// <see cref="PromptContributorRegistry"/> and its <c>CollectFragments</c> — the same method
/// <c>DynamicAgentMiddleware.InjectPromptAddons</c> calls before every LLM request. Restating the
/// rules in the test would have tested the restatement.
///
/// <para>
/// Worth pinning because the rules are invisible at every call site and every one of them fails
/// silently: a contributor returning null must vanish rather than leave a blank section, a
/// re-registered id must replace rather than duplicate (a duplicated section tells the model the
/// same thing twice, which reads as emphasis), section order must be stable across turns or the
/// provider's cached prompt prefix is invalidated on every turn, and a throwing contributor must
/// cost its own section rather than the whole request.
/// </para>
///
/// <para>
/// Writing this suite found the last two of those to be false, and both were fixed in
/// <see cref="PromptContributorRegistry"/> rather than written down as expected behaviour.
/// </para>
/// </summary>
[TestClass]
public sealed class SystemPromptEvals
{
    private sealed class StubContributor(string id, string? fragment) : IPromptContributor
    {
        public string ContributorId => id;
        public string? GetFragment() => fragment;
    }

    private sealed class ThrowingContributor(string id) : IPromptContributor
    {
        public string ContributorId => id;
        public string? GetFragment() => throw new InvalidOperationException("contributor blew up");
    }

    private static PromptContributorRegistry Registry(params IPromptContributor[] contributors)
    {
        var registry = new PromptContributorRegistry();
        foreach (var contributor in contributors)
            registry.Add(contributor);
        return registry;
    }

    [TestMethod]
    public void PromptAssemblyRules()
    {
        EvalSuite.Run("system-prompt", [
            new EvalCase("sections_appear_in_registration_order", () =>
            {
                var fragments = Registry(
                    new StubContributor("first", "A"),
                    new StubContributor("second", "B"),
                    new StubContributor("third", "C")).CollectFragments();

                return fragments.SequenceEqual(["A", "B", "C"])
                    ? null
                    : $"order was [{string.Join(", ", fragments)}] — an unstable section order "
                      + "invalidates the provider's cached prompt prefix on every turn";
            }),

            new EvalCase("re_registering_an_id_replaces_rather_than_duplicates", () =>
            {
                var registry = Registry(
                    new StubContributor("skills", "old"),
                    new StubContributor("skills", "new"));

                var fragments = registry.CollectFragments();
                if (fragments.Count != 1)
                    return $"got {fragments.Count} sections for one id ([{string.Join(", ", fragments)}]) — "
                           + "a duplicated section tells the model the same thing twice";

                return fragments[0] == "new"
                    ? null
                    : "the OLD contributor survived; a re-registration must win";
            }),

            new EvalCase("re_registering_keeps_the_original_position", () =>
            {
                var registry = Registry(
                    new StubContributor("a", "A"),
                    new StubContributor("b", "B"),
                    new StubContributor("c", "C"));
                registry.Add(new StubContributor("a", "A2"));

                var fragments = registry.CollectFragments();
                return fragments.SequenceEqual(["A2", "B", "C"])
                    ? null
                    : $"order became [{string.Join(", ", fragments)}] — a re-registration moved the "
                      + "section, which churns the cached prompt prefix for no reason";
            }),

            new EvalCase("a_removed_contributor_contributes_nothing", () =>
            {
                var registry = Registry(new StubContributor("temp", "X"), new StubContributor("keep", "K"));
                registry.Remove("temp");

                var fragments = registry.CollectFragments();
                return fragments.SequenceEqual(["K"])
                    ? null
                    : $"after removal the sections were [{string.Join(", ", fragments)}]";
            }),

            new EvalCase("removing_an_unknown_id_is_a_no_op", () =>
            {
                var registry = Registry(new StubContributor("keep", "K"));
                registry.Remove("never-existed");
                registry.Remove("keep");
                registry.Remove("keep");

                return registry.CollectFragments().Count == 0
                    ? null
                    : "repeated or unknown removals must not throw or resurrect anything";
            }),

            new EvalCase("a_null_fragment_contributes_no_section", () =>
            {
                // The contract from IPromptContributor: null means "nothing to contribute".
                var fragments = Registry(
                    new StubContributor("quiet", null),
                    new StubContributor("loud", "SECTION")).CollectFragments();

                return fragments.SequenceEqual(["SECTION"])
                    ? null
                    : $"sections were [{string.Join(", ", fragments)}] — a null fragment left a mark";
            }),

            new EvalCase("a_whitespace_only_fragment_contributes_no_section", () =>
            {
                var fragments = Registry(
                    new StubContributor("blank", "   \n  "),
                    new StubContributor("loud", "SECTION")).CollectFragments();

                return fragments.SequenceEqual(["SECTION"])
                    ? null
                    : $"sections were [{string.Join(", ", fragments)}] — whitespace is not content";
            }),

            new EvalCase("a_throwing_contributor_costs_only_its_own_section", () =>
            {
                // A prompt addon is decoration. Before CollectFragments existed this propagated
                // out of the middleware and failed the entire LLM call — a total failure traded
                // for a small degradation, in the wrong direction.
                var registry = Registry(
                    new ThrowingContributor("bad"),
                    new StubContributor("good", "SECTION"));

                try
                {
                    var fragments = registry.CollectFragments();
                    return fragments.SequenceEqual(["SECTION"])
                        ? null
                        : $"sections were [{string.Join(", ", fragments)}]";
                }
                catch (Exception ex)
                {
                    return $"collection propagated {ex.GetType().Name} — one bad contributor must "
                           + "not cost the whole turn";
                }
            }),

            new EvalCase("a_throwing_contributor_is_reported_not_swallowed", () =>
            {
                var reported = new List<string>();
                Registry(new ThrowingContributor("bad"), new StubContributor("good", "S"))
                    .CollectFragments((contributor, _) => reported.Add(contributor.ContributorId));

                return reported.SequenceEqual(["bad"])
                    ? null
                    : $"reported [{string.Join(", ", reported)}] — tolerating a failure must not "
                      + "mean hiding it";
            }),
        ]);
    }
}
