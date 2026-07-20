namespace AgentFox.Doctor.Checks;

using AgentFox.Doctor;
using AgentFox.Skills;

public class SkillHealthCheck : IHealthCheckable
{
    private readonly SkillRegistry _skillRegistry;
    public string ComponentName => "Skills";

    public SkillHealthCheck(SkillRegistry skillRegistry) => _skillRegistry = skillRegistry;

    public async Task<IReadOnlyList<HealthCheckResult>> CheckHealthAsync(CancellationToken ct = default)
    {
        var results = new List<HealthCheckResult>();

        var skills = _skillRegistry.GetAll();

        if (!skills.Any())
        {
            results.Add(new HealthCheckResult(HealthStatus.Healthy, "Skills", "No skills registered"));
            return results;
        }

        foreach (var skill in skills)
            results.AddRange(await skill.CheckHealthAsync(ct));

        return results;
    }
}
