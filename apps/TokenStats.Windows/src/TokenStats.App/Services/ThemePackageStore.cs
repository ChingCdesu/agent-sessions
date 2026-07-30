using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace TokenStats.App.Services;

/// <summary>
/// User-facing metadata for a color-only YAML theme package.
/// </summary>
public sealed record ThemePackageInfo(string Id, string Name);

/// <summary>
/// A theme file that could not be read or validated. Invalid packages are
/// isolated so one bad file never prevents TokenStats from starting.
/// </summary>
public sealed record ThemePackageError(string Source, string Message);

/// <summary>
/// A validated theme package. Packages contain colors only; layout, typography,
/// control templates, images, and behavior remain owned by the application.
/// </summary>
internal sealed record ThemePackage(
    string Id,
    string Name,
    IReadOnlyDictionary<string, Color> LightColors,
    IReadOnlyDictionary<string, Color> DarkColors);

/// <summary>
/// Loads color-only YAML theme packages from the application's data folder.
///
/// The supported YAML surface is deliberately small: nested block mappings,
/// comments, and quoted or plain scalar values. Sequences, aliases, tags,
/// flow mappings, block scalars, and multiple documents are rejected. Keeping
/// the grammar closed makes the color-only boundary enforceable without adding
/// a third-party runtime dependency.
/// </summary>
public sealed class ThemePackageStore
{
    public const string DefaultThemeId = "default";

    private const int MaximumThemeBytes = 256 * 1024;
    private const string DefaultThemeResourceName =
        "TokenStats.App.Themes.default.yaml";

