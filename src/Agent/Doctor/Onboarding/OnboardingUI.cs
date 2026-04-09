namespace AgentFox.Doctor.Onboarding;

using Spectre.Console;

/// <summary>
/// All Spectre.Console interaction for the onboarding wizard lives here.
/// Wizard logic must never call AnsiConsole directly.
/// </summary>
public static class OnboardingUI
{
    // ── Layout ────────────────────────────────────────────────────────────────

    public static void PrintBanner()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold blue]AgentFox Setup[/]");
        AnsiConsole.MarkupLine("  [dim]Let's get you up and running.[/]");
        AnsiConsole.MarkupLine("  [blue]│[/]");
    }

    public static void PrintStepHeader(string name, string description)
    {
        AnsiConsole.MarkupLine($"  [blue]●[/] [bold]{Markup.Escape(name)}[/]");
        AnsiConsole.MarkupLine($"  [blue]│[/]   [dim]{Markup.Escape(description)}[/]");
        AnsiConsole.MarkupLine("  [blue]│[/]");
    }

    public static void PrintLine()
        => AnsiConsole.MarkupLine("  [blue]│[/]");

    public static void PrintInfo(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [dim]{Markup.Escape(message)}[/]");

    public static void PrintSuccess(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [green]✓[/] {Markup.Escape(message)}");

    public static void PrintWarning(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [yellow]⚠[/] {Markup.Escape(message)}");

    public static void PrintDone(string configPath)
    {
        AnsiConsole.MarkupLine("  [blue]│[/]");
        AnsiConsole.MarkupLine($"  [blue]◆[/] [bold green]Configuration saved![/]");
        AnsiConsole.MarkupLine($"  [blue]│[/]   [dim]{Markup.Escape(configPath)}[/]");
        AnsiConsole.MarkupLine("  [blue]│[/]   [dim]Restart AgentFox to apply the new settings.[/]");
        AnsiConsole.WriteLine();
    }

    // ── Prompts ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ask for text input. Returns the typed text, or <paramref name="defaultValue"/>
    /// if the user presses Enter without typing. Returns <c>null</c> when there is no
    /// default and the user gives an empty response.
    /// </summary>
    public static string? AskText(string question, string? defaultValue = null, bool secret = false)
    {
        var prompt = new TextPrompt<string>($"  [green]◇[/] {question}");

        if (defaultValue != null)
            prompt.DefaultValue(defaultValue);
        else
            prompt.AllowEmpty();

        if (secret)
            prompt.Secret();

        var result = AnsiConsole.Prompt(prompt).Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>Present a single-choice selection list.</summary>
    public static string Choose(string question, IEnumerable<string> options)
        => AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"  [green]◇[/] {question}")
                .PageSize(12)
                .HighlightStyle(Style.Parse("dodgerblue1"))
                .AddChoices(options));

    /// <summary>Yes / No confirmation prompt.</summary>
    public static bool Confirm(string question, bool defaultValue = true)
        => AnsiConsole.Prompt(
            new ConfirmationPrompt($"  [green]◇[/] {question}")
            { DefaultValue = defaultValue });

    // ── Progress ──────────────────────────────────────────────────────────────

    public static async Task RunWithSpinner(string label, Func<Task> action)
        => await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync($"  [dim]{Markup.Escape(label)}[/]", async _ => await action());
}
