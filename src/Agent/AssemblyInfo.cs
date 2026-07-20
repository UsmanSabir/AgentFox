using System.Runtime.CompilerServices;

// Grants the test project access to `internal` members needed for unit testing
// (e.g. MarkdownSessionStore.AppendForTest), without relaxing those members to public.
[assembly: InternalsVisibleTo("AgentFox.ChannelTests")]