    internal static readonly IReadOnlyDictionary<string, string> ResourceKeys =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["windowBackground"] = "WindowBackgroundBrush",
                ["cardBackground"] = "CardBackgroundBrush",
                ["subtleBackground"] = "SubtleBackgroundBrush",
                ["controlBackground"] = "ControlBackgroundBrush",
                ["controlHover"] = "ControlHoverBrush",
                ["controlPressed"] = "ControlPressedBrush",
                ["primaryText"] = "PrimaryTextBrush",
                ["secondaryText"] = "SecondaryTextBrush",
                ["disabledText"] = "DisabledTextBrush",
                ["border"] = "BorderBrush",
                ["accent"] = "AccentBrush",
                ["accentForeground"] = "AccentForegroundBrush",
                ["accentSoft"] = "AccentSoftBrush",
                ["selection"] = "SelectionBrush",
                ["selectionText"] = "SelectionTextBrush",
                ["selectedItemBackground"] = "SelectedItemBackgroundBrush",
                ["selectedItemText"] = "SelectedItemTextBrush",
                ["danger"] = "DangerBrush",
                ["warning"] = "WarningBrush",
                ["tokenInput"] = "TokenInputBrush",
                ["tokenOutput"] = "TokenOutputBrush",
                ["tokenCacheWrite"] = "TokenCacheWriteBrush",
                ["tokenCacheRead"] = "TokenCacheReadBrush",
                ["tooltipBackground"] = "TooltipBackgroundBrush",
            });

    private static readonly Lazy<BuiltInTheme> BuiltIn =
        new(LoadBuiltInTheme, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, ThemePackage> _packages =
        new ReadOnlyDictionary<string, ThemePackage>(
            new Dictionary<string, ThemePackage>(
                StringComparer.OrdinalIgnoreCase));
    private IReadOnlyList<ThemePackageInfo> _themes = Array.Empty<ThemePackageInfo>();
    private IReadOnlyList<ThemePackageError> _errors =
        Array.Empty<ThemePackageError>();

    public ThemePackageStore(string themesDirectory)
    {
        if (string.IsNullOrWhiteSpace(themesDirectory))
        {
            throw new ArgumentException(
                "A theme package directory is required.",
                nameof(themesDirectory));
        }

        ThemesDirectory = Path.GetFullPath(themesDirectory);
        Reload();
    }

    public string ThemesDirectory { get; }

    public IReadOnlyList<ThemePackageInfo> Themes
    {
        get
        {
            lock (_gate)
            {
                return _themes;
            }
        }
    }

    public IReadOnlyList<ThemePackageError> Errors
    {
        get
        {
            lock (_gate)
            {
                return _errors;
            }
        }
    }

    /// <summary>
    /// Rescans the theme directory. Invalid packages are reported through
    /// <see cref="Errors"/> and omitted from <see cref="Themes"/>.
    /// </summary>
    public void Reload()
    {
        var errors = new List<ThemePackageError>();
        EnsureDefaultThemeFile(errors);

        var packages = new Dictionary<string, ThemePackage>(
            StringComparer.OrdinalIgnoreCase)
        {
            [DefaultThemeId] = BuiltIn.Value.Package,
        };
        var packageSources = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase)
        {
            [DefaultThemeId] = null,
        };

        foreach (var path in EnumerateThemeFiles(errors))
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!IsValidThemeId(id))
                {
                    throw new ThemePackageFormatException(
                        "The filename must be a 1-64 character theme id " +
                        "containing only letters, digits, '.', '_', or '-'.");
                }

                if (packageSources.TryGetValue(id, out var existingSource) &&
                    existingSource is not null)
                {
                    throw new ThemePackageFormatException(
                        $"Theme id '{id}' is already provided by " +
                        $"'{Path.GetFileName(existingSource)}'.");
                }

                var package = LoadPackage(path, id);
                packages[id] = package;
                packageSources[id] = path;
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException or
                ThemePackageFormatException)
            {
                errors.Add(new ThemePackageError(path, exception.Message));
            }
        }

        var themes = packages.Values
            .Select(package => new ThemePackageInfo(package.Id, package.Name))
            .OrderBy(info => info.Id == DefaultThemeId ? 0 : 1)
            .ThenBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(info => info.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_gate)
        {
            _packages = new ReadOnlyDictionary<string, ThemePackage>(packages);
            _themes = themes;
            _errors = errors.ToArray();
        }
    }

    internal ThemePackage Resolve(string? themeId)
    {
        lock (_gate)
        {
            return themeId is not null &&
                   _packages.TryGetValue(themeId, out var package)
                ? package
                : _packages[DefaultThemeId];
        }
    }

    internal static ThemePackage BuiltInDefault => BuiltIn.Value.Package;

    internal static bool IsValidThemeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            id.Length > 64 ||
            !char.IsAsciiLetterOrDigit(id[0]))
        {
            return false;
        }

        return id.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-');
    }

    private static BuiltInTheme LoadBuiltInTheme()
    {
        var assembly = typeof(ThemePackageStore).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            DefaultThemeResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded theme '{DefaultThemeResourceName}' is missing.");
        }

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        var yaml = reader.ReadToEnd();
        var package = ThemeYamlParser.Parse(DefaultThemeId, yaml);
        return new BuiltInTheme(yaml, package);
    }

    private ThemePackage LoadPackage(string path, string id)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length > MaximumThemeBytes)
        {
            throw new ThemePackageFormatException(
                $"Theme files may not exceed {MaximumThemeBytes / 1024} KiB.");
        }

        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return ThemeYamlParser.Parse(id, reader.ReadToEnd());
    }

    private IReadOnlyList<string> EnumerateThemeFiles(
        ICollection<ThemePackageError> errors)
    {
        try
        {
            if (!Directory.Exists(ThemesDirectory))
            {
                return Array.Empty<string>();
            }

            return Directory
                .EnumerateFiles(ThemesDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    string.Equals(
                        Path.GetExtension(path),
                        ".yaml",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        Path.GetExtension(path),
                        ".yml",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new ThemePackageError(
                ThemesDirectory,
                $"Could not enumerate theme packages: {exception.Message}"));
            return Array.Empty<string>();
        }
    }

    private void EnsureDefaultThemeFile(
        ICollection<ThemePackageError> errors)
    {
        var destination = Path.Combine(
            ThemesDirectory,
            $"{DefaultThemeId}.yaml");
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(ThemesDirectory);
            if (File.Exists(destination))
            {
                return;
            }

            temporaryPath = Path.Combine(
                ThemesDirectory,
                $".{DefaultThemeId}.{Environment.ProcessId}." +
                $"{Guid.NewGuid():N}.tmp");
            File.WriteAllText(
                temporaryPath,
                BuiltIn.Value.Yaml,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try
            {
                File.Move(temporaryPath, destination);
                temporaryPath = null;
            }
            catch (IOException) when (File.Exists(destination))
            {
                // Another process seeded the same app-data directory first.
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new ThemePackageError(
                destination,
                $"Could not create the default theme package: {exception.Message}"));
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add(new ThemePackageError(
                        temporaryPath,
                        $"Could not remove a temporary theme file: " +
                        exception.Message));
                }
            }
        }
    }

    private sealed record BuiltInTheme(string Yaml, ThemePackage Package);

    private static class ThemeYamlParser
    {
        private static readonly HashSet<string> KnownRootKeys =
            new(StringComparer.Ordinal)
            {
                "schemaVersion",
                "name",
                "colors",
            };

        internal static ThemePackage Parse(string id, string yaml)
        {
            var rootKeys = new HashSet<string>(StringComparer.Ordinal);
            var variants = new HashSet<string>(StringComparer.Ordinal);
            var light = new Dictionary<string, Color>(StringComparer.Ordinal);
            var dark = new Dictionary<string, Color>(StringComparer.Ordinal);
            string? name = null;
            string? currentRoot = null;
            string? currentVariant = null;
            var lineNumber = 0;
            var hasDocumentStart = false;
            var documentEnded = false;

            using var reader = new StringReader(yaml.TrimStart('\uFEFF'));
            while (reader.ReadLine() is { } rawLine)
            {
                lineNumber++;
                if (rawLine.Length > 4 * 1024)
                {
                    throw Error(lineNumber, "A YAML line is too long.");
                }

                if (rawLine.Contains('\t'))
                {
                    throw Error(lineNumber, "Tabs are not allowed for indentation.");
                }

                var indentation = rawLine.TakeWhile(character => character == ' ')
                    .Count();
                var content = StripComment(rawLine[indentation..]).TrimEnd();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                if (content == "---")
                {
                    if (hasDocumentStart ||
                        rootKeys.Count > 0 ||
                        documentEnded)
                    {
                        throw Error(
                            lineNumber,
                            "Multiple YAML documents are not supported.");
                    }

                    hasDocumentStart = true;
                    continue;
                }

                if (content == "...")
                {
                    if (documentEnded || rootKeys.Count == 0)
                    {
                        throw Error(
                            lineNumber,
                            "The YAML document end marker is misplaced.");
                    }

                    documentEnded = true;
                    continue;
                }

                if (documentEnded)
                {
                    throw Error(
                        lineNumber,
                        "Content after the YAML document end is not supported.");
                }

                var mapping = ParseMapping(content, lineNumber);
                switch (indentation)
                {
                    case 0:
                        if (!KnownRootKeys.Contains(mapping.Key))
                        {
                            throw Error(
                                lineNumber,
                                $"Unknown top-level key '{mapping.Key}'.");
                        }

                        if (!rootKeys.Add(mapping.Key))
                        {
                            throw Error(
                                lineNumber,
                                $"Duplicate top-level key '{mapping.Key}'.");
                        }

                        currentRoot = mapping.Key;
                        currentVariant = null;
                        if (mapping.Key == "colors")
                        {
                            RequireContainer(mapping, lineNumber);
                            break;
                        }

                        RequireScalar(mapping, lineNumber);
                        var rootValue = ParseScalar(mapping.Value!, lineNumber);
                        if (mapping.Key == "schemaVersion")
                        {
                            if (rootValue != "1")
                            {
                                throw Error(
                                    lineNumber,
                                    "Only theme schemaVersion 1 is supported.");
                            }
                        }
                        else
                        {
                            name = rootValue.Trim();
                            if (name.Length is < 1 or > 80)
                            {
                                throw Error(
                                    lineNumber,
                                    "Theme names must contain 1-80 characters.");
                            }
                        }

                        break;

                    case 2:
                        if (currentRoot != "colors")
                        {
                            throw Error(
                                lineNumber,
                                "Only light and dark mappings may be nested " +
                                "under colors.");
                        }

                        if (mapping.Key is not ("light" or "dark"))
                        {
                            throw Error(
                                lineNumber,
                                $"Unknown color variant '{mapping.Key}'.");
                        }

                        if (!variants.Add(mapping.Key))
                        {
                            throw Error(
                                lineNumber,
                                $"Duplicate color variant '{mapping.Key}'.");
                        }

                        RequireContainer(mapping, lineNumber);
                        currentVariant = mapping.Key;
                        break;

                    case 4:
                        if (currentRoot != "colors" ||
                            currentVariant is null)
                        {
                            throw Error(
                                lineNumber,
                                "Color values must be nested under " +
                                "colors.light or colors.dark.");
                        }

                        if (!ResourceKeys.ContainsKey(mapping.Key))
                        {
                            throw Error(
                                lineNumber,
                                $"Unknown color key '{mapping.Key}'.");
                        }

                        RequireScalar(mapping, lineNumber);
                        var target = currentVariant == "light" ? light : dark;
                        if (target.ContainsKey(mapping.Key))
                        {
                            throw Error(
                                lineNumber,
                                $"Duplicate {currentVariant} color key " +
                                $"'{mapping.Key}'.");
                        }

                        target[mapping.Key] = ParseColor(
                            ParseScalar(mapping.Value!, lineNumber),
                            lineNumber,
                            mapping.Key);
                        break;

                    default:
                        throw Error(
                            lineNumber,
                            "Theme YAML must use 0, 2, or 4 spaces of indentation.");
                }
            }

            foreach (var required in KnownRootKeys)
            {
                if (!rootKeys.Contains(required))
                {
                    throw new ThemePackageFormatException(
                        $"Missing top-level key '{required}'.");
                }
            }

            foreach (var variant in new[] { "light", "dark" })
            {
                if (!variants.Contains(variant))
                {
                    throw new ThemePackageFormatException(
                        $"Missing colors.{variant} mapping.");
                }
            }

            ValidateCompletePalette("light", light);
            ValidateCompletePalette("dark", dark);

            return new ThemePackage(
                id,
                name!,
                new ReadOnlyDictionary<string, Color>(light),
                new ReadOnlyDictionary<string, Color>(dark));
        }

        private static void ValidateCompletePalette(
            string variant,
            IReadOnlyDictionary<string, Color> colors)
        {
            var missing = ResourceKeys.Keys
                .Where(key => !colors.ContainsKey(key))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new ThemePackageFormatException(
                    $"colors.{variant} is missing: {string.Join(", ", missing)}.");
            }
        }

        private static Mapping ParseMapping(string content, int lineNumber)
        {
            var quote = '\0';
            var escaped = false;
            var separator = -1;
            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                if (quote == '"')
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (quote == '\'')
                {
                    if (character == '\'' &&
                        index + 1 < content.Length &&
                        content[index + 1] == '\'')
                    {
                        index++;
                    }
                    else if (character == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (character is '"' or '\'')
                {
                    quote = character;
                }
                else if (character == ':')
                {
                    separator = index;
                    break;
                }
            }

            if (separator <= 0)
            {
                throw Error(lineNumber, "Expected a YAML key/value mapping.");
            }

            var key = content[..separator].Trim();
            if (!IsValidKey(key))
            {
                throw Error(lineNumber, $"Invalid YAML key '{key}'.");
            }

            var value = content[(separator + 1)..].Trim();
            return new Mapping(key, value.Length == 0 ? null : value);
        }

        private static string StripComment(string content)
        {
            var quote = '\0';
            var escaped = false;
            for (var index = 0; index < content.Length; index++)
            {
                var character = content[index];
                if (quote == '"')
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (quote == '\'')
                {
                    if (character == '\'' &&
                        index + 1 < content.Length &&
                        content[index + 1] == '\'')
                    {
                        index++;
                    }
                    else if (character == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (character is '"' or '\'')
                {
                    quote = character;
                }
                else if (character == '#')
                {
                    return content[..index];
                }
            }

            return content;
        }

        private static string ParseScalar(string value, int lineNumber)
        {
            if (value.StartsWith('"'))
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(value) ??
                           throw Error(lineNumber, "A scalar may not be null.");
                }
                catch (JsonException exception)
                {
                    throw Error(
                        lineNumber,
                        $"Invalid double-quoted scalar: {exception.Message}");
                }
            }

            if (value.StartsWith('\''))
            {
                if (value.Length < 2 || !value.EndsWith('\''))
                {
                    throw Error(lineNumber, "Unterminated single-quoted scalar.");
                }

                var result = new StringBuilder();
                for (var index = 1; index < value.Length - 1; index++)
                {
                    if (value[index] != '\'')
                    {
                        result.Append(value[index]);
                        continue;
                    }

                    if (index + 1 >= value.Length - 1 ||
                        value[index + 1] != '\'')
                    {
                        throw Error(
                            lineNumber,
                            "Single quotes inside a scalar must be doubled.");
                    }

                    result.Append('\'');
                    index++;
                }

                return result.ToString();
            }

            if (value[0] is '&' or '*' or '!' or '[' or '{' or '|' or '>')
            {
                throw Error(
                    lineNumber,
                    "YAML aliases, tags, flow collections, and block scalars " +
                    "are not supported.");
            }

            return value;
        }

        private static Color ParseColor(
            string value,
            int lineNumber,
            string key)
        {
            if (value.Length is not (7 or 9) || value[0] != '#')
            {
                throw Error(
                    lineNumber,
                    $"Color '{key}' must be #RRGGBB or #AARRGGBB.");
            }

            if (!uint.TryParse(
                    value.AsSpan(1),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var packed))
            {
                throw Error(lineNumber, $"Color '{key}' is not valid hexadecimal.");
            }

            return value.Length == 7
                ? Color.FromRgb(
                    (byte)(packed >> 16),
                    (byte)(packed >> 8),
                    (byte)packed)
                : Color.FromArgb(
                    (byte)(packed >> 24),
                    (byte)(packed >> 16),
                    (byte)(packed >> 8),
                    (byte)packed);
        }

        private static bool IsValidKey(string key) =>
            key.Length > 0 &&
            char.IsAsciiLetter(key[0]) &&
            key.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-');

        private static void RequireContainer(Mapping mapping, int lineNumber)
        {
            if (mapping.Value is not null)
            {
                throw Error(
                    lineNumber,
                    $"'{mapping.Key}' must contain a nested mapping.");
            }
        }

        private static void RequireScalar(Mapping mapping, int lineNumber)
        {
            if (mapping.Value is null)
            {
                throw Error(
                    lineNumber,
                    $"'{mapping.Key}' requires a scalar value.");
            }
        }

        private static ThemePackageFormatException Error(
            int lineNumber,
            string message) =>
            new($"Line {lineNumber}: {message}");

        private sealed record Mapping(string Key, string? Value);
    }
}

internal sealed class ThemePackageFormatException : FormatException
{
    internal ThemePackageFormatException(string message)
        : base(message)
    {
    }
}
