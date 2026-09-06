using System.Reflection;
using System.Text.Json;
using AgentFox.Agents;
using AgentFox.Plugins.Interfaces;
using AgentFox.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace AgentFox.ChannelTests.Evals;

/// <summary>
/// The tool surface as the MODEL receives it: name, description and JSON schema, taken from
/// <c>AgentBuilder.CreateGatewayTools()</c> rather than from the <see cref="ITool"/> definitions,
/// because the bridge in between is where a schema actually gets malformed.
///
/// <para>
/// This is the suite with the most regression value and the least existing coverage. A tool's
/// description is prose that nothing compiles and nothing asserts on, it is the single largest
/// influence on whether the model calls the tool correctly, and an edit that empties or breaks one
/// changes behaviour with no test going red. The schema has the same property with sharper
/// consequences — a missing "items" on an array is rejected by the provider at request time,
/// which surfaces as a broken conversation rather than as a build failure.
/// </para>
///
/// <para>
/// Tools are DISCOVERED by reflection, not listed. A list would be correct on the day it was
/// written and quietly incomplete after the next tool was added, which is the failure mode an eval
/// suite exists to avoid.
/// </para>
/// </summary>
[TestClass]
public sealed class ToolSchemaEvals
{
    [TestMethod]
    public void ToolSurfaceTheModelSees()
    {
        var tools = BuildGatewayTools();

        Assert.IsTrue(tools.Count >= 5,
            $"Only {tools.Count} tool(s) could be constructed — the discovery below has stopped "
            + "finding the built-in set, so this suite would pass by evaluating nothing.");

        var cases = new List<EvalCase>
        {
            EvalSuite.Case("tool_names_are_unique",
                () => tools.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == tools.Count,
                "Two tools share a name. The model addresses tools by name, so one of them is "
                + "unreachable and which one depends on registration order."),
        };

        foreach (var tool in tools)
        {
            var name = tool.Name;
            var description = tool.Description ?? string.Empty;
            var schema = tool.JsonSchema;

            cases.Add(EvalSuite.Case($"{name}/has_a_description",
                () => !string.IsNullOrWhiteSpace(description),
                "Description is empty. It is the only thing telling the model when to use this."));

            // Deliberately NOT a length-vs-name heuristic. The first version of this case was
            // `description.Length > name.Length + 8`, which failed three tools by one character
            // and would have been silenced by padding a word — a style opinion wearing an eval's
            // clothes. What is actually defensible is a floor (a handful of characters cannot
            // guide a model) and the degenerate case (the description IS the name).
            cases.Add(EvalSuite.Case($"{name}/description_is_not_merely_the_tool_name",
                () => !string.Equals(
                    Normalize(description), Normalize(name), StringComparison.OrdinalIgnoreCase),
                $"Description ('{description.Trim()}') restates the tool name and adds nothing."));

            cases.Add(EvalSuite.Case($"{name}/description_is_long_enough_to_guide",
                () => description.Trim().Length >= 15,
                $"Description ('{description.Trim()}') is too short to tell the model when to use this."));

            cases.Add(EvalSuite.Case($"{name}/name_is_a_valid_function_identifier",
                () => name.Length is > 0 and <= 64
                      && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'),
                $"'{name}' is not accepted as a function name by the providers — letters, digits, "
                + "underscore and hyphen only, at most 64 characters."));

            cases.Add(new EvalCase($"{name}/schema_is_a_json_object", () =>
                schema.ValueKind == JsonValueKind.Object
                    ? null
                    : $"schema is {schema.ValueKind}, not an object"));

            cases.Add(new EvalCase($"{name}/schema_declares_type_object", () =>
                schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "object"
                    ? null
                    : "top-level schema must be type 'object' — providers reject anything else"));

            // The concrete failure that motivated this suite: OpenAI rejects an array property
            // with no "items", and the rejection arrives at request time as a 400 on the whole
            // conversation, not as anything traceable to the tool that caused it.
            cases.Add(new EvalCase($"{name}/array_properties_declare_items", () =>
            {
                if (!TryGetProperties(schema, out var properties))
                    return null;

                var offenders = properties.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Object
                                && p.Value.TryGetProperty("type", out var t)
                                && t.ValueKind == JsonValueKind.String
                                && t.GetString() == "array"
                                && !p.Value.TryGetProperty("items", out _))
                    .Select(p => p.Name)
                    .ToList();

                return offenders.Count == 0
                    ? null
                    : $"array parameter(s) without 'items': {string.Join(", ", offenders)}";
            }));

            cases.Add(new EvalCase($"{name}/required_parameters_exist_in_properties", () =>
            {
                if (schema.ValueKind != JsonValueKind.Object
                    || !schema.TryGetProperty("required", out var required)
                    || required.ValueKind != JsonValueKind.Array)
                    return null;

                var declared = TryGetProperties(schema, out var props)
                    ? props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);

                var missing = required.EnumerateArray()
                    .Where(r => r.ValueKind == JsonValueKind.String && !declared.Contains(r.GetString()!))
                    .Select(r => r.GetString()!)
                    .ToList();

                return missing.Count == 0
                    ? null
                    : $"required names nothing in properties: {string.Join(", ", missing)}";
            }));

