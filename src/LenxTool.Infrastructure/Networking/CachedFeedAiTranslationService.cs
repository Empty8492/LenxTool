using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Models;

namespace LenxTool.Infrastructure.Networking;

public sealed class CachedFeedAiTranslationService : IFeedAiTranslationService, IDisposable
{
    internal const string InProgressErrorCode = "TranslationInProgress";
    private const int EnvelopeVersion = 1;
    private const int MaximumCacheCharacters = 2_000_000;
    private const int MaximumTranslatedBlockCharacters = 100_000;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };
    private readonly ISubtitleTranslator _translator;
    private readonly IFeedAiResultRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly FeedAiTranslationOptions _options;
    private readonly object _keyLocksSync = new();
    private readonly Dictionary<FeedAiCacheKey, KeyLock> _keyLocks = [];
    private bool _disposed;

    public CachedFeedAiTranslationService(
        ISubtitleTranslator translator,
        IFeedAiResultRepository repository,
        TimeProvider timeProvider,
        FeedAiTranslationOptions options)
    {
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
    }

    public async Task<FeedAiTranslationResult> TranslateAsync(
        FeedAiTranslationInput input,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FeedAiTranslationInput snapshot = ValidateAndSnapshot(input);
        FeedAiCacheKey key = CreateKey(snapshot);
        FeedAiResult? cached = await _repository.GetCurrentAsync(key, cancellationToken)
            .ConfigureAwait(false);
        if (TryCreateCompletedResult(snapshot, cached, out FeedAiTranslationResult? result))
            return result;

        using KeyLockLease lease = await AcquireKeyLockAsync(key, cancellationToken)
            .ConfigureAwait(false);
        cached = await _repository.GetCurrentAsync(key, cancellationToken)
            .ConfigureAwait(false);
        if (TryCreateCompletedResult(snapshot, cached, out result))
            return result;

        return await TranslateAndPersistAsync(snapshot, key, cached, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_keyLocksSync)
        {
            _disposed = true;
            if (_keyLocks.Count == 0) return;
        }
    }

    private async Task<FeedAiTranslationResult> TranslateAndPersistAsync(
        FeedAiTranslationInput input,
        FeedAiCacheKey key,
        FeedAiResult? previous,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        PersistedEnvelope envelope = TryReadEnvelope(input, previous, out PersistedEnvelope? saved)
            ? saved
            : CreateEmptyEnvelope(key);
        int requestCount = previous?.RequestCount ?? 0;
        int promptTokens = previous?.PromptTokens ?? 0;
        int completionTokens = previous?.CompletionTokens ?? 0;
        int totalTokens = previous?.TotalTokens ?? 0;
        long previousDuration = previous?.DurationMilliseconds ?? 0;
        DateTimeOffset createdAt = previous?.CreatedAt ?? startedAt;
        string resultId = previous?.Id ?? CreateResultId(key);

        SubtitleSegment[] segments = input.Blocks
            .Select(block => new SubtitleSegment(TimeSpan.Zero, TimeSpan.Zero, block.Text)
            {
                Sequence = block.Sequence
            })
            .ToArray();
        var checkpoint = new SubtitleTranslationCheckpoint(
            envelope.OperationId,
            envelope.NextBlockIndex);
        SubtitleTranslationRequest request = SubtitleTranslationRequest.Create(
            envelope.OperationId,
            $"feed-entry:{input.EntryId}",
            input.TargetLanguage,
            _options.Model,
            _options.BatchSize,
            segments,
            checkpoint);

        try
        {
            await foreach (SubtitleTranslationBatchResult batch in
                           _translator.TranslateAsync(request, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                PersistedTranslation[] normalized = ValidateAndNormalizeBatch(
                    input,
                    envelope,
                    batch);
                requestCount = checked(requestCount + batch.RequestCount);
                promptTokens = checked(promptTokens + batch.TokenUsage.PromptTokens);
                completionTokens = checked(
                    completionTokens + batch.TokenUsage.CompletionTokens);
                totalTokens = checked(totalTokens + batch.TokenUsage.TotalTokens);
                PersistedEnvelope nextEnvelope = envelope with
                {
                    NextBlockIndex = batch.ResumeFrom.NextSegmentIndex,
                    Translations = envelope.Translations.Concat(normalized).ToArray()
                };
                EnsureEnvelopeCanBePersisted(nextEnvelope);
                envelope = nextEnvelope;

                DateTimeOffset updatedAt = _timeProvider.GetUtcNow();
                bool batchCompleted = envelope.NextBlockIndex == input.Blocks.Count;
                FeedAiResult current = CreateCacheRecord(
                    input,
                    key,
                    resultId,
                    envelope,
                    requestCount,
                    promptTokens,
                    completionTokens,
                    totalTokens,
                    AddDuration(previousDuration, startedAt, updatedAt),
                    batchCompleted ? null : InProgressErrorCode,
                    createdAt,
                    updatedAt);
                await _repository.UpsertAsync(current, cancellationToken)
                    .ConfigureAwait(false);
                previous = current;
            }
        }
        catch (SubtitleTranslationException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SubtitleTranslationException exception)
        {
            await PersistFailureAsync(
                input,
                key,
                resultId,
                envelope,
                requestCount,
                promptTokens,
                completionTokens,
                totalTokens,
                previousDuration,
                startedAt,
                createdAt,
                exception.Error,
                cancellationToken).ConfigureAwait(false);
            throw new AppException(exception.Error, exception);
        }
        catch (OperationCanceledException exception)
        {
            AppError error = AppErrorFactory.FromTimeout("DeepSeek");
            await PersistFailureAsync(
                input,
                key,
                resultId,
                envelope,
                requestCount,
                promptTokens,
                completionTokens,
                totalTokens,
                previousDuration,
                startedAt,
                createdAt,
                error,
                cancellationToken).ConfigureAwait(false);
            throw new AppException(error, exception);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException
                or JsonException
                or OverflowException)
        {
            AppError error = InvalidTranslationResponse(exception.Message);
            await PersistFailureAsync(
                input,
                key,
                resultId,
                envelope,
                requestCount,
                promptTokens,
                completionTokens,
                totalTokens,
                previousDuration,
                startedAt,
                createdAt,
                error,
                cancellationToken).ConfigureAwait(false);
            throw new AppException(error, exception);
        }

        if (previous is null
            || !TryCreateCompletedResult(input, previous, out FeedAiTranslationResult? completed))
        {
            AppError error = InvalidTranslationResponse("翻译器未返回完整批次。");
            await PersistFailureAsync(
                input,
                key,
                resultId,
                envelope,
                requestCount,
                promptTokens,
                completionTokens,
                totalTokens,
                previousDuration,
                startedAt,
                createdAt,
                error,
                cancellationToken).ConfigureAwait(false);
            throw new AppException(error);
        }

        return completed;
    }

    private async Task PersistFailureAsync(
        FeedAiTranslationInput input,
        FeedAiCacheKey key,
        string resultId,
        PersistedEnvelope envelope,
        int requestCount,
        int promptTokens,
        int completionTokens,
        int totalTokens,
        long previousDuration,
        DateTimeOffset startedAt,
        DateTimeOffset createdAt,
        AppError error,
        CancellationToken cancellationToken)
    {
        DateTimeOffset updatedAt = _timeProvider.GetUtcNow();
        FeedAiResult failure = CreateCacheRecord(
            input,
            key,
            resultId,
            envelope,
            requestCount,
            promptTokens,
            completionTokens,
            totalTokens,
            AddDuration(previousDuration, startedAt, updatedAt),
            error.Code.ToString(),
            createdAt,
            updatedAt);
        await _repository.UpsertAsync(failure, cancellationToken).ConfigureAwait(false);
    }

    private static FeedAiResult CreateCacheRecord(
        FeedAiTranslationInput input,
        FeedAiCacheKey key,
        string resultId,
        PersistedEnvelope envelope,
        int requestCount,
        int promptTokens,
        int completionTokens,
        int totalTokens,
        long durationMilliseconds,
        string? errorCode,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(
            resultId,
            key,
            input.Title.Trim(),
            SerializeEnvelope(envelope),
            requestCount,
            promptTokens,
            completionTokens,
            totalTokens,
            durationMilliseconds,
            errorCode,
            createdAt,
            updatedAt);

    private static PersistedTranslation[] ValidateAndNormalizeBatch(
        FeedAiTranslationInput input,
        PersistedEnvelope envelope,
        SubtitleTranslationBatchResult batch)
    {
        if (!string.Equals(
                batch.ResumeFrom.OperationId,
                envelope.OperationId,
                StringComparison.Ordinal)
            || batch.ResumeFrom.NextSegmentIndex <= envelope.NextBlockIndex
            || batch.ResumeFrom.NextSegmentIndex > input.Blocks.Count)
        {
            throw new InvalidOperationException("翻译批次返回了无效恢复位置。");
        }

        int expectedCount = batch.ResumeFrom.NextSegmentIndex - envelope.NextBlockIndex;
        if (batch.Translations.Count != expectedCount)
            throw new InvalidOperationException("翻译批次存在缺项或增项。");

        Dictionary<int, string> translatedBySequence = batch.Translations.ToDictionary(
            item => item.Sequence,
            item => item.TranslatedText);
        var normalized = new PersistedTranslation[expectedCount];
        for (int offset = 0; offset < expectedCount; offset++)
        {
            FeedAiTranslationBlock source = input.Blocks[envelope.NextBlockIndex + offset];
            if (!translatedBySequence.Remove(source.Sequence, out string? translatedText)
                || string.IsNullOrWhiteSpace(translatedText)
                || translatedText.Length > MaximumTranslatedBlockCharacters)
            {
                throw new InvalidOperationException(
                    $"翻译批次缺少原序号 {source.Sequence} 的有效纯文本译文。");
            }
            normalized[offset] = new(source.Sequence, translatedText);
        }
        if (translatedBySequence.Count > 0)
            throw new InvalidOperationException("翻译批次包含未知原序号。");
        return normalized;
    }

    private static string SerializeEnvelope(PersistedEnvelope envelope)
    {
        string content = JsonSerializer.Serialize(envelope, CacheJsonOptions);
        if (content.Length > MaximumCacheCharacters)
            throw new InvalidOperationException("译文缓存超过本地安全上限。");
        return content;
    }

    private static void EnsureEnvelopeCanBePersisted(PersistedEnvelope envelope) =>
        _ = SerializeEnvelope(envelope);

    private static bool TryCreateCompletedResult(
        FeedAiTranslationInput input,
        FeedAiResult? cached,
        [NotNullWhen(true)] out FeedAiTranslationResult? result)
    {
        result = null;
        if (cached is not { ErrorCode: null }
            || !TryReadEnvelope(input, cached, out PersistedEnvelope? envelope)
            || envelope.NextBlockIndex != input.Blocks.Count)
        {
            return false;
        }

        var translated = new FeedAiTranslatedBlock[input.Blocks.Count];
        for (int index = 0; index < translated.Length; index++)
        {
            FeedAiTranslationBlock source = input.Blocks[index];
            PersistedTranslation translation = envelope.Translations[index];
            translated[index] = new(
                source.Sequence,
                source.Kind,
                source.Text,
                translation.TranslatedText,
                source.ResourceUrl,
                source.HeadingLevel,
                source.Links);
        }
        result = new(cached, Array.AsReadOnly(translated));
        return true;
    }

    private static bool TryReadEnvelope(
        FeedAiTranslationInput input,
        FeedAiResult? cached,
        [NotNullWhen(true)] out PersistedEnvelope? envelope)
    {
        envelope = null;
        if (cached is null || string.IsNullOrWhiteSpace(cached.Content)) return false;
        try
        {
            PersistedEnvelope? parsed = JsonSerializer.Deserialize<PersistedEnvelope>(
                cached.Content,
                CacheJsonOptions);
            if (parsed is null
                || parsed.Version != EnvelopeVersion
                || string.IsNullOrWhiteSpace(parsed.OperationId)
                || parsed.OperationId.Length > 256
                || parsed.NextBlockIndex < 0
                || parsed.NextBlockIndex > input.Blocks.Count
                || parsed.Translations is null
                || parsed.Translations.Count != parsed.NextBlockIndex)
            {
                return false;
            }

            for (int index = 0; index < parsed.Translations.Count; index++)
            {
                PersistedTranslation translation = parsed.Translations[index];
                if (translation is null
                    || translation.Sequence != input.Blocks[index].Sequence
                    || string.IsNullOrWhiteSpace(translation.TranslatedText)
                    || translation.TranslatedText.Length > MaximumTranslatedBlockCharacters)
                {
                    return false;
                }
            }
            envelope = parsed with
            {
                Translations = Array.AsReadOnly(parsed.Translations.ToArray())
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static PersistedEnvelope CreateEmptyEnvelope(FeedAiCacheKey key) =>
        new(
            EnvelopeVersion,
            $"feed-translation-{CreateKeyHash(key)}",
            0,
            Array.Empty<PersistedTranslation>());

    private FeedAiCacheKey CreateKey(FeedAiTranslationInput input) =>
        new(
            input.EntryId,
            input.ContentHash,
            FeedAiTaskType.Translation,
            input.TargetLanguage,
            _options.Model,
            _options.PromptVersion);

    private static string CreateResultId(FeedAiCacheKey key) =>
        $"feed-ai-{CreateKeyHash(key)}";

    private static string CreateKeyHash(FeedAiCacheKey key)
    {
        string[] values =
        [
            key.EntryId,
            key.ContentHash,
            ((int)key.TaskType).ToString(CultureInfo.InvariantCulture),
            key.TargetLanguage,
            key.Model,
            key.PromptVersion
        ];
        string canonical = string.Concat(values.Select(
            value => $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private FeedAiTranslationInput ValidateAndSnapshot(FeedAiTranslationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateText(input.EntryId, nameof(input.EntryId), 256);
        if (input.ContentHash.Length != 64
            || input.ContentHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "内容哈希必须是 64 位十六进制 SHA-256。",
                nameof(input));
        }
        ValidateText(input.Title, nameof(input.Title), 500);
        ValidateText(input.TargetLanguage, nameof(input.TargetLanguage), 32);
        ArgumentNullException.ThrowIfNull(input.Blocks);
        if (input.Blocks.Count is < 1 || input.Blocks.Count > _options.MaximumBlocks)
            throw new ArgumentOutOfRangeException(nameof(input));

        int totalCharacters = 0;
        var sequences = new HashSet<int>();
        var blocks = new FeedAiTranslationBlock[input.Blocks.Count];
        for (int index = 0; index < input.Blocks.Count; index++)
        {
            FeedAiTranslationBlock block = input.Blocks[index]
                ?? throw new ArgumentException("翻译块不能为 null。", nameof(input));
            ArgumentOutOfRangeException.ThrowIfNegative(block.Sequence);
            if (!sequences.Add(block.Sequence))
                throw new ArgumentException($"翻译块原序号 {block.Sequence} 重复。", nameof(input));
            ArgumentNullException.ThrowIfNull(block.Text);
            if (string.IsNullOrWhiteSpace(block.Text)
                || block.Text.Length > _options.MaximumBlockCharacters)
            {
                throw new ArgumentOutOfRangeException(nameof(input));
            }
            totalCharacters = checked(totalCharacters + block.Text.Length);
            if (totalCharacters > _options.MaximumTotalCharacters)
                throw new ArgumentOutOfRangeException(nameof(input));

            ArgumentNullException.ThrowIfNull(block.Links);
            ArticleContentLink[] links = block.Links
                .Select(link => link
                    ?? throw new ArgumentException("链接不能为 null。", nameof(input)))
                .ToArray();
            blocks[index] = block with { Links = Array.AsReadOnly(links) };
        }

        return input with { Blocks = Array.AsReadOnly(blocks) };
    }

    private static void ValidateOptions(FeedAiTranslationOptions options)
    {
        ValidateText(options.Model, nameof(options.Model), 128);
        ValidateText(options.PromptVersion, nameof(options.PromptVersion), 128);
        if (options.BatchSize is < 1 or > SubtitleTranslationRequest.MaximumBatchSize
            || options.MaximumBlocks is < 1 or > 10_000
            || options.MaximumBlockCharacters is < 1 or > 12_000
            || options.MaximumTotalCharacters is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static long AddDuration(
        long previousDuration,
        DateTimeOffset startedAt,
        DateTimeOffset updatedAt) =>
        checked(previousDuration + Math.Max(
            0,
            (long)Math.Ceiling((updatedAt - startedAt).TotalMilliseconds)));

    private static AppError InvalidTranslationResponse(string details) =>
        new(
            AppErrorCode.ProviderUnavailable,
            "AI 译文无效",
            "DeepSeek 返回的译文无法按原文顺序安全使用。",
            "原文仍可阅读；请稍后从已保存位置重试。",
            details.Length <= 2048 ? details : details[..2048],
            "DeepSeek",
            IsRetryable: true);

    private async ValueTask<KeyLockLease> AcquireKeyLockAsync(
        FeedAiCacheKey key,
        CancellationToken cancellationToken)
    {
        KeyLock keyLock;
        lock (_keyLocksSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_keyLocks.TryGetValue(key, out keyLock!))
            {
                keyLock = new();
                _keyLocks.Add(key, keyLock);
            }
            keyLock.Users++;
        }

        try
        {
            await keyLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(this, key, keyLock);
        }
        catch
        {
            ReleaseKeyLockReference(key, keyLock, gateHeld: false);
            throw;
        }
    }

    private void ReleaseKeyLockReference(
        FeedAiCacheKey key,
        KeyLock keyLock,
        bool gateHeld)
    {
        if (gateHeld) keyLock.Gate.Release();
        lock (_keyLocksSync)
        {
            keyLock.Users--;
            if (keyLock.Users == 0
                && _keyLocks.Remove(key, out KeyLock? removed)
                && ReferenceEquals(keyLock, removed))
            {
                keyLock.Gate.Dispose();
            }
        }
    }

    private sealed record PersistedEnvelope(
        int Version,
        string OperationId,
        int NextBlockIndex,
        IReadOnlyList<PersistedTranslation> Translations);

    private sealed record PersistedTranslation(
        int Sequence,
        string TranslatedText);

    private sealed class KeyLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class KeyLockLease(
        CachedFeedAiTranslationService owner,
        FeedAiCacheKey key,
        KeyLock keyLock) : IDisposable
    {
        private CachedFeedAiTranslationService? _owner = owner;

        public void Dispose()
        {
            CachedFeedAiTranslationService? current = Interlocked.Exchange(ref _owner, null);
            current?.ReleaseKeyLockReference(key, keyLock, gateHeld: true);
        }
    }
}
