using System.Text;

namespace AgentFox.ChannelTests.Evals;

/// <summary>One check and what it means. The local equivalent of a FunctionEvaluator.</summary>
/// <param name="Name">Stable identifier. It is what a CI failure names, so keep it constant.</param>
/// <param name="Check">
/// Returns null when the case passes, or the reason it failed. A reason rather than a bool because
/// "tool_descriptions_present failed" is not actionable and "shell: description is empty" is.
/// </param>
public sealed record EvalCase(string Name, Func<string?> Check);

/// <summary>
/// A deliberately tiny local eval runner.
///
/// <para>
/// <b>Why this is not a package.</b> The hosted half of the Agent Framework's evaluation story —
/// model-graded relevance and coherence — is what the dependency buys, and it is the half not
/// wanted here: there is no ground truth to grade against, it costs a model call per case, and it
/// goes red on model drift rather than on regression. A signal nobody trusts gets muted, and a
/// muted gate is worse than no gate. What remains is a predicate over a value, which is this file.
/// </para>
///
/// <para>
/// <b>Why these subjects.</b> Everything evaluated here is a pure function of code — the schema a
/// tool advertises, the sections a prompt assembles, the decision a gate reaches. Those are the
/// things that regress silently when somebody reworks a description or reorders a check, because
/// nothing compiles differently and no existing test asserts on them. Anything requiring a live
/// model is out of scope by construction, not by omission.
/// </para>
///
/// <para>
/// <b>Every failure is reported, never just the first.</b> A suite that stops at the first failing
/// case invites fixing one thing and rerunning four times. It also misrepresents the damage: three
/// tools losing their descriptions in one edit is a different problem from one, and an assertion
/// that stops at the first cannot tell them apart.
/// </para>
/// </summary>
public static class EvalSuite
{
    /// <summary>
    /// Runs every case and fails once with all the reasons, or returns quietly.
    /// </summary>
    public static void Run(string suiteName, IEnumerable<EvalCase> cases)
    {
        var failures = new List<string>();
        var total = 0;

        foreach (var evalCase in cases)
        {
            total++;
            string? reason;
            try
            {
                reason = evalCase.Check();
            }
            catch (Exception ex)
            {
                // A throwing check is a failing check, not a broken run: the point is to report
                // every case, and one that blows up must not take the other reports with it.
                reason = $"threw {ex.GetType().Name}: {ex.Message}";
            }

            if (reason is not null)
                failures.Add($"  ✗ {evalCase.Name}: {reason}");
        }

        if (failures.Count == 0)
            return;

        var report = new StringBuilder()
            .AppendLine($"{suiteName}: {failures.Count} of {total} eval case(s) failed.")
            .AppendLine(string.Join(Environment.NewLine, failures));

        Assert.Fail(report.ToString());
    }

    /// <summary>Shorthand for a boolean predicate with a fixed failure reason.</summary>
    public static EvalCase Case(string name, Func<bool> predicate, string failureReason) =>
        new(name, () => predicate() ? null : failureReason);
}
