using System.Net.Http;
using System.Text.Json;

namespace ImageViewer.Services;

/// <summary>
/// Compares the running app's version against the latest GitHub release. Requires the repository
/// to be publicly readable - GitHub's release API returns 404 for anonymous requests to a private
/// repo, so this silently finds nothing if the repo is ever made private again.
/// </summary>
public static class UpdateChecker
{
    public const string CurrentVersion = "1.1.0";

    private const string LatestReleaseApiUrl = "https://api.github.com/repos/rolloll/VistaMia/releases/latest";

    public sealed record UpdateInfo(string Version, string ReleaseUrl);

    /// <summary>Returns update info if a newer release is available, or null if up to date /
    /// the check couldn't be completed (offline, API hiccup, etc.) - this is a best-effort,
    /// non-critical background check and must never throw or block startup.</summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VistaMia-UpdateChecker");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await client.GetAsync(LatestReleaseApiUrl);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            var releaseUrl = doc.RootElement.GetProperty("html_url").GetString();
            if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(releaseUrl)) return null;

            var latestVersionText = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(latestVersionText, out var latest)) return null;
            if (!Version.TryParse(CurrentVersion, out var current)) return null;

            return latest > current ? new UpdateInfo(latestVersionText, releaseUrl) : null;
        }
        catch
        {
            return null;
        }
    }
}
