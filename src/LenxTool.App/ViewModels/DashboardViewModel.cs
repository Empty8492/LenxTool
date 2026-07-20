namespace LenxTool.App.ViewModels;

public sealed record NewsPreview(string Time, string Source, string Title, string Summary);
public sealed record TrendPreview(int Rank, string Platform, string Title, string Heat);
public sealed record TaskPreview(string Name, string Status, double Progress, string Detail);
public sealed record QuickAction(string Label, string Description, string PageId, string IconData);

public sealed class DashboardViewModel : PageViewModel
{
    public DashboardViewModel() : base("今天，从重要的开始", "2026 年 7 月 19 日 · 本地数据已就绪")
    {
        News =
        [
            new("08:20", "AI 早报", "本地模型与云端推理进入协同阶段", "端侧隐私与云端能力开始形成更清晰的分工。"),
            new("07:45", "产品观察", "多模态工具正在成为桌面工作流入口", "从单点功能转向可追踪、可取消的任务链。"),
            new("昨日", "工程实践", "更新供应链安全重新受到重视", "签名清单与可回滚安装成为桌面发布基础设施。")
        ];
        Trends =
        [
            new(1, "微博", "AI 设备端推理", "892 万"),
            new(2, "知乎", "如何设计可靠的桌面工具", "615 万"),
            new(3, "GitHub", "local-first 应用架构", "4.2k stars")
        ];
        RecentTasks =
        [
            new("访谈录音 07-19.wav", "等待开始", 0, "本地 Whisper · 双语 SRT"),
            new("发布会片段.mp4", "已完成", 100, "12:43 · 已导出 3 个文件")
        ];
        QuickActions =
        [
            new("生成字幕", "导入音视频并创建批量任务", "media", "M4,3 L20,3 20,15 13,15 8,20 8,15 4,15 Z"),
            new("整理 JSON", "格式化、校验、排序或 Diff", "tools", "M6,3 L18,3 18,21 6,21 Z M9,8 L15,8 M9,12 L15,12 M9,16 L13,16"),
            new("全局搜索", "搜索早报、热点、报告与收藏", "history", "M10,4 A6,6 0 1 0 10,16 A6,6 0 1 0 10,4 M14.5,14.5 L20,20")
        ];
    }

    public IReadOnlyList<NewsPreview> News { get; }
    public IReadOnlyList<TrendPreview> Trends { get; }
    public IReadOnlyList<TaskPreview> RecentTasks { get; }
    public IReadOnlyList<QuickAction> QuickActions { get; }
}
