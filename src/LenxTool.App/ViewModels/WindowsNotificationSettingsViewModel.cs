using System.Globalization;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;

namespace LenxTool.App.ViewModels;

public sealed record WindowsNotificationPreviewOption(
    WindowsNotificationPreviewMode Value,
    string Label);

public sealed class WindowsNotificationSettingsViewModel : ObservableObject
{
    private readonly IWindowsNotificationSettingsStore _store;
    private readonly IWindowsNotificationController _controller;
    private bool _enabled;
    private WindowsNotificationPreviewMode _previewMode;
    private bool _quietHoursEnabled;
    private string _quietStartText = "22:00";
    private string _quietEndText = "07:00";
    private int _coalesceMinutes = 15;
    private string _status = "Windows 通知默认关闭，启用后才会显示。";

    public WindowsNotificationSettingsViewModel(
        IWindowsNotificationSettingsStore store,
        IWindowsNotificationController controller)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _controller = controller ??
            throw new ArgumentNullException(nameof(controller));
        SaveCommand = new(SaveAsync);
    }

    public IReadOnlyList<WindowsNotificationPreviewOption> PreviewModes { get; } =
    [
        new(
            WindowsNotificationPreviewMode.GenericOnly,
            "仅显示通用提示（推荐）"),
        new(
            WindowsNotificationPreviewMode.TitleOnly,
            "显示标题，不显示来源和正文")
    ];

    public IReadOnlyList<int> CoalesceOptions { get; } =
        [0, 5, 15, 30, 60];

    public AsyncRelayCommand SaveCommand { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public WindowsNotificationPreviewMode PreviewMode
    {
        get => _previewMode;
        set => SetProperty(ref _previewMode, value);
    }

    public bool QuietHoursEnabled
    {
        get => _quietHoursEnabled;
        set => SetProperty(ref _quietHoursEnabled, value);
    }

    public string QuietStartText
    {
        get => _quietStartText;
        set => SetProperty(ref _quietStartText, value ?? string.Empty);
    }

    public string QuietEndText
    {
        get => _quietEndText;
        set => SetProperty(ref _quietEndText, value ?? string.Empty);
    }

    public int CoalesceMinutes
    {
        get => _coalesceMinutes;
        set => SetProperty(ref _coalesceMinutes, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string AvailabilityStatus => _controller.Availability switch
    {
        WindowsNotificationAvailability.Available =>
            "Windows 系统通知可用。",
        WindowsNotificationAvailability.DisabledForApplication =>
            "Windows 已关闭 Lenx Tools 的通知，请在系统设置中启用。",
        WindowsNotificationAvailability.DisabledForUser =>
            "Windows 通知已被当前用户关闭。",
        WindowsNotificationAvailability.DisabledByGroupPolicy =>
            "Windows 通知已被组织策略关闭。",
        WindowsNotificationAvailability.DisabledByManifest =>
            "当前应用清单不允许 Windows 通知。",
        WindowsNotificationAvailability.RegistrationFailed =>
            "Windows 通知注册失败；应用内通知仍会保留。",
        _ => "当前环境不支持 Windows 系统通知；应用内通知仍会保留。"
    };

    public string PrivacyDescription { get; } =
        "系统通知不包含正文或来源；“仅通用提示”也不显示标题。" +
        "锁屏是否展示由 Windows 系统通知设置控制。";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        WindowsNotificationSettings settings =
            await _store.GetAsync(cancellationToken).ConfigureAwait(true);
        ApplyToEditor(settings);
        if (_controller.Settings != settings)
        {
            _controller.ApplySettings(settings);
        }
        Status = settings.Enabled
            ? "Windows 通知设置已从本地恢复。"
            : "Windows 通知当前关闭；应用内通知仍会正常保存。";
        OnPropertyChanged(nameof(AvailabilityStatus));
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!TryParseMinutes(QuietStartText, out int start) ||
            !TryParseMinutes(QuietEndText, out int end) ||
            (QuietHoursEnabled && start == end))
        {
            Status = "保存失败：静默时段必须使用 HH:mm，且开始和结束不能相同。";
            return;
        }

        var settings = new WindowsNotificationSettings(
            Enabled,
            PreviewMode,
            QuietHoursEnabled,
            start,
            end,
            CoalesceMinutes);
        try
        {
            settings.Validate();
            await _store.SaveAsync(settings, cancellationToken)
                .ConfigureAwait(true);
            _controller.ApplySettings(settings);
            Status = settings.Enabled
                ? "Windows 通知设置已保存并立即生效。"
                : "Windows 通知已关闭；应用内通知仍会正常保存。";
            OnPropertyChanged(nameof(AvailabilityStatus));
        }
        catch (ArgumentException)
        {
            Status = "保存失败：静默时段必须使用 HH:mm，并选择受支持的聚合间隔。";
        }
    }

    private void ApplyToEditor(WindowsNotificationSettings settings)
    {
        Enabled = settings.Enabled;
        PreviewMode = settings.PreviewMode;
        QuietHoursEnabled = settings.QuietHoursEnabled;
        QuietStartText = FormatMinutes(settings.QuietStartMinutes);
        QuietEndText = FormatMinutes(settings.QuietEndMinutes);
        CoalesceMinutes = settings.CoalesceMinutes;
    }

    private static bool TryParseMinutes(string text, out int minutes)
    {
        minutes = 0;
        if (!TimeOnly.TryParseExact(
                text,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly time))
        {
            return false;
        }
        minutes = time.Hour * 60 + time.Minute;
        return true;
    }

    private static string FormatMinutes(int minutes) =>
        $"{minutes / 60:00}:{minutes % 60:00}";
}
