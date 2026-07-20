using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;
using LenxTool.Core.Media;
using LenxTool.Core.Models;
using NAudio.Wave;

namespace LenxTool.Infrastructure.Networking;

public sealed class GroqWhisperClient(
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore) : ITranscriptionService
{
    private static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/audio/transcriptions");
    private const long MaximumAudioBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan ChunkDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ChunkOverlap = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<SubtitleSegment>> TranscribeAsync(
        string audioPath,
        string model,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        TimeSpan duration;
        try
        {
            using var reader = new WaveFileReader(audioPath);
            duration = reader.TotalTime;
        }
        catch (Exception exception) when (exception is FormatException or EndOfStreamException)
        {
            return await TranscribeSingleAsync(audioPath, model, null, progress, cancellationToken).ConfigureAwait(false);
        }
        if (duration <= ChunkDuration)
            return await TranscribeSingleAsync(audioPath, model, null, progress, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<AudioChunk> chunks = AudioChunkPlanner.Plan(duration, ChunkDuration, ChunkOverlap);
        IReadOnlyList<SubtitleSegment> merged = [];
        for (int index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioChunk chunk = chunks[index];
            string chunkPath = Path.Combine(Path.GetTempPath(), $"lenxtool-groq-{Guid.NewGuid():N}.wav");
            try
            {
                await WriteChunkAsync(audioPath, chunkPath, chunk, cancellationToken).ConfigureAwait(false);
                string? prompt = BuildContextPrompt(merged);
                IReadOnlyList<SubtitleSegment> local = await TranscribeSingleAsync(
                    chunkPath, model, prompt, null, cancellationToken).ConfigureAwait(false);
                SubtitleSegment[] shifted = local.Select(segment => segment with
                {
                    Start = segment.Start + chunk.Start,
                    End = segment.End + chunk.Start
                }).ToArray();
                TimeSpan handoff = index == 0 ? TimeSpan.Zero : chunk.Start + TimeSpan.FromTicks(ChunkOverlap.Ticks / 2);
                merged = SegmentMerger.Merge(merged, shifted, handoff);
                progress?.Report((index + 1) * 100d / chunks.Count);
            }
            finally
            {
                try { File.Delete(chunkPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return merged;
    }

    private async Task<IReadOnlyList<SubtitleSegment>> TranscribeSingleAsync(
        string audioPath,
        string model,
        string? prompt,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var fileInfo = new FileInfo(audioPath);
        if (!fileInfo.Exists) throw new FileNotFoundException("找不到待转写音频。", audioPath);
        if (fileInfo.Length > MaximumAudioBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(audioPath), "单个上传分片不能超过 200 MiB。");
        }

        string? apiKey = await secretStore.GetAsync("groq_api_key", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AppException(new(
                AppErrorCode.CredentialsInvalid,
                "尚未配置 Groq Key",
                "云端语音识别需要自备 Groq Key 或登录共享额度账号。",
                "请在设置中填写 Key，或切换本地 Whisper。",
                Provider: "Groq"));
        }

        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var form = new MultipartFormDataContent();
        await using var stream = new FileStream(
            audioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", fileInfo.Name);
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        request.Content = form;

        try
        {
            using HttpClient client = httpClientFactory.CreateClient("LenxTool.Groq");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            string requestId = Header(response, "x-request-id") ?? string.Empty;
            if (!response.IsSuccessStatusCode)
            {
                string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new AppException(AppErrorFactory.FromHttp(
                    response.StatusCode,
                    "Groq",
                    requestId,
                    LimitTechnicalDetails(responseText),
                    ParseRetryAfter(response),
                    Header(response, "x-ratelimit-limit-requests"),
                    CalculateUsed(response)));
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var parsed = new List<SubtitleSegment>();
            if (document.RootElement.TryGetProperty("segments", out JsonElement segments))
            {
                foreach (JsonElement element in segments.EnumerateArray())
                {
                    parsed.Add(new(
                        TimeSpan.FromSeconds(element.GetProperty("start").GetDouble()),
                        TimeSpan.FromSeconds(element.GetProperty("end").GetDouble()),
                        element.GetProperty("text").GetString()?.Trim() ?? string.Empty,
                        AverageLogProbability: GetOptionalDouble(element, "avg_logprob"),
                        NoSpeechProbability: GetOptionalDouble(element, "no_speech_prob")));
                }
            }

            progress?.Report(100);
            return SegmentMerger.Merge([], parsed, TimeSpan.Zero);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AppException(AppErrorFactory.FromTimeout("Groq"));
        }
        catch (HttpRequestException exception)
        {
            throw new AppException(AppErrorFactory.FromNetwork("Groq"), exception);
        }
    }

    private static Task WriteChunkAsync(
        string sourcePath,
        string destinationPath,
        AudioChunk chunk,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var reader = new WaveFileReader(sourcePath);
        long start = Align((long)(chunk.Start.TotalSeconds * reader.WaveFormat.AverageBytesPerSecond), reader.WaveFormat.BlockAlign);
        long length = Align((long)(chunk.Duration.TotalSeconds * reader.WaveFormat.AverageBytesPerSecond), reader.WaveFormat.BlockAlign);
        reader.Position = Math.Min(start, reader.Length);
        using var writer = new WaveFileWriter(destinationPath, reader.WaveFormat);
        byte[] buffer = new byte[81920];
        long remaining = Math.Min(length, reader.Length - reader.Position);
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) break;
            writer.Write(buffer, 0, read);
            remaining -= read;
        }
    }, cancellationToken);

    private static string? BuildContextPrompt(IReadOnlyList<SubtitleSegment> segments)
    {
        if (segments.Count == 0) return null;
        string context = string.Join(' ', segments.TakeLast(6).Select(segment => segment.Text));
        return context.Length <= 180 ? context : context[^180..];
    }

    private static long Align(long value, int blockAlign) => value - value % blockAlign;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        string? value = Header(response, "Retry-After");
        return double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out double seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static string? CalculateUsed(HttpResponseMessage response)
    {
        string? limitText = Header(response, "x-ratelimit-limit-requests");
        string? remainingText = Header(response, "x-ratelimit-remaining-requests");
        return long.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long limit) &&
               long.TryParse(remainingText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long remaining)
            ? Math.Max(0, limit - remaining).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string LimitTechnicalDetails(string value) =>
        value.Length <= 2048 ? value : value[..2048];
}
