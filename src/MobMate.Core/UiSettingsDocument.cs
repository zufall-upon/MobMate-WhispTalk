using System.Text.Json.Serialization;

namespace MobMate.Core;

public sealed class UiSettingsDocument
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("settings")]
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("browserMigrationCompleted")]
    public bool BrowserMigrationCompleted { get; init; }

    public static UiSettingsDocument Normalize(UiSettingsDocument? document)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (document?.Settings is not null)
        {
            foreach (var (key, value) in document.Settings)
            {
                if (!string.IsNullOrEmpty(key) && key.Length <= 256 && value is not null && value.Length <= 65536)
                {
                    settings[key] = value;
                }
            }
        }

        return new UiSettingsDocument
        {
            Settings = settings,
            BrowserMigrationCompleted = document?.BrowserMigrationCompleted == true,
        };
    }

    public string ResolveWizardReason() =>
        Settings.TryGetValue("mobmate.wizard.completed", out var completed) && completed == "1"
            ? "completed"
            : BrowserMigrationCompleted ? "not_completed" : "migration_pending";

    public static UiSettingsDocument PrepareForSave(UiSettingsDocument? request, UiSettingsDocument current)
    {
        var normalized = Normalize(request);
        if (!current.BrowserMigrationCompleted || normalized.BrowserMigrationCompleted)
        {
            return normalized;
        }

        return new UiSettingsDocument
        {
            Settings = normalized.Settings,
            BrowserMigrationCompleted = true,
        };
    }
}

public static class UiSettingsApiContract
{
    public const int SaveFailedStatusCode = 503;

    public static UiSettingsApiError SaveFailed { get; } = new("ui_settings.save_failed");
}

public sealed record UiSettingsApiError(
    [property: JsonPropertyName("code")] string Code);
