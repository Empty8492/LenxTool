using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LenxTool.Core.Accounts;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;

namespace LenxTool.Infrastructure.Networking;

public sealed class WorkerAccountSessionService : IAccountSessionService, IDisposable
{
    private const string RefreshTokenSecretName = "account_refresh_token";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretStore _secretStore;
    private readonly WorkerAccountOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private AccountSessionSnapshot _current = AccountSessionSnapshot.SignedOut;
    private TokenState? _tokens;
    private long _generation;
    private bool _disposed;

    public WorkerAccountSessionService(
        IHttpClientFactory httpClientFactory,
        ISecretStore secretStore,
        WorkerAccountOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _secretStore = secretStore;
        _options = options;
    }

    public bool IsConfigured => _options.BaseAddress is not null;

    public AccountSessionSnapshot Current
    {
        get
        {
            lock (_stateGate) return _current;
        }
    }

    public event EventHandler<AccountSessionChangedEventArgs>? SessionChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? refreshToken = await _secretStore.GetAsync(RefreshTokenSecretName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            SetCurrent(AccountSessionSnapshot.SignedOut);
            return;
        }
        if (!IsConfigured)
        {
            SetCurrent(AccountSessionSnapshot.SignedOut);
            return;
        }

        try
        {
            TokenPairDto pair = await RequestTokenRefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            await SaveTokensAsync(pair, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AppException exception) when (IsAuthenticationFailure(exception))
        {
            await ExpireAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureConfigured();
        string normalizedUsername = ValidateUsername(username);
        ValidatePassword(password);

        using HttpResponseMessage response = await SendAsync(
            () => CreateJsonRequest(HttpMethod.Post, "/v1/auth/login", new { username = normalizedUsername, password }),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        LoginResponseDto payload = await ReadJsonAsync<LoginResponseDto>(response, cancellationToken).ConfigureAwait(false);
        TokenPairDto pair = ValidateTokenPair(payload.AccessToken, payload.RefreshToken, payload.ExpiresInSeconds);
        AccountSessionSnapshot session = MapSession(payload.User, payload.Quota);
        await SaveTokensAsync(pair, cancellationToken).ConfigureAwait(false);
        SetCurrent(session);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using HttpResponseMessage response = await SendAuthorizedAsync(
            token => CreateAuthorizedRequest(HttpMethod.Get, "/v1/me", token.AccessToken),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        MeResponseDto payload = await ReadJsonAsync<MeResponseDto>(response, cancellationToken).ConfigureAwait(false);
        SetCurrent(MapSession(payload.User, payload.Quota));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (GetTokens() is not null && IsConfigured)
            {
                using HttpResponseMessage response = await SendAuthorizedAsync(
                    token => CreateAuthorizedJsonRequest(
                        HttpMethod.Post,
                        "/v1/auth/logout",
                        token.AccessToken,
                        new { refreshToken = token.RefreshToken }),
                    cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AppException)
        {
            // Local logout is authoritative for this device even when the Worker is offline.
        }
        finally
        {
            await ClearTokensAsync(AccountSessionSnapshot.SignedOut, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _refreshGate.Dispose();
        _disposed = true;
    }

    internal Task<HttpResponseMessage> GetAuthorizedAsync(
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(pathAndQuery)
            || pathAndQuery.Length > 2048
            || pathAndQuery[0] != '/'
            || pathAndQuery.StartsWith("//", StringComparison.Ordinal)
            || pathAndQuery.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("The Worker path must be a bounded origin-relative path.", nameof(pathAndQuery));
        }

        return SendAuthorizedAsync(
            token => CreateAuthorizedRequest(HttpMethod.Get, pathAndQuery, token.AccessToken),
            cancellationToken);
    }

    internal Task<HttpResponseMessage> SendCatalogMutationAsync(
        HttpMethod method,
        string path,
        long expectedCatalogVersion,
        object? payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (method != HttpMethod.Post && method != HttpMethod.Patch && method != HttpMethod.Delete)
            throw new ArgumentException("Catalog mutations only support POST, PATCH, or DELETE.", nameof(method));
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 256
            || !path.StartsWith("/v1/admin/", StringComparison.Ordinal)
            || path.Contains('?', StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
            || expectedCatalogVersion is < 0 or > 9_007_199_254_740_991)
        {
            throw new ArgumentException("The catalog mutation path or version is invalid.", nameof(path));
        }

        string idempotencyKey = Guid.NewGuid().ToString("N");
        return SendAuthorizedAsync(token =>
        {
            HttpRequestMessage request = CreateAuthorizedRequest(method, path, token.AccessToken);
            request.Headers.TryAddWithoutValidation(
                "If-Match",
                $"\"catalog-all-{expectedCatalogVersion}\"");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (payload is not null) request.Content = JsonContent.Create(payload);
            return request;
        }, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<TokenState, HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        TokenState failedToken = GetTokens() ?? throw CreateSignedOutException();
        HttpResponseMessage response = await SendAsync(() => requestFactory(failedToken), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();

        await EnsureRefreshedAsync(failedToken.Generation, cancellationToken).ConfigureAwait(false);
        TokenState refreshedToken = GetTokens() ?? throw CreateSignedOutException();
        HttpResponseMessage replay = await SendAsync(() => requestFactory(refreshedToken), cancellationToken)
            .ConfigureAwait(false);
        if (replay.StatusCode != HttpStatusCode.Unauthorized) return replay;
        replay.Dispose();

        await ExpireAsync(CancellationToken.None).ConfigureAwait(false);
        throw CreateExpiredException();
    }

    private async Task EnsureRefreshedAsync(long failedGeneration, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TokenState current = GetTokens() ?? throw CreateSignedOutException();
            if (current.Generation != failedGeneration) return;

            try
            {
                TokenPairDto pair = await RequestTokenRefreshAsync(current.RefreshToken, cancellationToken)
                    .ConfigureAwait(false);
                await SaveTokensAsync(pair, cancellationToken).ConfigureAwait(false);
            }
            catch (AppException exception) when (IsAuthenticationFailure(exception))
            {
                await ExpireAsync(CancellationToken.None).ConfigureAwait(false);
                throw CreateExpiredException();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<TokenPairDto> RequestTokenRefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            () => CreateJsonRequest(HttpMethod.Post, "/v1/auth/refresh", new { refreshToken }),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        TokenPairDto payload = await ReadJsonAsync<TokenPairDto>(response, cancellationToken).ConfigureAwait(false);
        return ValidateTokenPair(payload.AccessToken, payload.RefreshToken, payload.ExpiresInSeconds);
    }

    private async Task SaveTokensAsync(TokenPairDto pair, CancellationToken cancellationToken)
    {
        await _secretStore.SetAsync(RefreshTokenSecretName, pair.RefreshToken!, cancellationToken)
            .ConfigureAwait(false);
        lock (_stateGate)
        {
            _generation++;
            _tokens = new(pair.AccessToken!, pair.RefreshToken!, _generation);
        }
    }

    private async Task ExpireAsync(CancellationToken cancellationToken)
    {
        await ClearTokensAsync(AccountSessionSnapshot.Expired, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearTokensAsync(
        AccountSessionSnapshot clearedSession,
        CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            _generation++;
            _tokens = null;
        }
        SetCurrent(clearedSession);
        await _secretStore.DeleteAsync(RefreshTokenSecretName, cancellationToken).ConfigureAwait(false);
    }

    private TokenState? GetTokens()
    {
        lock (_stateGate) return _tokens;
    }

    private void SetCurrent(AccountSessionSnapshot session)
    {
        lock (_stateGate) _current = session;
        SessionChanged?.Invoke(this, new(session));
    }

    private HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, object payload)
    {
        EnsureConfigured();
        return new(method, new Uri(_options.BaseAddress!, path)) { Content = JsonContent.Create(payload) };
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, string accessToken)
    {
        EnsureConfigured();
        var request = new HttpRequestMessage(method, new Uri(_options.BaseAddress!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private HttpRequestMessage CreateAuthorizedJsonRequest(
        HttpMethod method,
        string path,
        string accessToken,
        object payload)
    {
        HttpRequestMessage request = CreateAuthorizedRequest(method, path, accessToken);
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = _httpClientFactory.CreateClient("LenxTool.Account");
            using HttpRequestMessage request = requestFactory();
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException exception)
        {
            throw new AppException(AppErrorFactory.FromTimeout("LenxTool Worker"), exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AppException(AppErrorFactory.FromNetwork("LenxTool Worker"), exception);
        }
    }

    internal static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        WorkerErrorEnvelope? envelope = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        string? code = Bound(envelope?.Error?.Code, 64);
        string? requestId = Bound(
            response.Headers.TryGetValues("x-request-id", out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : envelope?.Error?.RequestId,
            128);
        AppError mapped = AppErrorFactory.FromHttp(
            response.StatusCode,
            "LenxTool Worker",
            requestId,
            string.IsNullOrWhiteSpace(code) ? null : $"Worker error code: {code}");
        throw new AppException(mapped);
    }

    internal static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw CreateInvalidResponseException();
        }
        catch (JsonException exception)
        {
            throw new AppException(CreateInvalidResponseException().Error, exception);
        }
    }

    private static async Task<WorkerErrorEnvelope?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<WorkerErrorEnvelope>(bytes, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or AppException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) throw CreateInvalidResponseException();
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaximumResponseBytes) throw CreateInvalidResponseException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static AccountSessionSnapshot MapSession(PublicUserDto? user, AccountQuotaDto? quota)
    {
        if (user is null || quota is null
            || !Guid.TryParse(user.Id, out _)
            || string.IsNullOrWhiteSpace(user.Username)
            || user.Username.Length > 160
            || !DateOnly.TryParseExact(quota.Date, "yyyy-MM-dd", out DateOnly date))
            throw CreateInvalidResponseException();
        AccountRole role = user.Role switch
        {
            "USER" => AccountRole.User,
            "ADMIN" => AccountRole.Admin,
            _ => throw CreateInvalidResponseException()
        };
        return new(
            AccountSessionStatus.SignedIn,
            new(user.Id, user.Username, role),
            new(date, MapQuota(quota.Ai), MapQuota(quota.SpeechSeconds)));
    }

    private static AccountQuotaCounter MapQuota(AccountQuotaCounterDto? value)
    {
        if (value is null
            || value.Limit < 0
            || value.Used < 0
            || value.Reserved < 0
            || value.Remaining < 0
            || value.Remaining != Math.Max(0, value.Limit - value.Used - value.Reserved))
            throw CreateInvalidResponseException();
        return new(value.Limit, value.Used, value.Reserved, value.Remaining);
    }

    private static TokenPairDto ValidateTokenPair(string? accessToken, string? refreshToken, int expiresInSeconds)
    {
        if (string.IsNullOrWhiteSpace(accessToken)
            || Encoding.UTF8.GetByteCount(accessToken) > 4096
            || string.IsNullOrWhiteSpace(refreshToken)
            || Encoding.UTF8.GetByteCount(refreshToken) > 512
            || expiresInSeconds is <= 0 or > 86400)
            throw CreateInvalidResponseException();
        return new() { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresInSeconds = expiresInSeconds };
    }

    private void EnsureConfigured()
    {
        if (IsConfigured) return;
        throw new AppException(new(
            AppErrorCode.InvalidRequest,
            "云服务尚未配置",
            "当前版本没有可用的 Worker 服务地址。",
            "请设置 LENXTOOL_WORKER_BASE_URL 为部署后的 HTTPS 地址，再重新启动应用。",
            Provider: "LenxTool Worker"));
    }

    private static string ValidateUsername(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        string normalized = username.Normalize(NormalizationForm.FormKC).Trim();
        int length = normalized.EnumerateRunes().Count();
        if (length is < 3 or > 40) throw new AppException(new(
            AppErrorCode.InvalidRequest,
            "账号格式无效",
            "用户名长度必须为 3～40 个字符。",
            "请检查用户名后重试。",
            Provider: "LenxTool Worker"));
        return normalized;
    }

    private static void ValidatePassword(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        int length = password.EnumerateRunes().Count();
        if (length is < 1 or > 128) throw new AppException(new(
            AppErrorCode.InvalidRequest,
            "密码格式无效",
            "密码长度必须为 1～128 个字符。",
            "请检查密码后重试。",
            Provider: "LenxTool Worker"));
    }

    private static bool IsAuthenticationFailure(AppException exception) =>
        exception.Error.Code is AppErrorCode.CredentialsInvalid or AppErrorCode.AccessDenied;

    private static string? Bound(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, maximumLength)];

    private static AppException CreateInvalidResponseException() => new(new(
        AppErrorCode.ProviderUnavailable,
        "云服务响应无效",
        "账号服务返回了无法安全解析的数据。",
        "请稍后重试；若持续发生，请联系管理员检查 Worker 版本。",
        Provider: "LenxTool Worker",
        IsRetryable: true));

    private static AppException CreateSignedOutException() => new(new(
        AppErrorCode.CredentialsInvalid,
        "尚未登录",
        "当前没有可用的账号会话。",
        "请在设置中登录后重试。",
        Provider: "LenxTool Worker"));

    private static AppException CreateExpiredException() => new(new(
        AppErrorCode.CredentialsInvalid,
        "登录已过期",
        "账号会话已经失效或被撤销。",
        "请在设置中重新登录。",
        Provider: "LenxTool Worker"));

    private sealed record TokenState(string AccessToken, string RefreshToken, long Generation);

    private class TokenPairDto
    {
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public int ExpiresInSeconds { get; init; }
    }

    private sealed class LoginResponseDto : TokenPairDto
    {
        public PublicUserDto? User { get; init; }
        public AccountQuotaDto? Quota { get; init; }
    }

    private sealed class MeResponseDto
    {
        public PublicUserDto? User { get; init; }
        public AccountQuotaDto? Quota { get; init; }
    }

    private sealed class PublicUserDto
    {
        public string? Id { get; init; }
        public string? Username { get; init; }
        public string? Role { get; init; }
    }

    private sealed class AccountQuotaDto
    {
        public string? Date { get; init; }
        public AccountQuotaCounterDto? Ai { get; init; }
        public AccountQuotaCounterDto? SpeechSeconds { get; init; }
    }

    private sealed class AccountQuotaCounterDto
    {
        public int Limit { get; init; }
        public int Used { get; init; }
        public int Reserved { get; init; }
        public int Remaining { get; init; }
    }

    private sealed class WorkerErrorEnvelope
    {
        public WorkerErrorDto? Error { get; init; }
    }

    private sealed class WorkerErrorDto
    {
        public string? Code { get; init; }
        public string? RequestId { get; init; }
    }
}
