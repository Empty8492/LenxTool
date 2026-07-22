using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class FeedCatalogSyncService : IFeedCatalogSyncService, IDisposable
{
    private const int MaximumCatalogResponseBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WorkerAccountSessionService _accountSession;
    private readonly IFeedCatalogRepository _repository;
    private readonly FeedCatalogSyncOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _synchronizationGate = new(1, 1);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _statusGate = new();
    private FeedCatalogSyncStatus _current = new(
        false,
        0,
        FeedCatalogScope.Active,
        null,
        true,
        0,
        null,
        null);
    private Task? _backgroundTask;
    private bool _initialized;
    private bool _disposed;

    public FeedCatalogSyncService(
        WorkerAccountSessionService accountSession,
        IFeedCatalogRepository repository,
        FeedCatalogSyncOptions options,
        TimeProvider timeProvider)
    {
        _accountSession = accountSession;
        _repository = repository;
        _options = ValidateOptions(options);
        _timeProvider = timeProvider;
    }

    public FeedCatalogSyncStatus Current
    {
        get
        {
            lock (_statusGate) return _current;
        }
    }

    public event EventHandler<FeedCatalogSyncStatusChangedEventArgs>? StatusChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            FeedCatalogState local = await _repository.GetStateAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            Publish(new(
                false,
                local.Version,
                local.Scope,
                local.LastSyncedAt,
                IsExpired(local.LastSyncedAt, now),
                0,
                null,
                null));
            _accountSession.SessionChanged += OnAccountSessionChanged;
            _initialized = true;

            if (_accountSession.Current.IsAuthenticated)
            {
                try
                {
                    await SyncAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (AppException)
                {
                    // Startup remains usable offline; Current retains the failure and retry schedule.
                }
                catch
                {
                    _accountSession.SessionChanged -= OnAccountSessionChanged;
                    _initialized = false;
                    throw;
                }
            }

            _backgroundTask = RunBackgroundAsync(_shutdown.Token);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<FeedCatalogSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_accountSession.Current.IsAuthenticated)
        {
            FeedCatalogSyncStatus status = Current;
            return new(
                FeedCatalogSyncOutcome.SkippedNotAuthenticated,
                status.Version,
                status.LastSynchronizedAt);
        }

        await _synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FeedCatalogSyncStatus before = Current;
        try
        {
            FeedCatalogState local = await _repository.GetStateAsync(cancellationToken).ConfigureAwait(false);
            FeedCatalogScope requestedScope = _accountSession.Current.IsAdmin
                ? FeedCatalogScope.All
                : FeedCatalogScope.Active;
            long afterVersion = requestedScope == FeedCatalogScope.All && local.Scope != FeedCatalogScope.All
                ? 0
                : local.Version;
            Publish(before with
            {
                IsSynchronizing = true,
                Version = local.Version,
                Scope = local.Scope,
                LastSynchronizedAt = local.LastSyncedAt,
                Error = null
            });

            string scopeValue = requestedScope == FeedCatalogScope.All ? "ALL" : "ACTIVE";
            string path = string.Create(
                CultureInfo.InvariantCulture,
                $"/v1/feeds/catalog?afterVersion={afterVersion}&scope={scopeValue}");
            using HttpResponseMessage response = await _accountSession
                .GetAuthorizedAsync(path, cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset synchronizedAt = _timeProvider.GetUtcNow();

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (afterVersion == 0) throw CreateInvalidCatalogException();
                await _repository.MarkSynchronizedAsync(local.Version, synchronizedAt, cancellationToken)
                    .ConfigureAwait(false);
                PublishSuccess(local with { LastSyncedAt = synchronizedAt }, synchronizedAt);
                return new(FeedCatalogSyncOutcome.Unchanged, local.Version, synchronizedAt);
            }

            await WorkerAccountSessionService.EnsureSuccessAsync(response, cancellationToken)
                .ConfigureAwait(false);
            FeedCatalogSnapshot snapshot = await ReadSnapshotAsync(
                response,
                requestedScope,
                local.Version,
                synchronizedAt,
                cancellationToken).ConfigureAwait(false);
            await _repository.ReplaceAsync(snapshot, cancellationToken).ConfigureAwait(false);
            PublishSuccess(snapshot.State, synchronizedAt);
            return new(FeedCatalogSyncOutcome.Updated, snapshot.State.Version, synchronizedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(Current with { IsSynchronizing = false });
            throw;
        }
        catch (AppException exception)
        {
            PublishFailure(exception.Error);
            throw;
        }
        catch (Exception)
        {
            Publish(Current with { IsSynchronizing = false });
            throw;
        }
        finally
        {
            _synchronizationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accountSession.SessionChanged -= OnAccountSessionChanged;
        _shutdown.Cancel();
        SignalBackground();
    }

    private async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_accountSession.Current.IsAuthenticated)
                {
                    await _wakeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TimeSpan delay = DelayUntilNextAttempt();
                await _wakeSignal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                if (!_accountSession.Current.IsAuthenticated) continue;

                try
                {
                    await SyncAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (AppException)
                {
                    // SyncAsync records a bounded error and the next exponential-backoff attempt.
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or
                    IOException or
                    UnauthorizedAccessException or
                    Microsoft.Data.Sqlite.SqliteException)
                {
                    PublishFailure(CreateLocalCatalogException().Error);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    private TimeSpan DelayUntilNextAttempt()
    {
        FeedCatalogSyncStatus status = Current;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset next = status.NextAttemptAt ?? now.Add(_options.SynchronizationInterval);
        return next <= now ? TimeSpan.Zero : next - now;
    }

    private void OnAccountSessionChanged(object? sender, AccountSessionChangedEventArgs eventArgs)
    {
        if (eventArgs.Session.IsAuthenticated)
        {
            Publish(Current with { NextAttemptAt = _timeProvider.GetUtcNow() });
        }
        else
        {
            Publish(Current with { IsSynchronizing = false });
        }
        SignalBackground();
    }

    private void SignalBackground()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0) _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void PublishSuccess(FeedCatalogState state, DateTimeOffset synchronizedAt) => Publish(new(
        false,
        state.Version,
        state.Scope,
        synchronizedAt,
        false,
        0,
        synchronizedAt.Add(_options.SynchronizationInterval),
        null));

    private void PublishFailure(AppError error)
    {
        FeedCatalogSyncStatus status = Current;
        int failures = Math.Min(status.ConsecutiveFailures + 1, 31);
        TimeSpan retryDelay = CalculateRetryDelay(failures);
        Publish(status with
        {
            IsSynchronizing = false,
            IsStale = true,
            ConsecutiveFailures = failures,
            NextAttemptAt = _timeProvider.GetUtcNow().Add(retryDelay),
            Error = error with { TechnicalDetails = null }
        });
    }

    private TimeSpan CalculateRetryDelay(int consecutiveFailures)
    {
        double multiplier = Math.Pow(2, Math.Min(consecutiveFailures - 1, 20));
        double ticks = Math.Min(
            _options.InitialRetryDelay.Ticks * multiplier,
            _options.MaximumRetryDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private bool IsExpired(DateTimeOffset? lastSynchronizedAt, DateTimeOffset now) =>
        lastSynchronizedAt is null || now - lastSynchronizedAt > _options.StaleAfter;

    private void Publish(FeedCatalogSyncStatus status)
    {
        lock (_statusGate) _current = status;
        StatusChanged?.Invoke(this, new(status));
    }

    private static async Task<FeedCatalogSnapshot> ReadSnapshotAsync(
        HttpResponseMessage response,
        FeedCatalogScope expectedScope,
        long localVersion,
        DateTimeOffset synchronizedAt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw CreateInvalidCatalogException();
        }
        byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        CatalogSnapshotDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<CatalogSnapshotDto>(payload, JsonOptions)
                ?? throw CreateInvalidCatalogException();
        }
        catch (JsonException exception)
        {
            throw new AppException(CreateInvalidCatalogException().Error, exception);
        }

        try
        {
            return MapSnapshot(dto, expectedScope, localVersion, synchronizedAt);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new AppException(CreateInvalidCatalogException().Error, exception);
        }
    }

    private static FeedCatalogSnapshot MapSnapshot(
        CatalogSnapshotDto dto,
        FeedCatalogScope expectedScope,
        long localVersion,
        DateTimeOffset synchronizedAt)
    {
        FeedCatalogScope scope = dto.Scope switch
        {
            "ACTIVE" => FeedCatalogScope.Active,
            "ALL" => FeedCatalogScope.All,
            _ => throw CreateInvalidCatalogException()
        };
        if (scope != expectedScope
            || dto.CatalogVersion < localVersion
            || dto.CatalogVersion < 0
            || dto.GeneratedAt is null
            || dto.GeneratedAt.Value.Offset != TimeSpan.Zero
            || dto.Categories is null
            || dto.Feeds is null
            || dto.Categories.Count > 200
            || dto.Feeds.Count > 5000)
        {
            throw CreateInvalidCatalogException();
        }

        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        var categories = new List<FeedCategory>(dto.Categories.Count);
        foreach (CatalogCategoryDto category in dto.Categories)
        {
            ValidateGuid(category.Id);
            ValidateText(category.Name, 80);
            ValidateVersion(category.Version, dto.CatalogVersion);
            ValidateSortOrder(category.SortOrder);
            ValidateTimestamps(category.CreatedAt, category.UpdatedAt);
            if (!categoryIds.Add(category.Id!) || (scope == FeedCatalogScope.Active && !category.IsEnabled))
                throw CreateInvalidCatalogException();

            string name = category.Name!;
            categories.Add(new(
                category.Id!,
                name,
                name.Normalize(NormalizationForm.FormKC).ToLowerInvariant(),
                category.SortOrder,
                category.IsEnabled,
                category.Version,
                category.CreatedAt!.Value,
                category.UpdatedAt!.Value));
        }

        var feedIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedUrls = new HashSet<string>(StringComparer.Ordinal);
        var feeds = new List<FeedCatalogItem>(dto.Feeds.Count);
        foreach (CatalogFeedDto feed in dto.Feeds)
        {
            ValidateGuid(feed.Id);
            ValidateText(feed.DisplayName, 160);
            ValidateHttpsUrl(feed.OriginalUrl);
            ValidateHttpsUrl(feed.NormalizedUrl);
            if (feed.SiteUrl is not null) ValidateHttpsUrl(feed.SiteUrl);
            if (feed.CategoryId is not null && !categoryIds.Contains(feed.CategoryId))
                throw CreateInvalidCatalogException();
            ValidateVersion(feed.Version, dto.CatalogVersion);
            ValidateSortOrder(feed.SortOrder);
            ValidateTimestamps(feed.CreatedAt, feed.UpdatedAt);
            if (feed.RefreshIntervalMinutes is < 5 or > 1440
                || !feedIds.Add(feed.Id!)
                || !normalizedUrls.Add(feed.NormalizedUrl!)
                || (scope == FeedCatalogScope.Active && !feed.IsEnabled))
            {
                throw CreateInvalidCatalogException();
            }

            FeedViewKind viewKind = feed.ViewKind switch
            {
                "ARTICLE" => FeedViewKind.Article,
                "PICTURE" => FeedViewKind.Picture,
                "AUDIO" => FeedViewKind.Audio,
                "VIDEO" => FeedViewKind.Video,
                "NOTIFICATION" => FeedViewKind.Notification,
                _ => throw CreateInvalidCatalogException()
            };
            feeds.Add(new(
                feed.Id!,
                feed.OriginalUrl!,
                feed.NormalizedUrl!,
                feed.DisplayName!,
                feed.SiteUrl,
                feed.CategoryId,
                viewKind,
                feed.RefreshIntervalMinutes,
                feed.SortOrder,
                feed.IsEnabled,
                feed.Version,
                feed.CreatedAt!.Value,
                feed.UpdatedAt!.Value));
        }

        return new(
            new(dto.CatalogVersion, scope, dto.GeneratedAt, synchronizedAt),
            categories,
            feeds);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumCatalogResponseBytes)
            throw CreateInvalidCatalogException();

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumCatalogResponseBytes) throw CreateInvalidCatalogException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void ValidateGuid(string? value)
    {
        if (!Guid.TryParseExact(value, "D", out _)) throw CreateInvalidCatalogException();
    }

    private static void ValidateText(string? value, int maximumCodePoints)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.EnumerateRunes().Count() > maximumCodePoints
            || value.Any(char.IsControl))
        {
            throw CreateInvalidCatalogException();
        }
    }

    private static void ValidateHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw CreateInvalidCatalogException();
        }
    }

    private static void ValidateVersion(long version, long catalogVersion)
    {
        if (version < 0 || version > catalogVersion) throw CreateInvalidCatalogException();
    }

    private static void ValidateSortOrder(int sortOrder)
    {
        if (sortOrder is < 0 or > 1_000_000) throw CreateInvalidCatalogException();
    }

    private static void ValidateTimestamps(DateTimeOffset? createdAt, DateTimeOffset? updatedAt)
    {
        if (createdAt is null
            || updatedAt is null
            || createdAt.Value.Offset != TimeSpan.Zero
            || updatedAt.Value.Offset != TimeSpan.Zero
            || updatedAt < createdAt)
            throw CreateInvalidCatalogException();
    }

    private static FeedCatalogSyncOptions ValidateOptions(FeedCatalogSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SynchronizationInterval <= TimeSpan.Zero
            || options.InitialRetryDelay <= TimeSpan.Zero
            || options.MaximumRetryDelay < options.InitialRetryDelay
            || options.StaleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return options;
    }

    private static AppException CreateInvalidCatalogException() => new(new(
        AppErrorCode.ProviderUnavailable,
        "共享目录响应无效",
        "云服务返回了无法安全应用的共享目录。",
        "已保留上次成功目录；请稍后重试或联系管理员检查 Worker 版本。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private static AppException CreateLocalCatalogException() => new(new(
        AppErrorCode.ProviderUnavailable,
        "共享目录暂时不可用",
        "共享目录未能安全写入本地缓存。",
        "已保留上次成功目录，应用稍后会自动重试。",
        Provider: "本地目录缓存",
        IsRetryable: true));

    private sealed class CatalogSnapshotDto
    {
        public long CatalogVersion { get; init; }
        public string? Scope { get; init; }
        public DateTimeOffset? GeneratedAt { get; init; }
        public List<CatalogCategoryDto>? Categories { get; init; }
        public List<CatalogFeedDto>? Feeds { get; init; }
    }

    private sealed class CatalogCategoryDto
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public int SortOrder { get; init; }
        public bool IsEnabled { get; init; }
        public long Version { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private sealed class CatalogFeedDto
    {
        public string? Id { get; init; }
        public string? OriginalUrl { get; init; }
        public string? NormalizedUrl { get; init; }
        public string? DisplayName { get; init; }
        public string? SiteUrl { get; init; }
        public string? CategoryId { get; init; }
        public string? ViewKind { get; init; }
        public int RefreshIntervalMinutes { get; init; }
        public int SortOrder { get; init; }
        public bool IsEnabled { get; init; }
        public long Version { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }
}
