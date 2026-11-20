using System.Net;
using System.Text.Json;
using PasswordTool_WinUI;

namespace PasswordTool.Presentation.Tests;

public sealed class ReleaseUpdateCheckTests
{
    [Theory]
    [InlineData("v0.1.10", "0.1.10")]
    [InlineData("v0.1.9", null)]
    [InlineData("v0.1.8", null)]
    public async Task Compares_published_package_version_numerically(string tag, string? expected)
    {
        using var client = Client(Release(tag));

        var result = await ReleaseUpdateCheck.CheckAsync(client, new Version(0, 1, 9), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Newer?.ToString());
        if (result.Newer is { } newer)
            Assert.Equal("https://github.com/Lmnhutw/YourSafe/releases/tag/v0.1.10", ReleaseUpdateCheck.ReleasePage(newer).AbsoluteUri);
    }

    [Theory]
    [InlineData("v0.2.0", true, false, true)]
    [InlineData("v0.2.0", false, true, true)]
    [InlineData("v0.2.0-test", false, false, true)]
    [InlineData("v0.2.0", false, false, false)]
    public async Task Does_not_offer_unpublished_or_unavailable_packages(string tag, bool draft, bool prerelease, bool hasPackage)
    {
        using var client = Client(Release(tag, draft, prerelease, hasPackage));

        var result = await ReleaseUpdateCheck.CheckAsync(client, new Version(0, 1, 9), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Newer);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Bad_response_does_not_report_up_to_date(HttpStatusCode status)
    {
        using var client = Client("invalid JSON", status);

        var result = await ReleaseUpdateCheck.CheckAsync(client, new Version(0, 1, 9), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Newer);
    }

    [Fact]
    public void Daily_check_defaults_off_and_respects_the_saved_attempt()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "update-preferences.json");
        var now = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        try
        {
            Assert.False(UpdateCheckPreferences.Load(path).IsDailyCheckDue(now));
            Assert.True(new UpdateCheckPreferences(DailyCheckEnabled: true).IsDailyCheckDue(now));

            new UpdateCheckPreferences(DailyCheckEnabled: true, LastAttemptUtc: now).Save(path);
            var restored = UpdateCheckPreferences.Load(path);
            Assert.True(restored.DailyCheckEnabled);
            Assert.False(restored.IsDailyCheckDue(now.AddHours(23)));
            Assert.True(restored.IsDailyCheckDue(now.AddDays(1)));

            (restored with { DailyCheckEnabled = false }).Save(path);
            Assert.False(UpdateCheckPreferences.Load(path).IsDailyCheckDue(now.AddDays(2)));
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false, bool hasPackage = true) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            html_url = "https://example.invalid/",
            assets = hasPackage ? new[] { new { name = "YourSafe-setup.exe", state = "uploaded" } } : []
        });

    private static HttpClient Client(string body, HttpStatusCode status = HttpStatusCode.OK) => new(new ReleaseResponse(body, status));

    private sealed class ReleaseResponse(string body, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.github.com/repos/Lmnhutw/YourSafe/releases/latest", request.RequestUri?.AbsoluteUri);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
