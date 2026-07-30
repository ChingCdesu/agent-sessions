using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TokenStats.App.Controls;
using TokenStats.App.Services;

namespace TokenStats.App.Infrastructure;

internal enum WindowsAppTheme
{
    Light,
    Dark,
    HighContrast,
}

internal static class WindowsThemeService
{
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;

    internal static event EventHandler? ThemeChanged;

    internal static WindowsAppTheme CurrentTheme { get; private set; } =
        WindowsAppTheme.Light;

    internal static string CurrentThemePackageId { get; private set; } =
        ThemePackageStore.DefaultThemeId;

    internal static void ApplySystemTheme(ResourceDictionary resources) =>
        Apply(
            resources,
            DetectSystemTheme(),
            ThemePackageStore.BuiltInDefault);

    internal static void ApplySystemTheme(
        ResourceDictionary resources,
        ThemePackage package) =>
        Apply(resources, DetectSystemTheme(), package);

    internal static void Apply(
        ResourceDictionary resources,
        WindowsAppTheme theme) =>
        Apply(resources, theme, ThemePackageStore.BuiltInDefault);

    internal static void Apply(
        ResourceDictionary resources,
        WindowsAppTheme theme,
        ThemePackage package)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(package);

        var colors = theme switch
        {
            WindowsAppTheme.HighContrast => HighContrastColors(),
            WindowsAppTheme.Dark => package.DarkColors,
            _ => package.LightColors,
        };

        // A package is fully parsed and validated before any shared resource is
        // updated, so a malformed YAML document can never leave a mixed palette.
        foreach (var (colorKey, resourceKey) in ThemePackageStore.ResourceKeys)
        {
            SetBrush(resources, resourceKey, colors[colorKey]);
        }

        CurrentTheme = theme;
        CurrentThemePackageId = package.Id;

        if (System.Windows.Application.Current is { } application)
        {
            foreach (Window window in application.Windows)
            {
                ApplyWindowChrome(window);
                InvalidateThemeVisuals(window);
            }
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyWindowChrome(window);
        }
    }

    internal static WindowsAppTheme DetectSystemTheme()
    {
        if (SystemParameters.HighContrast)
        {
            return WindowsAppTheme.HighContrast;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? WindowsAppTheme.Dark
                : WindowsAppTheme.Light;
        }
        catch
        {
            return WindowsAppTheme.Light;
        }
    }

    private static IReadOnlyDictionary<string, Color> HighContrastColors() =>
        new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["windowBackground"] = SystemColors.WindowColor,
            ["cardBackground"] = SystemColors.WindowColor,
            ["subtleBackground"] = SystemColors.ControlColor,
            ["controlBackground"] = SystemColors.WindowColor,
            ["controlHover"] = SystemColors.HighlightColor,
            ["controlPressed"] = SystemColors.HotTrackColor,
            ["primaryText"] = SystemColors.WindowTextColor,
            ["secondaryText"] = SystemColors.GrayTextColor,
            ["disabledText"] = SystemColors.GrayTextColor,
            ["border"] = SystemColors.WindowTextColor,
            ["accent"] = SystemColors.HighlightColor,
            ["accentForeground"] = SystemColors.HighlightTextColor,
            ["accentSoft"] = SystemColors.ControlColor,
            ["selection"] = SystemColors.HighlightColor,
            ["selectionText"] = SystemColors.HighlightTextColor,
            ["selectedItemBackground"] = SystemColors.HighlightColor,
            ["selectedItemText"] = SystemColors.HighlightTextColor,
            ["danger"] = SystemColors.HotTrackColor,
            ["warning"] = SystemColors.HotTrackColor,
            ["tokenInput"] = SystemColors.HighlightColor,
            ["tokenOutput"] = SystemColors.HotTrackColor,
            ["tokenCacheWrite"] = SystemColors.WindowTextColor,
            ["tokenCacheRead"] = SystemColors.GrayTextColor,
            ["tooltipBackground"] = SystemColors.InfoColor,
        };

    private static void ApplyWindowChrome(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDarkChrome = CurrentTheme == WindowsAppTheme.Dark ? 1 : 0;
        var result = DwmSetWindowAttribute(
            handle,
            DwmUseImmersiveDarkMode,
            ref useDarkChrome,
            sizeof(int));
        if (result != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref useDarkChrome,
                sizeof(int));
        }
    }

    private static void InvalidateThemeVisuals(DependencyObject root)
    {
        if (root is UsageGauge gauge)
        {
            gauge.InvalidateVisual();
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            InvalidateThemeVisuals(VisualTreeHelper.GetChild(root, index));
        }
    }

    private static void SetBrush(
        ResourceDictionary resources,
        string key,
        Color color)
    {
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        // Reuse a mutable brush when WPF permits it. Shared resources may be
        // frozen after resolution; in that case replace the resource and let
        // ThemeChanged refresh controls that were created in code.
        resources[key] = new SolidColorBrush(color);
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
