using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LenxTool.Infrastructure.Networking;

/// <summary>
/// Eagle Web API V2 的最小能力快照。P2-12 只依赖 Windows 版 4.0
/// Build 21 及以上，不读取或暴露用户资源库路径。
/// </summary>
public sealed record EagleApiCapability(
    string Version,
    int BuildNumber,
    string LibraryRevision);

/// <summary>
/// 发送给 Eagle 的单个已验证图片。图片必须已经由 LenxTool 完成网络、
/// 类型、魔数与大小检查，因此这里只接受 data URI，不接受远程图片 URL。
/// </summary>
public sealed record EagleAddItem(
    string ItemId,
    string DataUri,
    string Name,
    string? Website,
    IReadOnlyList<string> Tags);

public enum EagleApiFailure
{
    Unavailable = 1,
    Incompatible = 2,
    Rejected = 3
}

/// <summary>
/// 封闭 Eagle 错误，不把第三方响应正文或本机资源库信息带出适配器边界。
/// </summary>
public sealed class EagleApiException : Exception
{
    public EagleApiException(
        EagleApiFailure failure,
        bool isRetryable,
        Exception? innerException = null)
        : base(CreateMessage(failure), innerException)
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }
        Failure = failure;
        IsRetryable = isRetryable;
    }

    public EagleApiFailure Failure { get; }
    public bool IsRetryable { get; }

    private static string CreateMessage(EagleApiFailure failure) =>
        failure switch
        {
            EagleApiFailure.Unavailable =>
                "Eagle 本机服务暂时不可用。",
            EagleApiFailure.Incompatible =>
                "Eagle 本机服务不支持所需的 Web API V2 能力。",
            EagleApiFailure.Rejected =>
                "Eagle 拒绝了图片导入请求。",
            _ => "Eagle 请求失败。"
        };
}

public interface IEagleApiClient
{
    Task<EagleApiCapability> ProbeAsync(
        Uri endpoint,
        CancellationToken cancellationToken);

    Task<bool> ExistsAsync(
        Uri endpoint,
        string itemId,
        CancellationToken cancellationToken);

    Task<string> AddAsync(
        Uri endpoint,
        EagleAddItem item,
        CancellationToken cancellationToken);
}

/// <summary>
/// 创建 Eagle 专用网络处理器。初始 URI 是 loopback 仍不代表重定向目标安全，
/// 因此必须在主处理器层禁用自动跳转、系统代理与 Cookie。
/// </summary>
public static class EagleHttpClientSecurity
{
    public static SocketsHttpHandler CreatePrimaryHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
}

