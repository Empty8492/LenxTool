using System.IO;
using System.Reflection;
using System.Windows;
using LenxTool.App.Services;
using LenxTool.App.ViewModels;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Infrastructure.Data;
using LenxTool.Infrastructure.Media;
using LenxTool.Infrastructure.Networking;
using LenxTool.Infrastructure.Security;
using LenxTool.Infrastructure.SystemServices;
using LenxTool.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LenxTool.App;

public partial class App : Application
{
    private IHost? _host;
    private readonly ExceptionDialogGate _exceptionDialogGate = new();

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            ConfigureServices(builder.Services);
            _host = builder.Build();
            await _host.StartAsync().ConfigureAwait(true);

            SqliteDatabase database = _host.Services.GetRequiredService<SqliteDatabase>();
            await database.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            NewsCenterViewModel newsCenter = _host.Services.GetRequiredService<NewsCenterViewModel>();
            await newsCenter.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            MediaWorkbenchViewModel media = _host.Services.GetRequiredService<MediaWorkbenchViewModel>();
            await media.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            SettingsViewModel settings = _host.Services.GetRequiredService<SettingsViewModel>();
            await settings.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            FeedAdminViewModel feedAdmin = _host.Services.GetRequiredService<FeedAdminViewModel>();
            await feedAdmin.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            IFeedRefreshService feedRefresh = _host.Services.GetRequiredService<IFeedRefreshService>();
            await feedRefresh.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            HistoryViewModel history = _host.Services.GetRequiredService<HistoryViewModel>();
            await history.InitializeAsync(CancellationToken.None).ConfigureAwait(true);

