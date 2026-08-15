using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ImageViewer.Services;

/// <summary>
/// Compares the running app's version against the latest GitHub release. Requires the repository
/// to be publicly readable - GitHub's release API returns 404 for anonymous requests to a private
/// repo, so this silently finds nothing if the repo is ever made private again.
/// </summary>
public static class UpdateChecker
{
    // Reads from the assembly version (set by <Version> in ImageViewer.csproj) rather than a
    // separate hardcoded literal, so this can't drift out of sync with the actual build version.
    public static readonly string CurrentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private const string LatestReleaseApiUrl = "https://api.github.com/repos/rolloll/VistaMia/releases/latest";
    private const string AssetName = "VistaMia-win-x64.zip";

    public sealed record UpdateInfo(string Version, string ReleaseUrl, string? AssetDownloadUrl);

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
            if (latest <= current) return null;

            string? assetUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() == AssetName)
                    {
                        assetUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            return new UpdateInfo(latestVersionText, releaseUrl, assetUrl);
        }
        catch
        {
            return null;
        }
    }
}
