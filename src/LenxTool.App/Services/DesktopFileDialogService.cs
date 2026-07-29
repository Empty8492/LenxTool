using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LenxTool.App.Services;

public interface IDesktopFileDialogService
{
    IReadOnlyList<string> PickMediaFiles();
    string? PickWhisperModel();
    string? PickDatabaseBackup();
    string? PickFileForHash();
    (string Source, string Destination)? PickWordConversion();
    string? PickFolder();
    void OpenFolder(string path);
    void OpenUri(string uri);
}

public interface IOpmlFileDialogService
{
    string? PickOpmlImport();
    string? PickOpmlExport(string suggestedFileName);
}

public sealed class DesktopFileDialogService : IDesktopFileDialogService, IOpmlFileDialogService
{
    public IReadOnlyList<string> PickMediaFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择音频、视频或 SRT 字幕",
            Filter = "媒体与字幕|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.wma;*.mp4;*.mkv;*.mov;*.webm;*.srt|SRT 字幕|*.srt|媒体文件|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.wma;*.mp4;*.mkv;*.mov;*.webm|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickWhisperModel()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 Whisper 模型",
            Filter = "Whisper GGML 模型|ggml-*.bin",
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickDatabaseBackup()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Lenx 数据库备份",
            Filter = "SQLite 数据库|*.db;*.sqlite|所有文件|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFileForHash()
    {
        var dialog = new OpenFileDialog { Title = "选择要校验的文件", Filter = "所有文件|*.*", CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public (string Source, string Destination)? PickWordConversion()
    {
        var open = new OpenFileDialog { Title = "选择 Word 文档", Filter = "Word 文档|*.doc;*.docx", CheckFileExists = true };
        if (open.ShowDialog() != true) return null;
        var save = new SaveFileDialog
        {
            Title = "保存 PDF",
            Filter = "PDF 文档|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(open.FileName) + ".pdf",
            AddExtension = true
        };
        return save.ShowDialog() == true ? (open.FileName, save.FileName) : null;
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Obsidian Vault 根目录",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickOpmlImport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要预览的 OPML 订阅文件",
            Filter = "OPML 订阅文件|*.opml;*.xml|所有文件|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOpmlExport(string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出共享订阅目录",
            Filter = "OPML 订阅文件|*.opml",
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = ".opml"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void OpenFolder(string path)
    {
        string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = folder
        });
    }

    public void OpenUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? target) ||
            target.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("只能打开 HTTP 或 HTTPS 链接。", nameof(uri));
        }

        Process.Start(new ProcessStartInfo(target.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }
}
