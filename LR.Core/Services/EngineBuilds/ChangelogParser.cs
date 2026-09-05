using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Turns a GitHub compare response into changelog rows, pulling the pull-request number out of
/// each commit summary's trailing <c>(#1234)</c> (the convention llama.cpp merges use).
/// </summary>
public static class ChangelogParser
{
    private static readonly Regex PrRef = new(@"\(#(\d+)\)\s*$", RegexOptions.Compiled);

    public static List<EngineBuildChangelogEntry> ToEntries(GitHubCompareResult compare, string repo)
    {
        var entries = new List<EngineBuildChangelogEntry>(compare.Commits.Count);
        // GitHub returns compare commits oldest-first; show newest-first.
        foreach (var c in Enumerable.Reverse(compare.Commits))
        {
            var summary = c.Commit.Summary;
            var entry = new EngineBuildChangelogEntry
            {
                Sha = c.Sha,
                Summary = summary,
                Author = c.Author?.Login ?? c.Commit.Author?.Name,
                Date = c.Commit.Author?.Date,
                CommitUrl = c.HtmlUrl,
            };

            var m = PrRef.Match(summary);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pr))
            {
                entry.PullRequestNumber = pr;
                entry.PullRequestUrl = $"https://github.com/{repo}/pull/{pr}";
            }

            entries.Add(entry);
        }
        return entries;
    }
}
