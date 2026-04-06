namespace AgentFox.Doctor;

using Spectre.Console;

/// <summary>
/// All Spectre.Console interaction for the doctor command lives here.
/// Health check logic must never call AnsiConsole directly.
/// </summary>
public static class DoctorUI
{
    // ── Section headers ──────────────────────────────────────────────

    public static void PrintBanner()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold blue]AgentFox Doctor[/]");
        AnsiConsole.MarkupLine("  [dim]System health check[/]");
        AnsiConsole.MarkupLine("  [blue]│[/]");
    }

    public static void PrintComponentHeader(string name)
    {
        AnsiConsole.MarkupLine($"  [blue]●[/] [bold]{Markup.Escape(name)}[/]");
        AnsiConsole.MarkupLine("  [blue]│[/]");
    }

    public static void PrintSummary(int healthy, int warnings, int critical)
    {
        AnsiConsole.MarkupLine("  [blue]│[/]");
        AnsiConsole.MarkupLine($"  [blue]◆[/] Summary: " +
            $"[green]{healthy} healthy[/]  " +
            $"[yellow]{warnings} warnings[/]  " +
            $"[red]{critical} critical[/]");
        AnsiConsole.WriteLine();
    }

    // ── Per-check result lines ────────────────────────────────────────

    public static void ReportHealthy(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [green]✓[/] {Markup.Escape(message)}");

    public static void ReportWarning(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [yellow]⚠[/] {Markup.Escape(message)}");

    public static void ReportCritical(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [red]✗[/] {Markup.Escape(message)}");

    public static void ReportFixAvailable(string fixDescription)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [dim cyan]◇ Fix available: {Markup.Escape(fixDescription)}[/]");

    public static void ReportFixApplied(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [green]⚡ Fixed: {Markup.Escape(message)}[/]");

    public static void ReportFixFailed(string message)
        => AnsiConsole.MarkupLine($"  [blue]│[/]   [red]⚡ Fix failed: {Markup.Escape(message)}[/]");

    public static void ReportResult(HealthCheckResult result)
    {
        switch (result.Status)
        {
            case HealthStatus.Healthy:  ReportHealthy(result.Message);  break;
            case HealthStatus.Warning:  ReportWarning(result.Message);  break;
            case HealthStatus.Critical: ReportCritical(result.Message); break;
        }
        if (result.CanAutoFix && result.FixDescription is not null)
            ReportFixAvailable(result.FixDescription);
    }

    // ── Prompts ───────────────────────────────────────────────────────

    /// <summary>Safe confirmation — used for reversible fixes.</summary>
    public static bool Confirm(string question, bool defaultValue = true)
        => AnsiConsole.Prompt(
            new ConfirmationPrompt($"  [green]◇[/] {question}")
            { DefaultValue = defaultValue });

    /// <summary>Destructive confirmation — red styling + explicit warning. Default is NO.</summary>
    public static bool ConfirmDestructive(string action, string consequence)
    {
        AnsiConsole.MarkupLine($"  [red]⚠  WARNING:[/] {Markup.Escape(consequence)}");
        return AnsiConsole.Prompt(
            new ConfirmationPrompt($"  [red]◇[/] {Markup.Escape(action)} — are you sure?")
            { DefaultValue = false });
    }

    /// <summary>Multi-choice fix selection when multiple fix strategies exist.</summary>
    public static string ChooseFix(string question, IEnumerable<string> options)
        => AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"  [green]◇[/] {question}")
                .AddChoices(options));

    // ── Progress ──────────────────────────────────────────────────────

    public static async Task RunWithSpinner(string label, Func<Task> action)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync($"  [dim]{Markup.Escape(label)}[/]", async _ => await action());
    }
}
