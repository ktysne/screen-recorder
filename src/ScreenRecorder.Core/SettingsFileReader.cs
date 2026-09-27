using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenRecorder.Core;

/// <summary>設定ファイルの読み取りで見つかった問題を表します。</summary>
public sealed record SettingsFileIssue(string Code, string? Property, string Message);

/// <summary>設定値、検査結果、既定値を補った項目をまとめます。</summary>
public sealed record SettingsFileReadResult(
    Settings Settings,
    IReadOnlyList<SettingsFileIssue> Issues,
    IReadOnlyList<string> DefaultedProperties);

/// <summary>設定ファイルを変更せずに読み取り、既定値と問題を返します。</summary>
public static class SettingsFileReader
{
    private static readonly PropertyInfo[] SettingProperties = typeof(Settings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.CanRead && property.CanWrite)
        .OrderBy(property => property.MetadataToken)
        .ToArray();

    private static readonly HashSet<string> NullableStringProperties =
    [
        nameof(Settings.ConfirmedStillImageDirectory),
        nameof(Settings.ConfirmedVideoDirectory),
        nameof(Settings.MicrophoneDeviceId),
        nameof(Settings.SkippedUpdateVersion)
    ];

    /// <summary>指定された設定ファイルを読み取り、ファイルや隣接ファイルを書き換えずに検査します。</summary>
    public static SettingsFileReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var settings = new Settings();
        var defaults = new Settings();
        var issues = new List<SettingsFileIssue>();
        var defaultedProperties = new List<string>();
        if (IsMissingFile(path))
        {
            defaultedProperties.AddRange(SettingProperties.Select(property => property.Name));
            return new SettingsFileReadResult(settings, issues, defaultedProperties);
        }

        using var document = TryParse(path, issues);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            if (document is not null)
                issues.Add(new SettingsFileIssue("invalidJson", null, "設定ファイルの最上位は JSON オブジェクトである必要があります。"));
            defaultedProperties.AddRange(SettingProperties.Select(property => property.Name));
            return new SettingsFileReadResult(settings, issues, defaultedProperties);
        }

        var root = document.RootElement;
        var jsonNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var jsonProperty in root.EnumerateObject())
            jsonNames.Add(jsonProperty.Name);

        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in SettingProperties)
            knownNames.Add(JsonNamingPolicy.CamelCase.ConvertName(property.Name));

        foreach (var jsonName in jsonNames)
        {
            if (!knownNames.Contains(jsonName))
                issues.Add(new SettingsFileIssue("unknownProperty", jsonName, $"知らない設定項目です: {jsonName}"));
        }

        foreach (var property in SettingProperties)
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (!root.TryGetProperty(jsonName, out var element))
            {
                defaultedProperties.Add(property.Name);
                continue;
            }

            if (!TryReadValue(property, element, defaults, out var value, out var code, out var message))
            {
                defaultedProperties.Add(property.Name);
                issues.Add(new SettingsFileIssue(code, property.Name, message));
                continue;
            }

            property.SetValue(settings, value);
        }

        foreach (var issue in SettingsValidator.Validate(settings))
        {
            var code = issue.Kind switch
            {
                SettingsIssueKind.InvalidShortcut => "invalidShortcut",
                SettingsIssueKind.DuplicateShortcut => "duplicateShortcut",
                _ => "invalidValue"
            };
            issues.Add(new SettingsFileIssue(code, issue.SettingName, $"設定値が正しくありません: {issue.SettingName}"));
        }

        return new SettingsFileReadResult(settings, issues, defaultedProperties);
    }

    private static JsonDocument? TryParse(string path, List<SettingsFileIssue> issues)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            issues.Add(new SettingsFileIssue("invalidJson", null, "設定ファイルの JSON が壊れています。"));
            return null;
        }
    }

    private static bool IsMissingFile(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static bool TryReadValue(
        PropertyInfo property,
        JsonElement element,
        Settings defaults,
        out object? value,
        out string code,
        out string message)
    {
        value = null;
        code = "invalidValue";
        message = $"設定値の形式が正しくありません: {property.Name}";

        if (property.PropertyType == typeof(string))
        {
            if (NullableStringProperties.Contains(property.Name) && element.ValueKind == JsonValueKind.Null)
            {
                value = null;
                return true;
            }

            if (element.ValueKind != JsonValueKind.String) return false;
            var text = element.GetString()!;
            if (property.Name is nameof(Settings.StillImageDirectory) or nameof(Settings.VideoDirectory)
                && string.IsNullOrWhiteSpace(text))
                return false;
            value = text;
            return true;
        }

        if (property.PropertyType == typeof(bool))
        {
            if (element.ValueKind == JsonValueKind.True)
            {
                value = true;
                return true;
            }
            if (element.ValueKind == JsonValueKind.False)
            {
                value = false;
                return true;
            }
            return false;
        }

        var intSetting = SettingsSchema.IntSettings.FirstOrDefault(setting => setting.Name == property.Name);
        if (intSetting is not null)
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var parsed)) return false;
            if (!intSetting.IsValid(parsed))
            {
                code = "valueOutOfRange";
                message = $"設定値が許容範囲外です: {property.Name}";
                return false;
            }
            value = parsed;
            return true;
        }

        if (property.PropertyType.IsEnum && element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (property.PropertyType == typeof(DiagnosticLogLevel))
            {
                value = text switch
                {
                    "silent" => DiagnosticLogLevel.Silent,
                    "error" => DiagnosticLogLevel.Error,
                    "warn" => DiagnosticLogLevel.Warn,
                    "info" => DiagnosticLogLevel.Info,
                    "debug" => DiagnosticLogLevel.Debug,
                    _ => null
                };
                return value is not null;
            }

            if (Enum.TryParse(property.PropertyType, text, ignoreCase: true, out var parsedEnum)
                && parsedEnum is not null
                && Enum.IsDefined(property.PropertyType, parsedEnum))
            {
                value = parsedEnum;
                return true;
            }
            return false;
        }

        return false;
    }
}
