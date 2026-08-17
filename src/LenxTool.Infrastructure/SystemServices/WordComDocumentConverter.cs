using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using LenxTool.Core.Contracts;
using LenxTool.Core.Errors;

namespace LenxTool.Infrastructure.SystemServices;

public sealed class WordComDocumentConverter : IDocumentConverter
{
    private const int PdfFormat = 17;

    public string Name => "Microsoft Word";

    public bool IsAvailable => Type.GetTypeFromProgID("Word.Application", throwOnError: false) is not null;

    public Task ConvertToPdfAsync(
        string sourcePath,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("找不到 Word 文档。", sourcePath);
        string extension = Path.GetExtension(sourcePath);
        if (!string.Equals(extension, ".doc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("仅支持 .doc 和 .docx 文档。", nameof(sourcePath));
        }

        Type? wordType = Type.GetTypeFromProgID("Word.Application", throwOnError: false);
        if (wordType is null)
        {
            throw new AppException(new(
                AppErrorCode.ProviderUnavailable,
                "未安装 Microsoft Word",
                "当前转换适配器需要本机安装 Microsoft Word。",
                "请安装 Word，或在未来版本中配置其他文档转换适配器。",
                Provider: "Microsoft Word"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => ConvertOnStaThread(
            wordType,
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(destinationPath),
            progress,
            completion,
            cancellationToken))
        {
            IsBackground = true,
            Name = "LenxTool.WordConverter"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void ConvertOnStaThread(
        Type wordType,
        string sourcePath,
        string destinationPath,
        IProgress<double>? progress,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        object? application = null;
        object? documents = null;
        object? document = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            application = Activator.CreateInstance(wordType)
                ?? throw new InvalidOperationException("无法启动 Microsoft Word。");
            wordType.InvokeMember("Visible", BindingFlags.SetProperty, null, application, [false], CultureInfo.InvariantCulture);
            wordType.InvokeMember("DisplayAlerts", BindingFlags.SetProperty, null, application, [0], CultureInfo.InvariantCulture);
            documents = wordType.InvokeMember("Documents", BindingFlags.GetProperty, null, application, null, CultureInfo.InvariantCulture);
            document = documents!.GetType().InvokeMember(
                "Open", BindingFlags.InvokeMethod, null, documents, [sourcePath], CultureInfo.InvariantCulture);
            progress?.Report(40);
            cancellationToken.ThrowIfCancellationRequested();
            document!.GetType().InvokeMember(
                "SaveAs2", BindingFlags.InvokeMethod, null, document, [destinationPath, PdfFormat], CultureInfo.InvariantCulture);
            progress?.Report(100);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (TargetInvocationException exception)
        {
            completion.TrySetException(exception.InnerException ?? exception);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            TryInvoke(document, "Close", [false]);
            TryInvoke(application, "Quit", null);
            ReleaseComObject(document);
            ReleaseComObject(documents);
            ReleaseComObject(application);
        }
    }

    private static void TryInvoke(object? target, string member, object?[]? arguments)
    {
        if (target is null) return;
        try
        {
            target.GetType().InvokeMember(
                member, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
        }
        catch (COMException exception)
        {
            Trace.TraceWarning("Word COM cleanup failed for {0}: {1}", member, exception.GetType().Name);
        }
        catch (TargetInvocationException exception)
        {
            Trace.TraceWarning("Word invocation cleanup failed for {0}: {1}", member, exception.GetType().Name);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
