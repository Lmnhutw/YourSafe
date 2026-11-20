using System.Net.Http;
using System.Text.Json;

namespace PasswordTool_WinUI;

internal static class ReleaseUpdateCheck
{
    private const string Repository = "Lmnhutw/YourSafe";
    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 512 * 1024,
        DefaultRequestHeaders =
        {
            { "User-Agent", "YourSafe-UpdateCheck" },
            { "Accept", "application/vnd.github+json" }
        }
    };

    internal static readonly Version InstalledVersion = new(typeof(ReleaseUpdateCheck).Assembly.GetName().Version!.ToString(3));
    internal static Uri ReleasePage(Version version) => new($"https://github.com/{Repository}/releases/tag/v{version.ToString(3)}");

    internal static Task<(bool Succeeded, Version? Newer)> CheckAsync(CancellationToken cancellationToken) =>
        CheckAsync(Client, InstalledVersion, cancellationToken);

    internal static async Task<(bool Succeeded, Version? Newer)> CheckAsync(
        HttpClient client, Version installedVersion, CancellationToken cancellationToken)
    {
        try { return (true, await FindNewerVersionAsync(client, installedVersion, cancellationToken).ConfigureAwait(false)); }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or IOException or OperationCanceledException)
        {
            return (false, null);
        }
    }

    private static async Task<Version?> FindNewerVersionAsync(HttpClient client, Version installedVersion, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(LatestRelease, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False
            || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
            || tag.GetString() is not { Length: >= 6 and <= 34 } value || value[0] != 'v'
            || !Version.TryParse(value.AsSpan(1), out var latest) || latest.Build < 0 || latest.Revision != -1
            || value != $"v{latest.ToString(3)}")
        {
            throw new InvalidDataException("GitHub did not return a published YourSafe release version.");
        }

        if (latest <= installedVersion) return null;
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array
            || !assets.EnumerateArray().Any(asset => asset.ValueKind == JsonValueKind.Object
                && asset.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.ValueEquals("uploaded")
                && asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && (name.ValueEquals("YourSafe-setup.exe") || name.ValueEquals($"YourSafe-{latest}-win-x64.zip"))))
        {
            throw new InvalidDataException("The published YourSafe release has no Windows package.");
        }

        return latest;
    }
}
