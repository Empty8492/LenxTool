using System.Text.Json;
using LenxTool.Core.Contracts;

namespace LenxTool.App.Services;

public enum WindowsNotificationPreviewMode
{
    GenericOnly,
    TitleOnly
}

public sealed record WindowsNotificationSettings(
    bool Enabled,
    WindowsNotificationPreviewMode PreviewMode,
    bool QuietHoursEnabled,
    int QuietStartMinutes,
    int QuietEndMinutes,
    int CoalesceMinutes)
{
    private static readonly int[] AllowedCoalesceMinutes =
        [0, 5, 15, 30, 60];

    public static WindowsNotificationSettings Default { get; } = new(
        Enabled: false,
        WindowsNotificationPreviewMode.GenericOnly,
        QuietHoursEnabled: true,
        QuietStartMinutes: 22 * 60,
        QuietEndMinutes: 7 * 60,
        CoalesceMinutes: 15);

    public void Validate()
    {
        if (!Enum.IsDefined(PreviewMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PreviewMode));
        }
        if (QuietStartMinutes is < 0 or >= 24 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(QuietStartMinutes));
        }
        if (QuietEndMinutes is < 0 or >= 24 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(QuietEndMinutes));
        }
        if (QuietHoursEnabled && QuietStartMinutes == QuietEndMinutes)
        {
            throw new ArgumentException(
                "勿扰开始和结束时间不能相同。",
                nameof(QuietEndMinutes));
        }
        if (!AllowedCoalesceMinutes.Contains(CoalesceMinutes))
        {
            throw new ArgumentOutOfRangeException(nameof(CoalesceMinutes));
        }
    }
}

public static class WindowsNotificationPolicy
{
    public static bool IsQuietTime(
        WindowsNotificationSettings settings,
        TimeOnly localTime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (!settings.QuietHoursEnabled)
        {
            return false;
        }

        int current = localTime.Hour * 60 + localTime.Minute;
        int start = settings.QuietStartMinutes;
        int end = settings.QuietEndMinutes;
        return start < end
            ? current >= start && current < end
            : current >= start || current < end;
    }
}

public interface IWindowsNotificationSettingsStore
{
    Task<WindowsNotificationSettings> GetAsync(
        CancellationToken cancellationToken);

    Task SaveAsync(
        WindowsNotificationSettings settings,
        CancellationToken cancellationToken);
}

public sealed class AppSettingsWindowsNotificationSettingsStore(
    IAppSettingsRepository repository) : IWindowsNotificationSettingsStore
{
    public const string SettingsKey = "notifications.windows.v1";
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<WindowsNotificationSettings> GetAsync(
        CancellationToken cancellationToken)
    {
        string? json = await repository.GetAsync(
            SettingsKey,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return WindowsNotificationSettings.Default;
        }

        try
        {
            StoredWindowsNotificationSettings? stored =
                JsonSerializer.Deserialize<
                    StoredWindowsNotificationSettings>(json, JsonOptions);
            if (stored is null || stored.SchemaVersion != SchemaVersion)
            {
                return WindowsNotificationSettings.Default;
            }
            var result = new WindowsNotificationSettings(
                stored.Enabled,
                stored.PreviewMode,
                stored.QuietHoursEnabled,
                stored.QuietStartMinutes,
                stored.QuietEndMinutes,
                stored.CoalesceMinutes);
            result.Validate();
            return result;
        }
        catch (Exception exception)
            when (exception is JsonException
                  or ArgumentException)
        {
            return WindowsNotificationSettings.Default;
        }
    }

    public Task SaveAsync(
        WindowsNotificationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        string json = JsonSerializer.Serialize(
            new StoredWindowsNotificationSettings(
                SchemaVersion,
                settings.Enabled,
                settings.PreviewMode,
                settings.QuietHoursEnabled,
                settings.QuietStartMinutes,
                settings.QuietEndMinutes,
                settings.CoalesceMinutes),
            JsonOptions);
        return repository.SetAsync(SettingsKey, json, cancellationToken);
    }

    private sealed record StoredWindowsNotificationSettings(
        int SchemaVersion,
        bool Enabled,
        WindowsNotificationPreviewMode PreviewMode,
        bool QuietHoursEnabled,
        int QuietStartMinutes,
        int QuietEndMinutes,
        int CoalesceMinutes);
}
