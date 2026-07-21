using System.Text.Json;
using LenxTool.App.Mvvm;
using LenxTool.App.Services;
using LenxTool.Core.Contracts;
using LenxTool.Core.Tools;

namespace LenxTool.App.ViewModels;

public sealed class ToolsViewModel : PageViewModel
{
    private readonly IFileHashService _hashService;
    private readonly IDocumentConverter _documentConverter;
    private readonly IDesktopFileDialogService _dialogs;
    private string _input = "{\n  \"name\": \"Lenx Tools\",\n  \"localFirst\": true\n}";
    private string _output = string.Empty;
    private string _status = "选择操作后，结果会显示在右侧。";

    public ToolsViewModel(
        IFileHashService hashService,
        IDocumentConverter documentConverter,
        IDesktopFileDialogService dialogs) : base("文档与数据工具", "JSON、编码、校验与文本整理均可离线使用")
    {
        _hashService = hashService;
        _documentConverter = documentConverter;
        _dialogs = dialogs;
        FormatJsonCommand = new(() => Transform(JsonToolkit.Format, "JSON 已格式化"));
        MinifyJsonCommand = new(() => Transform(JsonToolkit.Minify, "JSON 已压缩"));
        SortJsonCommand = new(() => Transform(value => JsonToolkit.SortProperties(value), "JSON 键已排序"));
        ValidateJsonCommand = new(ValidateJson);
        EncodeBase64Command = new(() => Transform(EncodingToolkit.ToBase64, "已按 UTF-8 编码 Base64"));
        DecodeBase64Command = new(() => Transform(EncodingToolkit.FromBase64, "Base64 已按 UTF-8 解码"));
        EncodeUrlCommand = new(() => Transform(EncodingToolkit.EncodeUrl, "URL 组件已编码"));
        DecodeUrlCommand = new(() => Transform(EncodingToolkit.DecodeUrl, "URL 组件已解码"));
        CleanTextCommand = new(() => Transform(
            value => TextToolkit.Clean(value, removeDuplicateLines: true, collapseBlankLines: true),
            "重复行与多余空行已清理"));
        SwapCommand = new(Swap);
        HashFileCommand = new(HashFileAsync);
        WordToPdfCommand = new(WordToPdfAsync);
    }

    public string Input
    {
        get => _input;
        set => SetProperty(ref _input, value ?? string.Empty);
    }

    public string Output
    {
        get => _output;
        set => SetProperty(ref _output, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public RelayCommand FormatJsonCommand { get; }
    public RelayCommand MinifyJsonCommand { get; }
    public RelayCommand SortJsonCommand { get; }
    public RelayCommand ValidateJsonCommand { get; }
    public RelayCommand EncodeBase64Command { get; }
    public RelayCommand DecodeBase64Command { get; }
    public RelayCommand EncodeUrlCommand { get; }
    public RelayCommand DecodeUrlCommand { get; }
    public RelayCommand CleanTextCommand { get; }
    public RelayCommand SwapCommand { get; }
    public AsyncRelayCommand HashFileCommand { get; }
    public AsyncRelayCommand WordToPdfCommand { get; }

    private async Task HashFileAsync(CancellationToken cancellationToken)
    {
        string? path = _dialogs.PickFileForHash();
        if (path is null) return;
        Status = "正在计算 SHA-256…";
        Output = await _hashService.ComputeSha256Async(path, null, cancellationToken);
        Status = "SHA-256 已完成";
    }

    private async Task WordToPdfAsync(CancellationToken cancellationToken)
    {
        if (!_documentConverter.IsAvailable)
        {
            Status = "此电脑未安装可用的 Microsoft Word，无法执行 PDF 转换。";
            return;
        }
        (string Source, string Destination)? selection = _dialogs.PickWordConversion();
        if (selection is null) return;
        Status = "正在通过独立文档适配器转换…";
        await _documentConverter.ConvertToPdfAsync(selection.Value.Source, selection.Value.Destination, null, cancellationToken);
        Output = selection.Value.Destination;
        Status = "Word 已转换为 PDF";
        _dialogs.OpenFolder(selection.Value.Destination);
    }

    private void Transform(Func<string, string> operation, string successStatus)
    {
        try
        {
            Output = operation(Input);
            Status = successStatus;
        }
        catch (JsonException exception)
        {
            Status = $"JSON 无效：第 {(exception.LineNumber ?? 0) + 1} 行，第 {(exception.BytePositionInLine ?? 0) + 1} 列";
        }
        catch (FormatException exception)
        {
            Status = $"输入格式无效：{exception.Message}";
        }
        catch (ArgumentException exception)
        {
            Status = exception.Message;
        }
    }

    private void ValidateJson()
    {
        JsonValidationResult result = JsonToolkit.Validate(Input);
        Status = result.IsValid
            ? "JSON 语法有效"
            : $"JSON 无效：第 {(result.LineNumber ?? 0) + 1} 行，第 {(result.BytePositionInLine ?? 0) + 1} 列";
        Output = result.IsValid ? "✓ Valid JSON" : result.Message ?? "Invalid JSON";
    }

    private void Swap()
    {
        (Input, Output) = (Output, Input);
        Status = "输入与输出已交换";
    }
}