/// <summary>
/// 仅连接用户显式配置的 loopback HTTP 端点。协议字段依据 Eagle 官方
/// Web API V2 的 app/info、library/info 与 item/add 契约实现。
/// </summary>
public sealed class EagleApiClient(IHttpClientFactory clients)
    : IEagleApiClient
{
    private const int MinimumBuildNumber = 21;
    private const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumDataUriLength = 17 * 1024 * 1024;
    private const int MaximumNameLength = 255;
    private const int MaximumTagCount = 32;
    private const int MaximumTagLength = 64;
    private const int LibraryRevisionLength = 24;
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly Version MinimumVersion = new(4, 0, 0);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            MaxDepth = 16
        };

    public async Task<EagleApiCapability> ProbeAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        Uri normalizedEndpoint = ValidateEndpoint(endpoint);
        using JsonDocument app = await SendForJsonAsync(
                HttpMethod.Get,
                new(normalizedEndpoint, "api/v2/app/info"),
                content: null,
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement appData = GetSuccessData(app);
        string versionText = GetRequiredString(appData, "version");
        string buildText = GetRequiredString(appData, "buildVersion");
        string platform = GetRequiredString(appData, "platform");
        if (!Version.TryParse(versionText, out Version? version)
            || !TryParseBuild(buildText, out int buildNumber)
            || !MeetsMinimumVersion(version, buildNumber)
            || !string.Equals(
                platform,
                "win32",
                StringComparison.Ordinal))
        {
            throw new EagleApiException(
                EagleApiFailure.Incompatible,
                isRetryable: false);
        }

        // 打开资源库是导入的必要条件；只验证官方必需字段，随后立即丢弃，
        // 不把用户资源库名称或路径保留到能力快照和异常中。
        using JsonDocument library = await SendForJsonAsync(
                HttpMethod.Get,
                new(normalizedEndpoint, "api/v2/library/info"),
                content: null,
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement libraryData = GetSuccessData(library);
        _ = GetRequiredString(libraryData, "name");
        string libraryPath = GetRequiredString(libraryData, "path");
        return new(
            version.ToString(),
            buildNumber,
            CreateLibraryRevision(libraryPath));
    }

    public Task<bool> ExistsAsync(
        Uri endpoint,
        string itemId,
        CancellationToken cancellationToken)
    {
        Uri normalizedEndpoint = ValidateEndpoint(endpoint);
        string normalizedItemId = ValidateItemId(itemId);
        return ExistsCoreAsync(
            normalizedEndpoint,
            normalizedItemId,
            cancellationToken);
    }

    public async Task<string> AddAsync(
        Uri endpoint,
        EagleAddItem item,
        CancellationToken cancellationToken)
    {
        Uri normalizedEndpoint = ValidateEndpoint(endpoint);
        EagleAddItem normalizedItem = ValidateItem(item);
        if (await ExistsCoreAsync(
                normalizedEndpoint,
                normalizedItem.ItemId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            // 稳定自定义 ID 是至少一次队列跨崩溃重放的幂等锚点。
            return normalizedItem.ItemId;
        }

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                id = normalizedItem.ItemId,
                base64 = normalizedItem.DataUri,
                name = normalizedItem.Name,
                website = normalizedItem.Website,
                tags = normalizedItem.Tags
            },
            JsonOptions);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json")
        {
            CharSet = "utf-8"
        };
        try
        {
            using JsonDocument response = await SendForJsonAsync(
                    HttpMethod.Post,
                    new(normalizedEndpoint, "api/v2/item/add"),
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            JsonElement data = GetSuccessData(response);
            string returnedId = GetRequiredString(data, "id");
            if (!string.Equals(
                    returnedId,
                    normalizedItem.ItemId,
                    StringComparison.Ordinal))
            {
                throw new EagleApiException(
                    EagleApiFailure.Incompatible,
                    isRetryable: false);
            }
            return returnedId;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is EagleApiException
                  or OperationCanceledException
                  or HttpRequestException
                  or IOException)
        {
            // POST 可能已在 Eagle 落库但响应丢失、损坏或被并发写入拒绝；
            // 用稳定 ID 做一次有界复查，存在即把至少一次投递收敛为成功。
            if (await TryReconcilePostAsync(
                    normalizedEndpoint,
                    normalizedItem.ItemId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return normalizedItem.ItemId;
            }
            throw;
        }
    }

    public static Uri ValidateEndpoint(Uri? endpoint)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || endpoint.Scheme != Uri.UriSchemeHttp
            || endpoint.IsDefaultPort
            || endpoint.Port is < 1 or > 65535
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath != "/"
            || !IPAddress.TryParse(
                endpoint.IdnHost,
                out IPAddress? address)
            || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "Eagle 端点必须是带显式端口的 loopback HTTP 根地址。",
                nameof(endpoint));
        }

        var builder = new UriBuilder(
            Uri.UriSchemeHttp,
            address.ToString(),
            endpoint.Port,
            "/");
        return builder.Uri;
    }

    private async Task<bool> ExistsCoreAsync(
        Uri endpoint,
        string itemId,
        CancellationToken cancellationToken)
    {
        string query = $"api/v2/item/get?id={Uri.EscapeDataString(itemId)}" +
                       "&fields=id&offset=0&limit=1";
        using JsonDocument response = await SendForJsonAsync(
                HttpMethod.Get,
                new(endpoint, query),
                content: null,
                cancellationToken)
            .ConfigureAwait(false);
        JsonElement data = GetSuccessData(response);
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("data", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new EagleApiException(
                EagleApiFailure.Incompatible,
                isRetryable: false);
        }
        foreach (JsonElement existing in items.EnumerateArray())
        {
            if (existing.ValueKind == JsonValueKind.Object
                && existing.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(
                    id.GetString(),
                    itemId,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryReconcilePostAsync(
        Uri endpoint,
        string itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExistsCoreAsync(
                    endpoint,
                    itemId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is EagleApiException
                  or OperationCanceledException
                  or HttpRequestException
                  or IOException)
        {
            // 复查失败意味着无法判断 POST 是否已经落库，必须保持可重试；
            // 不能沿用原 4xx/畸形响应把不确定状态永久化。
            throw new EagleApiException(
                EagleApiFailure.Unavailable,
                isRetryable: true);
        }
    }

    private async Task<JsonDocument> SendForJsonAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(method, uri)
        {
            Content = content
        };
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            HttpClient client = clients.CreateClient("LenxTool.Eagle");
            using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw MapStatus(response.StatusCode);
            }
            await using Stream stream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            byte[] json = await ReadBoundedAsync(stream, timeout.Token)
                .ConfigureAwait(false);
            try
            {
                return JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions
                    {
                        MaxDepth = JsonOptions.MaxDepth,
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow
                    });
            }
            catch (JsonException exception)
            {
                throw new EagleApiException(
                    EagleApiFailure.Incompatible,
                    isRetryable: false,
                    exception);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new EagleApiException(
                EagleApiFailure.Unavailable,
                isRetryable: true,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new EagleApiException(
                EagleApiFailure.Unavailable,
                isRetryable: true,
                exception);
        }
        catch (IOException exception)
        {
            throw new EagleApiException(
                EagleApiFailure.Unavailable,
                isRetryable: true,
                exception);
        }
    }

    private static EagleApiException MapStatus(
        HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.NotImplemented =>
                new(
                    EagleApiFailure.Incompatible,
                    isRetryable: false),
            HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests =>
                new(
                    EagleApiFailure.Unavailable,
                    isRetryable: true),
            >= HttpStatusCode.InternalServerError =>
                new(
                    EagleApiFailure.Unavailable,
                    isRetryable: true),
            _ => new(
                EagleApiFailure.Rejected,
                isRetryable: false)
        };

    private static JsonElement GetSuccessData(JsonDocument document)
    {
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.String
            || !string.Equals(
                status.GetString(),
                "success",
                StringComparison.Ordinal)
            || !root.TryGetProperty("data", out JsonElement data))
        {
            throw new EagleApiException(
                EagleApiFailure.Rejected,
                isRetryable: false);
        }
        return data;
    }

    private static string GetRequiredString(
        JsonElement value,
        string name)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new EagleApiException(
                EagleApiFailure.Incompatible,
                isRetryable: false);
        }
        return property.GetString()!;
    }

    private static bool TryParseBuild(
        string value,
        out int buildNumber)
    {
        const string Prefix = "build";
        buildNumber = 0;
        return value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(
                   value[Prefix.Length..],
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out buildNumber);
    }

    private static string CreateLibraryRevision(string libraryPath)
    {
        // Windows 路径大小写与分隔符不影响库身份；只把单向摘要带出探测边界，
        // 原始资源库名称和路径既不持久化，也不进入界面或异常。
        string canonicalPath = libraryPath
            .Trim()
            .Replace('/', '\\')
            .ToUpperInvariant();
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"eagle-library-v1\0{canonicalPath}"));
        return Convert.ToHexString(hash)
            .ToLowerInvariant()[..LibraryRevisionLength];
    }

    private static bool MeetsMinimumVersion(
        Version version,
        int buildNumber)
    {
        int comparison = version.CompareTo(MinimumVersion);
        return comparison > 0
               || (comparison == 0
                   && buildNumber >= MinimumBuildNumber);
    }

    private static EagleAddItem ValidateItem(EagleAddItem? item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _ = ValidateItemId(item.ItemId);
        string name = item.Name.Trim();
        if (name.Length is 0 or > MaximumNameLength
            || name.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Eagle 图片标题无效。",
                nameof(item));
        }
        if (item.DataUri.Length > MaximumDataUriLength
            || !(item.DataUri.StartsWith(
                     "data:image/png;base64,",
                     StringComparison.Ordinal)
                 || item.DataUri.StartsWith(
                     "data:image/jpeg;base64,",
                     StringComparison.Ordinal)
                 || item.DataUri.StartsWith(
                     "data:image/gif;base64,",
                     StringComparison.Ordinal)
                 || item.DataUri.StartsWith(
                     "data:image/webp;base64,",
                     StringComparison.Ordinal)
                 || item.DataUri.StartsWith(
                     "data:image/bmp;base64,",
                     StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Eagle 图片 data URI 无效或过大。",
                nameof(item));
        }
        if (item.Website is { } website
            && (website.Length > 2048
                || !Uri.TryCreate(
                    website,
                    UriKind.Absolute,
                    out Uri? source)
                || source.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(source.UserInfo)))
        {
            throw new ArgumentException(
                "Eagle 图片来源地址无效。",
                nameof(item));
        }
        ArgumentNullException.ThrowIfNull(item.Tags);
        if (item.Tags.Count > MaximumTagCount
            || item.Tags.Any(tag =>
                string.IsNullOrWhiteSpace(tag)
                || tag.Length > MaximumTagLength
                || tag.Any(char.IsControl)
                || !string.Equals(
                    tag,
                    tag.Trim(),
                    StringComparison.Ordinal))
            || item.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != item.Tags.Count)
        {
            throw new ArgumentException(
                "Eagle 图片标签无效。",
                nameof(item));
        }
        return item with
        {
            Name = name,
            Tags = Array.AsReadOnly(item.Tags.ToArray())
        };
    }

    private static string ValidateItemId(string? itemId)
    {
        if (itemId is null
            || itemId.Length != 32
            || !itemId.StartsWith("LT", StringComparison.Ordinal)
            || itemId[2..].Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Eagle 稳定条目标识无效。",
                nameof(itemId));
        }
        return itemId;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaximumResponseBytes)
            {
                throw new EagleApiException(
                    EagleApiFailure.Incompatible,
                    isRetryable: false);
            }
            output.Write(buffer, 0, read);
        }
    }
}
