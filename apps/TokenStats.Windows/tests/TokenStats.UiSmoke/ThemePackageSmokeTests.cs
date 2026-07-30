using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using TokenStats.App.Infrastructure;
using TokenStats.App.Services;

namespace TokenStats.UiSmoke;

internal static partial class Program
{
    private static void VerifyThemePackages(
        AppSettingsStore settings,
        ResourceDictionary resources)
    {
        var store = settings.ThemePackages;
        var settingsDirectory = Path.GetDirectoryName(settings.SettingsPath) ??
            throw new InvalidOperationException(
                "The smoke settings path has no parent directory.");
        var expectedDirectory = Path.GetFullPath(
            Path.Combine(settingsDirectory, "Themes"));
        if (!string.Equals(
                store.ThemesDirectory,
                expectedDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Theme YAML used '{store.ThemesDirectory}' instead of the " +
                $"application data folder '{expectedDirectory}'.");
        }

        var defaultPath = Path.Combine(
            expectedDirectory,
            $"{ThemePackageStore.DefaultThemeId}.yaml");
        if (!File.Exists(defaultPath) || new FileInfo(defaultPath).Length == 0)
        {
            throw new InvalidOperationException(
                "The default YAML theme was not seeded beside settings.json.");
        }

        if (!store.Themes.Any(theme =>
                theme.Id == ThemePackageStore.DefaultThemeId &&
                !string.IsNullOrWhiteSpace(theme.Name)))
        {
            throw new InvalidOperationException(
                "The seeded default YAML theme is not available.");
        }

        var validYaml = BuildSmokeThemeYaml();
        File.WriteAllText(
            Path.Combine(expectedDirectory, $"{SmokeThemeId}.yaml"),
            validYaml);

        var lightAccent = ThemeYamlColorLine(
            Array.FindIndex(
                ThemeColorRoles,
                role => role.YamlKey == "accent"),
            dark: false);
        var lightTooltip = ThemeYamlColorLine(
            Array.FindIndex(
                ThemeColorRoles,
                role => role.YamlKey == "tooltipBackground"),
            dark: false);
        var invalidThemes = new Dictionary<string, string>
        {
            ["unknown-root"] = validYaml +
                "layout: forbidden" + Environment.NewLine,
            ["duplicate-color"] = validYaml.Replace(
                lightAccent,
                lightAccent + Environment.NewLine + lightAccent,
                StringComparison.Ordinal),
            ["bad-hex"] = validYaml.Replace(
                lightAccent,
                "    accent: \"#GG1122\"",
                StringComparison.Ordinal),
            ["missing-color"] = validYaml.Replace(
                lightTooltip + Environment.NewLine,
                string.Empty,
                StringComparison.Ordinal),
        };
        foreach (var (id, yaml) in invalidThemes)
        {
            File.WriteAllText(
                Path.Combine(expectedDirectory, $"{id}.yaml"),
                yaml);
        }

        store.Reload();
        var customInfo = store.Themes.SingleOrDefault(
            theme => theme.Id == SmokeThemeId);
        if (customInfo is null || customInfo.Name != "Smoke Custom")
        {
            throw new InvalidOperationException(
                "A complete custom Light/Dark YAML theme was not loaded.");
        }

        foreach (var invalidId in invalidThemes.Keys)
        {
            if (store.Themes.Any(theme => theme.Id == invalidId))
            {
                throw new InvalidOperationException(
                    $"Invalid YAML theme '{invalidId}' was accepted.");
            }

            if (!store.Errors.Any(error => string.Equals(
                    Path.GetFileNameWithoutExtension(error.Source),
                    invalidId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Invalid YAML theme '{invalidId}' did not report an error.");
            }
        }

        var custom = store.Resolve(SmokeThemeId);
        VerifyThemePalette(
            resources,
            WindowsAppTheme.Light,
            SmokeThemeColors(dark: false),
            custom);
        VerifyThemePalette(
            resources,
            WindowsAppTheme.Dark,
            SmokeThemeColors(dark: true),
            custom);
        VerifyThemePalette(
            resources,
            WindowsAppTheme.HighContrast,
            ExpectedHighContrastColors(),
            custom);

        var fallback = store.Resolve("missing-theme");
        WindowsThemeService.Apply(
            resources,
            WindowsAppTheme.Light,
            fallback);
        if (WindowsThemeService.CurrentThemePackageId !=
                ThemePackageStore.DefaultThemeId ||
            resources["AccentBrush"] is not SolidColorBrush fallbackAccent ||
            fallbackAccent.Color != Rgb(0x08, 0x86, 0x6D))
        {
            throw new InvalidOperationException(
                "A missing theme id did not fall back to the default package.");
        }
    }

    private static string BuildSmokeThemeYaml()
    {
        var yaml = new StringBuilder();
        yaml.AppendLine("schemaVersion: 1");
        yaml.AppendLine("name: \"Smoke Custom\"");
        yaml.AppendLine("colors:");
        foreach (var (variant, dark) in new[]
                 {
                     ("light", false),
                     ("dark", true),
                 })
        {
            yaml.Append("  ").Append(variant).AppendLine(":");
            for (var index = 0; index < ThemeColorRoles.Length; index++)
            {
                yaml.AppendLine(ThemeYamlColorLine(index, dark));
            }
        }

        return yaml.ToString();
    }

    private static string ThemeYamlColorLine(int index, bool dark)
    {
        var color = SmokeThemeColor(index, dark);
        var value = index % 2 == 0
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        return $"    {ThemeColorRoles[index].YamlKey}: \"{value}\"";
    }

    private static IReadOnlyDictionary<string, Color> SmokeThemeColors(
        bool dark) =>
        ThemeColorRoles
            .Select((role, index) => new
            {
                role.ResourceKey,
                Color = SmokeThemeColor(index, dark),
            })
            .ToDictionary(
                entry => entry.ResourceKey,
                entry => entry.Color,
                StringComparer.Ordinal);

    private static Color SmokeThemeColor(int index, bool dark)
    {
        var offset = dark ? 0x70 : 0x20;
        var alpha = index % 2 == 0 ? 0xFF : 0x70 + index;
        return Color.FromArgb(
            (byte)alpha,
            (byte)(offset + index),
            (byte)(offset + 0x20 + index),
            (byte)(offset + 0x40 + index));
    }

    private static IReadOnlyDictionary<string, Color>
        ExpectedHighContrastColors() =>
        new Dictionary<string, Color>
        {
            ["WindowBackgroundBrush"] = SystemColors.WindowColor,
            ["CardBackgroundBrush"] = SystemColors.WindowColor,
            ["SubtleBackgroundBrush"] = SystemColors.ControlColor,
            ["ControlBackgroundBrush"] = SystemColors.WindowColor,
            ["ControlHoverBrush"] = SystemColors.HighlightColor,
            ["ControlPressedBrush"] = SystemColors.HotTrackColor,
            ["PrimaryTextBrush"] = SystemColors.WindowTextColor,
            ["SecondaryTextBrush"] = SystemColors.GrayTextColor,
            ["DisabledTextBrush"] = SystemColors.GrayTextColor,
            ["BorderBrush"] = SystemColors.WindowTextColor,
            ["AccentBrush"] = SystemColors.HighlightColor,
            ["AccentForegroundBrush"] = SystemColors.HighlightTextColor,
            ["AccentSoftBrush"] = SystemColors.ControlColor,
            ["SelectionBrush"] = SystemColors.HighlightColor,
            ["SelectionTextBrush"] = SystemColors.HighlightTextColor,
            ["SelectedItemBackgroundBrush"] = SystemColors.HighlightColor,
            ["SelectedItemTextBrush"] = SystemColors.HighlightTextColor,
            ["DangerBrush"] = SystemColors.HotTrackColor,
            ["WarningBrush"] = SystemColors.HotTrackColor,
            ["TokenInputBrush"] = SystemColors.HighlightColor,
            ["TokenOutputBrush"] = SystemColors.HotTrackColor,
            ["TokenCacheWriteBrush"] = SystemColors.WindowTextColor,
            ["TokenCacheReadBrush"] = SystemColors.GrayTextColor,
            ["TooltipBackgroundBrush"] = SystemColors.InfoColor,
        };
}
