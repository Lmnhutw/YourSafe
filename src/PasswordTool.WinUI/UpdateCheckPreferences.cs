using System.Text.Json;

namespace PasswordTool_WinUI;

internal sealed record UpdateCheckPreferences(bool DailyCheckEnabled = false, DateTimeOffset? LastAttemptUtc = null)
{
    public bool IsDailyCheckDue(DateTimeOffset now) =>
        DailyCheckEnabled && (LastAttemptUtc is not { } last || now < last || now - last >= TimeSpan.FromDays(1));

    public static UpdateCheckPreferences Load(string path)
    {
        try { return JsonSerializer.Deserialize<UpdateCheckPreferences>(File.ReadAllText(path)) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
