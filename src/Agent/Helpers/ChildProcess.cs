using System.Diagnostics;

namespace AgentFox.Helpers;

/// <summary>
/// Shared spawn hygiene for every child process the agent starts.
///
/// <para>
/// With <c>UseShellExecute = false</c> and no stdin redirect, a child inherits the parent's console
/// input handle. Any child that reads stdin — a credential prompt, <c>npm init</c>, a package
/// manager's confirmation, an accidental REPL — then competes with the interactive prompt for the
/// user's keystrokes and blocks until something answers it, which nothing ever does. Redirecting
/// stdin and immediately closing it turns that class of hang into an EOF the child handles itself.
/// </para>
///
/// <para>
/// Killing matters just as much: <c>Process.Kill()</c> without the tree flag reaps only the
/// <c>cmd.exe</c> wrapper and orphans whatever it launched — and the orphan keeps the inherited
/// handles it was given. Every kill here is a tree kill.
/// </para>
/// </summary>
public static class ChildProcess
{
    /// <summary>
    /// Marks the child's stdin for redirection so it does not inherit the console.
    /// Call before <see cref="Process.Start()"/>; pair with <see cref="CloseStandardInput"/>.
    /// </summary>
    public static void DetachStandardInput(ProcessStartInfo startInfo)
        => startInfo.RedirectStandardInput = true;

    /// <summary>
    /// Closes the redirected stdin pipe so a child that reads it sees EOF immediately instead of
    /// blocking forever on input that is never coming.
    /// </summary>
    public static void CloseStandardInput(Process process)
    {
        try { process.StandardInput.Close(); }
        catch { /* already closed, or the child exited before we got here */ }
    }

    /// <summary>
    /// Kills the process and everything it spawned. Safe to call on an already-exited process.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* exited between the check and the kill, or access denied on a reparented child */ }
    }
}
