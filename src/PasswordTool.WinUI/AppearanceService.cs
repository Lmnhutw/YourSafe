using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace PasswordTool_WinUI;

internal sealed class AppearanceService : IDisposable
{
    private readonly UISettings uiSettings = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly ResourceDictionary accents = new();
    private readonly HashSet<FrameworkElement> dialogRoots = [];
    private readonly string settingsPath;
    private FrameworkElement? root;
    private bool applying;
    private bool monitoring;

    public AppearanceSettings Settings { get; private set; }
    public bool IsHighContrast => accessibility.HighContrast;
    public event EventHandler? Changed;

    public AppearanceService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PasswordTool");
#if DEBUG
        var testDirectory = Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(testDirectory)) directory = testDirectory;
#endif
        settingsPath = Path.Combine(directory, "appearance.json");
        Settings = AppearanceSettings.Load(settingsPath);
        accents.ThemeDictionaries["Light"] = new ResourceDictionary();
        accents.ThemeDictionaries["Dark"] = new ResourceDictionary();
        accents.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
        WritePalette((ResourceDictionary)accents.ThemeDictionaries["Light"], Palette(dark: false));
        WritePalette((ResourceDictionary)accents.ThemeDictionaries["Dark"], Palette(dark: true));
        Application.Current.Resources.MergedDictionaries.Add(accents);
        uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
    }

    public void Attach(FrameworkElement element)
    {
        if (root is not null)
        {
            root.ActualThemeChanged -= Root_ActualThemeChanged;
            root.Loaded -= Root_Loaded;
        }
        root = element;
        root.ActualThemeChanged += Root_ActualThemeChanged;
        root.Loaded += Root_Loaded;
        Refresh();
    }

    public void Save(AppearanceSettings settings)
    {
        settings = settings.Validated();
        settings.Save(settingsPath);
        Settings = settings;
        Refresh();
    }

    public void ApplyTo(FrameworkElement element)
    {
        element.RequestedTheme = ResolveTheme();
        if (dialogRoots.Add(element)) element.Unloaded += Dialog_Unloaded;
    }

    public void Release(FrameworkElement element)
    {
        element.Unloaded -= Dialog_Unloaded;
        dialogRoots.Remove(element);
    }

    private ElementTheme ResolveTheme() => Settings.Theme switch
    {
        AppearanceTheme.Light => ElementTheme.Light,
        AppearanceTheme.Dark => ElementTheme.Dark,
        _ => uiSettings.GetColorValue(UIColorType.Background).R < 128 ? ElementTheme.Dark : ElementTheme.Light
    };

    private void Refresh()
    {
        if (root is null || applying) return;
        applying = true;
        try
        {
            ApplyTheme(root, ResolveTheme());
            foreach (var dialog in dialogRoots) dialog.RequestedTheme = root.RequestedTheme;
        }
        finally { applying = false; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyTheme(FrameworkElement element, ElementTheme theme)
    {
        element.RequestedTheme = theme;
        // Frame navigation is a separate resource boundary; theme its current page explicitly.
        if (element is Frame { Content: FrameworkElement page }) page.RequestedTheme = theme;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (VisualTreeHelper.GetChild(element, index) is FrameworkElement child && (child is Frame || element is Panel))
                ApplyTheme(child, theme);
    }

    private static Dictionary<string, Color> Palette(bool dark)
    {
        const string fill = AppearanceSettings.DefaultAccent;
        var text = AppearanceSettings.TextColorFor(fill);
        var shadeTarget = text == "#FFFFFF" ? "#000000" : "#FFFFFF";
        var hover = AppearanceSettings.Blend(fill, shadeTarget, 0.08);
        var pressed = AppearanceSettings.Blend(fill, shadeTarget, 0.16);
        var link = AppearanceSettings.ColorForSurface(fill, dark);
        var values = new Dictionary<string, Color>
        {
            ["SystemAccentColor"] = Rgb(fill),
            ["SystemAccentColorLight1"] = Rgb(AppearanceSettings.Blend(fill, "#FFFFFF", 0.25)),
            ["SystemAccentColorLight2"] = Rgb(AppearanceSettings.Blend(fill, "#FFFFFF", 0.45)),
            ["SystemAccentColorLight3"] = Rgb(AppearanceSettings.Blend(fill, "#FFFFFF", 0.65)),
            ["SystemAccentColorDark1"] = Rgb(AppearanceSettings.Blend(fill, "#000000", 0.14)),
            ["SystemAccentColorDark2"] = Rgb(AppearanceSettings.Blend(fill, "#000000", 0.30)),
            ["SystemAccentColorDark3"] = Rgb(AppearanceSettings.Blend(fill, "#000000", 0.46))
        };
        foreach (var name in new[] { "AccentFillColorDefault", "AccentFillColorSelectedTextBackground", "AccentAAFillColorDefault" }) AddBrush(values, name, fill);
        foreach (var name in new[] { "AccentFillColorSecondary", "AccentAAFillColorSecondary" }) AddBrush(values, name, hover);
        foreach (var name in new[] { "AccentFillColorTertiary", "AccentAAFillColorTertiary" }) AddBrush(values, name, pressed);
        foreach (var name in new[] { "AccentTextFillColorPrimary", "AccentTextFillColorSecondary", "AccentTextFillColorTertiary" }) AddBrush(values, name, link);
        foreach (var name in new[] { "TextOnAccentFillColorPrimary", "TextOnAccentFillColorSecondary", "TextOnAccentFillColorDefault", "TextOnAccentFillColorSelectedText", "TextOnAccentAAFillColorPrimary", "TextOnAccentAAFillColorSecondary" }) AddBrush(values, name, text);
        foreach (var name in new[] { "SystemControlBackgroundAccent", "SystemControlForegroundAccent", "SystemControlHighlightAccent", "SystemControlHighlightAltAccent", "SystemControlRevealFocusVisual" }) values[name + "Brush"] = Rgb(fill);
        values["SystemControlHyperlinkTextBrush"] = Rgb(link);
        values["SystemFillColorAttentionBrush"] = Rgb(fill);
        values["AccentButtonBackground"] = Rgb(fill);
        values["AccentButtonBackgroundPointerOver"] = Rgb(hover);
        values["AccentButtonBackgroundPressed"] = Rgb(pressed);
        foreach (var name in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" }) values[name] = Rgb(text);
        values["NavigationViewSelectionIndicatorForeground"] = Rgb(AppearanceSettings.ColorForSurface(fill, dark, 3));
        return values;
    }

    private static void AddBrush(Dictionary<string, Color> values, string name, string hex)
    {
        values[name] = Rgb(hex);
        values[name + "Brush"] = values[name];
    }

    private static void WritePalette(ResourceDictionary dictionary, Dictionary<string, Color> palette)
    {
        // Own every override; dictionary lookup can otherwise return a shared native brush.
        foreach (var (key, color) in palette)
        {
            if (key.EndsWith("Brush", StringComparison.Ordinal) || key.StartsWith("AccentButton", StringComparison.Ordinal) || key == "NavigationViewSelectionIndicatorForeground")
                dictionary[key] = new SolidColorBrush(color);
            else dictionary[key] = color;
        }
    }

    private static Color Rgb(string value) => Color.FromArgb(255,
        byte.Parse(value.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
        byte.Parse(value.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture),
        byte.Parse(value.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));

    private void UiSettings_ColorValuesChanged(UISettings sender, object args) => root?.DispatcherQueue.TryEnqueue(Refresh);
    private void Root_Loaded(object sender, RoutedEventArgs args) => root?.DispatcherQueue.TryEnqueue(() =>
    {
        if (root is null || monitoring) return;
        // Desktop notifications work without the UWP CoreWindow required by HighContrastChanged.
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        monitoring = true;
    });
    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => root?.DispatcherQueue.TryEnqueue(Refresh);
    private void Root_ActualThemeChanged(FrameworkElement sender, object args) => Refresh();
    private void Dialog_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement element) Release(element);
    }

    public void Dispose()
    {
        uiSettings.ColorValuesChanged -= UiSettings_ColorValuesChanged;
        if (monitoring) SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        if (root is not null)
        {
            root.ActualThemeChanged -= Root_ActualThemeChanged;
            root.Loaded -= Root_Loaded;
        }
        foreach (var dialog in dialogRoots) dialog.Unloaded -= Dialog_Unloaded;
        dialogRoots.Clear();
        root = null;
    }
}