            cases.Add(new EvalCase($"{name}/every_parameter_is_described", () =>
            {
                if (!TryGetProperties(schema, out var properties))
                    return null;

                var undescribed = properties.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Object
                                && (!p.Value.TryGetProperty("description", out var d)
                                    || string.IsNullOrWhiteSpace(d.GetString())))
                    .Select(p => p.Name)
                    .ToList();

                return undescribed.Count == 0
                    ? null
                    : $"parameter(s) with no description: {string.Join(", ", undescribed)}";
            }));
        }

        EvalSuite.Run("tool-schema", cases);
    }

    /// <summary>Lowercase alphanumerics only, so "make_directory" and "Make Directory" compare equal.</summary>
    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryGetProperties(JsonElement schema, out JsonElement properties)
    {
        properties = default;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var found)
            || found.ValueKind != JsonValueKind.Object)
            return false;

        properties = found;
        return true;
    }

    /// <summary>
    /// Every built-in <see cref="ITool"/> in the host assembly, bridged exactly as the agent
    /// bridges it. Types needing dependencies this fixture cannot supply are skipped rather than
    /// failed — the suite is about the tools it CAN see, and a hard failure here would make adding
    /// a tool with an unusual dependency look like a schema defect. The count assertion above is
    /// what stops that leniency from turning into a suite that silently evaluates nothing.
    /// </summary>
    private static IReadOnlyList<AIFunction> BuildGatewayTools()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RestrictToWorkspace"] = "true" })
            .Build();
        var workspace = new WorkspaceManager(configuration);

        var registry = new ToolRegistry();
        foreach (var type in typeof(ToolRegistry).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ITool).IsAssignableFrom(type))
                continue;

            if (TryConstruct(type, workspace, configuration) is { } tool)
                registry.Register(tool);
        }

        return new AgentBuilder(registry).CreateGatewayTools().OfType<AIFunction>().ToList();
    }

    private static ITool? TryConstruct(Type type, WorkspaceManager workspace, IConfiguration configuration)
    {
        foreach (var ctor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
        {
            var arguments = new List<object?>();
            var usable = true;

            foreach (var parameter in ctor.GetParameters())
            {
                if (parameter.ParameterType == typeof(WorkspaceManager)) arguments.Add(workspace);
                else if (typeof(IConfiguration).IsAssignableFrom(parameter.ParameterType)) arguments.Add(configuration);
                else if (parameter.HasDefaultValue) arguments.Add(parameter.DefaultValue);
                else if (parameter.ParameterType == typeof(int)) arguments.Add(30);
                else { usable = false; break; }
            }

            if (!usable) continue;

            try { return (ITool?)ctor.Invoke([.. arguments]); }
            catch (TargetInvocationException) { /* refuses these arguments; not evaluable here */ }
            catch (Exception) { /* same */ }
        }

        return null;
    }
}