            MainWindow window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
            _ = settings.CheckInBackgroundAsync(CancellationToken.None);
        }
        catch (AppException exception)
        {
            ShowStartupError(exception.Error);
            Shutdown(-1);
        }
        catch (Exception exception)
        {
            string detail = SecretRedactor.Redact(exception.Message);
            MessageBox.Show(
                $"Lenx Tools 暂时无法启动。\n\n{detail}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(AppPaths.CreateDefault());
        services.AddSingleton(static services =>
            new ExceptionDiagnosticLog(services.GetRequiredService<AppPaths>().LogsDirectory));
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<INewsRepository, NewsRepository>();
        services.AddSingleton<INewsCenterService, NewsCenterService>();
        services.AddSingleton<IAiReportService, DeepSeekReportService>();
        services.AddSingleton<ISubtitleTranslator, DeepSeekSubtitleTranslator>();
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        services.AddSingleton(CreateWorkerAccountOptions());
        services.AddSingleton<WorkerAccountSessionService>();
        services.AddSingleton<IAccountSessionService>(static services =>
            services.GetRequiredService<WorkerAccountSessionService>());
        services.AddSingleton<IFeedCatalogRepository, FeedCatalogRepository>();
        services.AddSingleton<IFeedFetchStateRepository, FeedFetchStateRepository>();
        services.AddSingleton<FeedEntryRepository>();
        services.AddSingleton<IFeedEntryWriter>(static services =>
            services.GetRequiredService<FeedEntryRepository>());
        services.AddSingleton<IFeedEntryRepository>(static services =>
            services.GetRequiredService<FeedEntryRepository>());
        services.AddSingleton(FeedCatalogSyncOptions.Default);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IFeedCatalogSyncService, FeedCatalogSyncService>();
        services.AddSingleton<IFeedCatalogAdminService, FeedCatalogAdminService>();
        services.AddFeedDiscovery(CreateFeedDiscoveryOptions());
        services.AddFeedRefresh(FeedRefreshOptions.Default);
        services.AddSingleton<MediaJobRepository>();
        services.AddSingleton<IMediaJobRepository>(static services =>
            services.GetRequiredService<MediaJobRepository>());
        services.AddSingleton<ISubtitleRepository>(static services =>
            services.GetRequiredService<MediaJobRepository>());
        services.AddSingleton<IAppSettingsRepository, AppSettingsRepository>();
        services.AddSingleton<IFileHashService, FileHashService>();
        services.AddSingleton<ILocalModelService, LocalWhisperModelService>();
        services.AddSingleton<IDatabaseMaintenanceService, DatabaseMaintenanceService>();
        services.AddSingleton<IDocumentConverter, WordComDocumentConverter>();
        services.AddSingleton<ITranscriptionService, GroqWhisperClient>();
        services.AddSingleton<ILocalTranscriptionService, LocalWhisperTranscriptionService>();
        services.AddSingleton<IMediaAudioService, MediaFoundationAudioService>();
        services.AddSingleton<IDesktopFileDialogService, DesktopFileDialogService>();
        services.AddSingleton<ISubtitleExportService, SubtitleExportService>();
        services.AddSingleton(CreateUpdateOptions());
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddHttpClient("LenxTool.Default", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("LenxTool.News", client => client.Timeout = TimeSpan.FromSeconds(15));
        services.AddHttpClient("LenxTool.Groq", client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddHttpClient("LenxTool.DeepSeek", client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddHttpClient("LenxTool.Update", client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient("LenxTool.Account", client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<NewsCenterViewModel>();
        services.AddSingleton<MediaWorkbenchViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<ToolsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<FeedAdminViewModel>();
        services.AddSingleton(CreateShellViewModel);
        services.AddSingleton<MainWindow>();
    }

    private static ShellViewModel CreateShellViewModel(IServiceProvider services)
    {
        DashboardViewModel dashboard = services.GetRequiredService<DashboardViewModel>();
        NewsCenterViewModel news = services.GetRequiredService<NewsCenterViewModel>();
        MediaWorkbenchViewModel media = services.GetRequiredService<MediaWorkbenchViewModel>();
        HistoryViewModel history = services.GetRequiredService<HistoryViewModel>();
        ToolsViewModel tools = services.GetRequiredService<ToolsViewModel>();
        SettingsViewModel settings = services.GetRequiredService<SettingsViewModel>();
        IAccountSessionService accountSession = services.GetRequiredService<IAccountSessionService>();
        FeedAdminViewModel feedAdmin = services.GetRequiredService<FeedAdminViewModel>();

        return new(
        [
            new("home", "首页", "今日概览与快捷开始", "M3,11 L12,3 21,11 21,21 14,21 14,15 10,15 10,21 3,21 Z", dashboard),
            new("news", "资讯中心", "早报、热点、收藏与 AI 报告", "M4,4 L20,4 20,18 7,18 4,21 Z M8,8 L16,8 M8,12 L17,12 M8,16 L14,16", news),
            new("media", "媒体工作台", "字幕、音频与批量任务", "M4,5 L20,5 20,17 4,17 Z M9,9 L15,12 9,15 Z M8,21 L16,21", media),
            new("tools", "文档与数据", "转换、JSON、编码与校验", "M6,3 L18,3 18,21 6,21 Z M9,8 L15,8 M9,12 L15,12 M9,16 L13,16", tools),
            new("history", "历史与数据", "任务、收藏、搜索与备份", "M12,4 A8,8 0 1 1 4.5,9 M4,4 L4,9 9,9 M12,8 L12,13 16,15", history),
            new("feed-admin", "订阅管理", "管理员共享目录入口", "M4,5 L20,5 20,19 4,19 Z M8,9 L16,9 M8,13 L16,13 M8,17 L13,17", feedAdmin, AdminOnly: true),
            new("settings", "设置", "主题、密钥、账号与更新", "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 M12,3 L13,5 16,6 18,5 20,9 18,11 18,14 20,16 18,20 15,19 13,20 11,19 8,20 6,18 7,15 6,12 4,10 6,6 9,6 Z", settings)
        ], accountSession);
    }

    private static WorkerAccountOptions CreateWorkerAccountOptions()
    {
        string? configured = Environment.GetEnvironmentVariable("LENXTOOL_WORKER_BASE_URL");
        return Uri.TryCreate(configured, UriKind.Absolute, out Uri? address)
            && address.Scheme == Uri.UriSchemeHttps
            ? new(address)
            : new(null);
    }

    private static FeedDiscoveryOptions CreateFeedDiscoveryOptions() =>
        FeedDiscoveryOptions.Default with
        {
            AllowedHttpHosts = ReadConfiguredHosts("LENXTOOL_FEED_HTTP_HOSTS"),
            TrustedPrivateHosts = ReadConfiguredHosts("LENXTOOL_FEED_PRIVATE_HOSTS")
        };

    private static HashSet<string> ReadConfiguredHosts(string variableName)
    {
        string? configured = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(configured))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in configured.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string host = candidate.Trim().TrimEnd('.');
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                throw new InvalidOperationException($"{variableName} contains an invalid host name.");
            hosts.Add(System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address)
                ? address.ToString().ToLowerInvariant()
                : new System.Globalization.IdnMapping().GetAscii(host).ToLowerInvariant());
        }
        return hosts;
    }

    private static UpdateOptions CreateUpdateOptions()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LenxTool.UpdatePublicKey")
            ?? throw new InvalidOperationException("缺少更新公钥资源。");
        using var reader = new StreamReader(stream);
        string publicKey = reader.ReadToEnd();
        return new(
            [new Uri("https://github.com/Empty8492/LenxTools/releases/latest/download/update-manifest.json")],
            publicKey);
    }

    private static WorkspacePageViewModel CreateMediaPage() =>
        new(
            "媒体工作台",
            "批量处理音视频、已有 SRT，并保留可取消、可重试的任务历史",
            "导入媒体",
            [
                new("QUEUE", "批量任务", "排队、进度、取消、失败重试与完成后打开输出目录。", "并发 1"),
                new("GROQ", "云端 Whisper", "使用自备 Groq Key 或共享额度；限流信息会被精确解析。", "未配置"),
                new("LOCAL", "本地 Whisper", "导入现有 ggml 模型，媒体和字幕全程留在本机。", "等待模型"),
                new("EXPORT", "字幕与文本", "导出原文 SRT、双语 SRT 和纯文本。", "UTF-8")
            ]);

    private static WorkspacePageViewModel CreateHistoryPage() =>
        new(
            "历史与数据",
            "搜索任务、输出、收藏、错误与模型用量，并管理本地数据库",
            "全局搜索",
            [
                new("TASKS", "任务历史", "重新执行任务、打开输出文件并查看结构化错误。", "0 个运行中"),
                new("SEARCH", "全文搜索", "统一搜索早报、热点和 AI 报告。", "FTS5 已就绪"),
                new("FAVORITES", "收藏", "收藏内容不会被 180 天清理策略自动删除。", "本地保存"),
                new("BACKUP", "备份与恢复", "一键备份数据库；恢复前自动保留当前副本。", "可用")
            ]);

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (!_exceptionDialogGate.TryEnter()) return;

        ILogger<App>? logger = _host?.Services.GetService<ILogger<App>>();
        if (logger is not null)
        {
            LogUnhandledException(logger, SecretRedactor.Redact(e.Exception.Message));
        }

        try
        {
            _host?.Services.GetService<ExceptionDiagnosticLog>()?.Write(e.Exception);
        }
        catch (Exception logException)
        {
            if (logger is not null)
            {
                LogDiagnosticWriteFailure(logger, SecretRedactor.Redact(logException.Message));
            }
        }

        MessageBox.Show(
            "操作未能完成。任务状态已尽可能保留，请重试或打开日志查看脱敏详情。",
            "Lenx Tools 遇到问题",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void ShowStartupError(AppError error) =>
        MessageBox.Show(
            $"{error.UserMessage}\n\n建议：{error.Suggestion}",
            error.Title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    [LoggerMessage(9001, LogLevel.Error, "Unhandled UI exception: {RedactedMessage}")]
    private static partial void LogUnhandledException(ILogger logger, string redactedMessage);

    [LoggerMessage(9002, LogLevel.Error, "Unable to persist UI exception diagnostics: {RedactedMessage}")]
    private static partial void LogDiagnosticWriteFailure(ILogger logger, string redactedMessage);
}
