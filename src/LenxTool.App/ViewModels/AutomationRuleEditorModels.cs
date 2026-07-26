using LenxTool.App.Mvvm;
using LenxTool.Core.Models;

namespace LenxTool.App.ViewModels;

public sealed record AutomationFieldChoice(
    FeedAutomationField Field,
    string Label);

public sealed record AutomationOperatorChoice(
    FeedAutomationOperator Operator,
    string Label);

public sealed record AutomationActionChoice(
    FeedAutomationActionType Type,
    string Label);

public sealed record AutomationMatchModeChoice(
    FeedAutomationMatchMode Mode,
    string Label);

public sealed class AutomationConditionEditorItem : ObservableObject
{
    private readonly Action _changed;
    private AutomationFieldChoice _selectedField;
    private AutomationOperatorChoice _selectedOperator;
    private string _value = string.Empty;

    public AutomationConditionEditorItem(
        AutomationFieldChoice field,
        FeedAutomationOperator @operator,
        string? value,
        Action changed)
    {
        _changed = changed;
        _selectedField = field;
        OperatorChoices = CreateOperatorChoices(field.Field);
        _selectedOperator = OperatorChoices.FirstOrDefault(
                choice => choice.Operator == @operator)
            ?? OperatorChoices[0];
        _value = value ?? string.Empty;
    }

    public AutomationFieldChoice SelectedField
    {
        get => _selectedField;
        set
        {
            if (!SetProperty(ref _selectedField, value))
            {
                return;
            }
            OperatorChoices = CreateOperatorChoices(value.Field);
            OnPropertyChanged(nameof(OperatorChoices));
            SelectedOperator = OperatorChoices[0];
            Value = IsBooleanValue ? "true" : string.Empty;
            OnPropertyChanged(nameof(IsBooleanValue));
            OnPropertyChanged(nameof(RequiresValue));
            OnPropertyChanged(nameof(ValueHint));
            _changed();
        }
    }

    public IReadOnlyList<AutomationOperatorChoice> OperatorChoices { get; private set; }

    public AutomationOperatorChoice SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (!SetProperty(ref _selectedOperator, value))
            {
                return;
            }
            if (!RequiresValue)
            {
                Value = string.Empty;
            }
            OnPropertyChanged(nameof(RequiresValue));
            OnPropertyChanged(nameof(ValueHint));
            _changed();
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                _changed();
            }
        }
    }

    public bool RequiresValue =>
        SelectedOperator.Operator != FeedAutomationOperator.Exists;

    public bool IsBooleanValue =>
        SelectedField.Field is FeedAutomationField.HasAudio
            or FeedAutomationField.HasVideo;

    public string ValueHint => SelectedField.Field switch
    {
        FeedAutomationField.Feed => "输入 Feed 的 UUID",
        FeedAutomationField.Category => "输入分类的 UUID",
        FeedAutomationField.PublishedAt => "例如 2026-07-26T08:00:00+08:00",
        FeedAutomationField.HasAudio or FeedAutomationField.HasVideo =>
            "选择 true 或 false",
        _ when SelectedOperator.Operator == FeedAutomationOperator.Regex =>
            "安全正则（不支持回溯）",
        _ when !RequiresValue => "此操作符不需要值",
        _ => "输入匹配值"
    };

    private static AutomationOperatorChoice[] CreateOperatorChoices(
        FeedAutomationField field)
    {
        FeedAutomationOperator[] operators = field switch
        {
            FeedAutomationField.Feed =>
                [FeedAutomationOperator.Equals],
            FeedAutomationField.Category =>
                [FeedAutomationOperator.Equals, FeedAutomationOperator.Exists],
            FeedAutomationField.Title
                or FeedAutomationField.Author
                or FeedAutomationField.Content =>
                [
                    FeedAutomationOperator.Contains,
                    FeedAutomationOperator.Equals,
                    FeedAutomationOperator.Regex,
                    FeedAutomationOperator.Exists
                ],
            FeedAutomationField.Language =>
                [FeedAutomationOperator.Equals, FeedAutomationOperator.Exists],
            FeedAutomationField.PublishedAt =>
                [
                    FeedAutomationOperator.After,
                    FeedAutomationOperator.Before,
                    FeedAutomationOperator.Exists
                ],
            FeedAutomationField.HasAudio
                or FeedAutomationField.HasVideo =>
                [FeedAutomationOperator.Equals, FeedAutomationOperator.Exists],
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        return operators.Select(value => new AutomationOperatorChoice(
                value,
                AutomationRuleLabels.Operator(value)))
            .ToArray();
    }
}

public sealed class AutomationActionEditorItem : ObservableObject
{
    private readonly Action _changed;
    private AutomationActionChoice _selectedType;
    private string _value = string.Empty;

    public AutomationActionEditorItem(
        AutomationActionChoice type,
        string? value,
        Action changed)
    {
        _selectedType = type;
        _value = value ?? string.Empty;
        _changed = changed;
    }

    public AutomationActionChoice SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value))
            {
                return;
            }
            if (!RequiresValue)
            {
                Value = string.Empty;
            }
            else if (IsTranslation)
            {
                Value = "zh-Hans";
            }
            OnPropertyChanged(nameof(RequiresValue));
            OnPropertyChanged(nameof(IsTranslation));
            OnPropertyChanged(nameof(ValueHint));
            _changed();
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? string.Empty))
            {
                _changed();
            }
        }
    }

    public bool RequiresValue =>
        SelectedType.Type is FeedAutomationActionType.AddTag
            or FeedAutomationActionType.Translate;

    public bool IsTranslation =>
        SelectedType.Type == FeedAutomationActionType.Translate;

    public string ValueHint => SelectedType.Type switch
    {
        FeedAutomationActionType.AddTag => "输入本地标签",
        FeedAutomationActionType.Translate => "选择目标语言",
        _ => "此动作不接受载荷"
    };
}

public sealed record AutomationSimulationEntryViewModel(
    string EntryId,
    string Title,
    string SourceLabel,
    string PublishedText,
    bool IsMatched,
    string OutcomeLabel,
    string ActionsLabel);

internal static class AutomationRuleLabels
{
    public static string Field(FeedAutomationField value) => value switch
    {
        FeedAutomationField.Feed => "Feed",
        FeedAutomationField.Category => "分类",
        FeedAutomationField.Title => "标题",
        FeedAutomationField.Author => "作者",
        FeedAutomationField.Content => "正文",
        FeedAutomationField.Language => "语言",
        FeedAutomationField.PublishedAt => "发布时间",
        FeedAutomationField.HasAudio => "包含音频",
        FeedAutomationField.HasVideo => "包含视频",
        _ => value.ToString()
    };

    public static string Operator(FeedAutomationOperator value) => value switch
    {
        FeedAutomationOperator.Equals => "等于",
        FeedAutomationOperator.Contains => "包含",
        FeedAutomationOperator.Regex => "安全正则",
        FeedAutomationOperator.Before => "早于",
        FeedAutomationOperator.After => "晚于",
        FeedAutomationOperator.Exists => "存在",
        _ => value.ToString()
    };

    public static string Action(FeedAutomationActionType value) => value switch
    {
        FeedAutomationActionType.AddTag => "添加标签",
        FeedAutomationActionType.Hide => "隐藏",
        FeedAutomationActionType.MarkRead => "标为已读",
        FeedAutomationActionType.GenerateSummary => "生成摘要",
        FeedAutomationActionType.Translate => "翻译",
        FeedAutomationActionType.SendToMedia => "发送到媒体工作台",
        FeedAutomationActionType.Notify => "通知",
        _ => value.ToString()
    };
}
