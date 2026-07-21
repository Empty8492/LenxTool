using LenxTool.Core.Models;

namespace LenxTool.Core.Contracts;

public interface ISubtitleTranslator
{
    /// <summary>
    /// 按批次返回已完成的译文。调用方应在继续枚举前持久化每个批次；失败时实现应抛出
    /// <see cref="SubtitleTranslationException"/> 并携带可安全重试的恢复位置。
    /// </summary>
    IAsyncEnumerable<SubtitleTranslationBatchResult> TranslateAsync(
        SubtitleTranslationRequest request,
        CancellationToken cancellationToken);
}
