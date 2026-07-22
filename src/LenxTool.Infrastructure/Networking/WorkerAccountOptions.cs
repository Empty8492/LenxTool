namespace LenxTool.Infrastructure.Networking;

public sealed class WorkerAccountOptions
{
    public WorkerAccountOptions(Uri? baseAddress)
    {
        if (baseAddress is not null)
        {
            if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Worker 地址必须是绝对 HTTPS URL。", nameof(baseAddress));
            if (!string.IsNullOrEmpty(baseAddress.UserInfo)
                || !string.IsNullOrEmpty(baseAddress.Query)
                || !string.IsNullOrEmpty(baseAddress.Fragment))
                throw new ArgumentException("Worker 地址不能包含凭据、查询参数或片段。", nameof(baseAddress));
        }

        BaseAddress = baseAddress;
    }

    public Uri? BaseAddress { get; }
}
