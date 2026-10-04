using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PasswordTool_WinUI;

public enum AppearanceTheme { System, Light, Dark }

public sealed record AppearanceSettings
{
    public const string DefaultAccent = "#204BDB";
    public AppearanceTheme Theme { get; init; }
    public bool IsVerticalTabs { get; init; }
    public string? AllTabName { get; init; }
    public string? UngroupedTabName { get; init; }
    public string? AllTabColor { get; init; }
    public string? UngroupedTabColor { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static AppearanceSettings Load(string path)
    {
        try { return (JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(path), JsonOptions) ?? new()).Validated(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(); // Appearance preferences never prevent opening the app or its vault.
        }
    }

    public void Save(string path)
    {
        var normalized = Validated();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    public AppearanceSettings Validated()
    {
        if (!Enum.IsDefined(Theme)) throw new ArgumentException("Choose a supported appearance option.");
        return this with
        {
            AllTabName = NormalizeTabName(AllTabName),
            UngroupedTabName = NormalizeTabName(UngroupedTabName),
            AllTabColor = string.IsNullOrWhiteSpace(AllTabColor) ? null : NormalizeRgb(AllTabColor),
            UngroupedTabColor = string.IsNullOrWhiteSpace(UngroupedTabColor) ? null : NormalizeRgb(UngroupedTabColor)
        };
    }

    public static string? NormalizeTabName(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 100) throw new ArgumentException("Tab name cannot exceed 100 characters.", nameof(value));
        return value;
    }

    public static string NormalizeRgb(string? value)
    {
        value = value?.Trim();
        if (value is not { Length: 7 } || value[0] != '#' || value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new ArgumentException("Color must use #RRGGBB format.", nameof(value));
        return value.ToUpperInvariant();
    }

    public static string TextColorFor(string background) => Luminance(background) > 0.179128784747792 ? "#000000" : "#FFFFFF";

    public static double ContrastRatio(string first, string second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    public static string ColorForSurface(string accent, bool dark, double minimumRatio = 4.5)
    {
        accent = NormalizeRgb(accent);
        var surface = dark ? "#2D2D2D" : "#EEF2F6";
        var target = dark ? "#FFFFFF" : "#000000";
        for (var step = 0; step <= 100; step++)
        {
            var candidate = Blend(accent, target, step / 100d);
            if (ContrastRatio(candidate, surface) >= minimumRatio) return candidate;
        }
        return target;
    }

    public static string Blend(string first, string second, double amount)
    {
        first = NormalizeRgb(first);
        second = NormalizeRgb(second);
        amount = Math.Clamp(amount, 0, 1);
        var channels = new byte[3];
        for (var index = 0; index < channels.Length; index++)
        {
            var offset = 1 + index * 2;
            var a = byte.Parse(first.AsSpan(offset, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            var b = byte.Parse(second.AsSpan(offset, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            channels[index] = (byte)Math.Round(a + (b - a) * amount);
        }
        return $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
    }

    private static double Luminance(string value)
    {
        value = NormalizeRgb(value);
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var r = byte.Parse(value.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var g = byte.Parse(value.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var b = byte.Parse(value.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }
}
