using System.Xml.Linq;

namespace LenxTool.App.Tests.Views;

public sealed class NewsCenterLayoutTests
{
    [Fact]
    public void NewsReaderUsesCompactHeaderAndNonStretchingRefreshAction()
    {
        XElement template = LoadNewsCenterTemplate();

        XElement refreshButton = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Content")?.Value == "刷新今日资讯");
        Assert.Equal("Center", refreshButton.Attribute("VerticalAlignment")?.Value);

        XElement dailyTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "每日早报");
        XElement articleView = Assert.Single(
            dailyTab.Descendants(),
            element => element.Name.LocalName == "RichArticleView");
        XElement articleGrid = Assert.IsType<XElement>(articleView.Parent);
        XElement[] articleRows = articleGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();
        Assert.Equal("56", articleRows[0].Attribute("Height")?.Value);
    }

    [Fact]
    public void AiReportReaderReservesMoreHeightForContent()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement reportBody = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attributes().Any(attribute => attribute.Value == "AI 报告正文"));
        XElement reportGrid = Assert.IsType<XElement>(reportBody.Parent);
        XElement[] rows = reportGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();

        Assert.Equal("56", rows[0].Attribute("Height")?.Value);
        Assert.Equal("40", rows[2].Attribute("Height")?.Value);
    }

    [Fact]
    public void NewsPageUsesRoutedSectionsAndSharedSmoothScrolling()
    {
        XElement template = LoadNewsCenterTemplate();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement pageScroller = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "AnimatedScrollViewer"
                && element.Attribute(x + "Name")?.Value == "NewsPageScrollViewer");
        Assert.Equal("Visible", pageScroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("Disabled", pageScroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        // 页面不再携带私有倍率，所有滚动区由共享类级行为统一处理。
        Assert.Null(pageScroller.Attribute("WheelScrollMultiplier"));
        Assert.Equal(
            "{Binding SelectedSectionIndex}",
            pageScroller.Attribute("ScrollResetKey")?.Value);
        Assert.Contains(pageScroller.Descendants(), element => element.Name.LocalName == "TabControl");
        Assert.Contains(
            pageScroller.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding ActiveSectionTitle}");

        XElement sectionHost = Assert.Single(
            pageScroller.Descendants(),
            element => element.Name.LocalName == "TabControl");
        Assert.Equal(
            "{Binding SelectedSectionIndex, Mode=TwoWay}",
            sectionHost.Attribute("SelectedIndex")?.Value);
        Assert.Contains(
            sectionHost.Descendants(),
            element => element.Name.LocalName == "ContentPresenter"
                && element.Attribute("ContentSource")?.Value == "SelectedContent");

        XElement backToTopButton = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "回到页面顶部");
        Assert.Contains("SmoothScrollToTopCommand", backToTopButton.Attribute("Command")?.Value);
        Assert.Contains("NewsPageScrollViewer", backToTopButton.Attribute("CommandTarget")?.Value);
        Assert.Contains(
            backToTopButton.Descendants(),
            element => element.Name.LocalName == "DataTrigger"
                && element.Attribute("Binding")?.Value.Contains("IsBackToTopVisible", StringComparison.Ordinal) == true);
        Assert.Contains(
            backToTopButton.Descendants(),
            element => element.Name.LocalName == "DoubleAnimation"
                && element.Attributes().Any(attribute => attribute.Value == "Opacity")
                && element.Attribute("To")?.Value == "1");
    }

    [Fact]
    public void TrendPageUsesGroupedClickableItemsWithoutNestedListScroller()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement trendTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "热点趋势");

        Assert.DoesNotContain(trendTab.Descendants(), element => element.Name.LocalName == "ListBox");
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding TrendGroups}");
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value.Contains("OpenTrendCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void TrendPageProvidesVisibleMultiSourceFilters()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement trendTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "热点趋势");

        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding SourceFilters}");
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "ToggleButton"
                && element.Attribute("Style")?.Value.Contains("FilterChipStyle", StringComparison.Ordinal) == true
                && element.Attribute("IsChecked")?.Value.Contains("IsSelected", StringComparison.Ordinal) == true);
        Assert.Contains(
            trendTab.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value == "{Binding SelectAllSourcesCommand}");
    }

    [Fact]
    public void NewsSectionsDoNotRenderAnInternalTabStrip()
    {
        XElement template = LoadNewsCenterTemplate();

        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Attribute("Header")?.Value == "Feed 时间线");
        Assert.DoesNotContain(
            template.Descendants(),
            element => element.Name.LocalName == "Border"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "Name"
                    && attribute.Value == "SelectedIndicator"));
    }

    [Fact]
    public void FeedTimelineUsesRecyclingVirtualizationAndScrollPaging()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement timelineTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "资讯列表");
        Assert.Contains(
            timelineTab.Descendants(),
            element => element.Name.LocalName == "FeedTimelineView");
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");
        XElement timeline = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "PagedListBox");

        Assert.Equal("{Binding TimelineEntries}", timeline.Attribute("ItemsSource")?.Value);
        Assert.Equal(
            "{Binding LoadMoreTimelineCommand}",
            timeline.Attribute("LoadMoreCommand")?.Value);
        Assert.Equal("True", timeline.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal("Recycling", timeline.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.Equal("True", timeline.Attribute("ScrollViewer.CanContentScroll")?.Value);
    }

    [Fact]
    public void PictureFeedUsesCachedThumbnailsVirtualizedRowsAndKeyboardOpen()
    {
        XElement host = LoadFixture("FeedTimelineView.xaml");
        XElement viewTabs = Assert.Single(
            host.Descendants(),
            element => element.Name.LocalName == "TabControl");
        Assert.Equal(
            "{Binding SelectedFeedViewIndex, Mode=TwoWay}",
            viewTabs.Attribute("SelectedIndex")?.Value);
        Assert.Contains(
            host.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "图片");

        XElement picture = LoadFixture("FeedPictureView.xaml");
        XElement list = Assert.Single(
            picture.Descendants(),
            element => element.Name.LocalName == "PagedListBox");
        Assert.Equal("{Binding PictureFeed.Rows}", list.Attribute("ItemsSource")?.Value);
        Assert.Equal(
            "{Binding PictureFeed.LoadMoreCommand}",
            list.Attribute("LoadMoreCommand")?.Value);
        Assert.Equal("True", list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal("Recycling", list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.Equal("True", list.Attribute("ScrollViewer.CanContentScroll")?.Value);
        Assert.Contains(
            picture.Descendants(),
            element => element.Name.LocalName == "FeedThumbnail"
                && element.Attribute("SourceUrl")?.Value == "{Binding PrimaryImageUrl}");
        Assert.DoesNotContain(
            picture.Descendants(),
            element => element.Name.LocalName == "Image"
                && element.Attribute("Source")?.Value?.Contains("PrimaryImageUrl", StringComparison.Ordinal) == true);
        Assert.Contains(
            picture.Descendants(),
            element => element.Name.LocalName == "KeyBinding"
                && element.Attribute("Key")?.Value == "Enter"
                && element.Attribute("Command")?.Value.Contains("OpenItemCommand", StringComparison.Ordinal) == true);

        string[] filterNames =
        [
            "图片分类筛选",
            "图片来源筛选",
            "图片日期筛选",
            "仅看收藏图片"
        ];
        Assert.All(
            filterNames,
            name => Assert.Contains(
                picture.Descendants(),
                element => element.Attribute("AutomationProperties.Name")?.Value == name));
    }

    [Fact]
    public void AudioFeedUsesExplicitPlaybackVirtualizationAndAccessibleControls()
    {
        XElement host = LoadFixture("FeedTimelineView.xaml");
        Assert.Contains(
            host.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "音频");

        XElement audio = LoadFixture("FeedAudioView.xaml");
        XElement list = Assert.Single(
            audio.Descendants(),
            element => element.Name.LocalName == "PagedListBox");
        Assert.Equal(
            "{Binding AudioFeed.Items}",
            list.Attribute("ItemsSource")?.Value);
        Assert.Equal(
            "{Binding AudioFeed.Feed.LoadMoreCommand}",
            list.Attribute("LoadMoreCommand")?.Value);
        Assert.Equal(
            "{Binding AudioFeed.SelectedItem, Mode=TwoWay}",
            list.Attribute("SelectedItem")?.Value);
        Assert.Equal(
            "True",
            list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal(
            "Recycling",
            list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.DoesNotContain(
            audio.Descendants(),
            element => element.Name.LocalName == "MediaElement");

        string[] accessibleActions =
        [
            "播放或暂停所选音频",
            "将所选音频送入转写",
            "请求在浏览器打开音频原文",
            "确认在浏览器打开音频原文",
            "取消在浏览器打开音频原文",
            "音频播放进度"
        ];
        Assert.All(
            accessibleActions,
            name => Assert.Contains(
                audio.Descendants(),
                element =>
                    element.Attribute("AutomationProperties.Name")?.Value
                    == name));

        string[] filterNames =
        [
            "音频分类筛选",
            "音频来源筛选",
            "音频日期筛选",
            "仅看收藏音频"
        ];
        Assert.All(
            filterNames,
            name => Assert.Contains(
                audio.Descendants(),
                element =>
                    element.Attribute("AutomationProperties.Name")?.Value
                    == name));
    }

    [Fact]
    public void VideoFeedUsesSafePosterExplicitActionsAndNoEmbeddedPlayer()
    {
        XElement host = LoadFixture("FeedTimelineView.xaml");
        Assert.Contains(
            host.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "视频");

        XElement video = LoadFixture("FeedVideoView.xaml");
        XElement list = Assert.Single(
            video.Descendants(),
            element => element.Name.LocalName == "PagedListBox");
        Assert.Equal(
            "{Binding VideoFeed.Items}",
            list.Attribute("ItemsSource")?.Value);
        Assert.Equal(
            "{Binding VideoFeed.Feed.LoadMoreCommand}",
            list.Attribute("LoadMoreCommand")?.Value);
        Assert.Equal(
            "{Binding VideoFeed.SelectedItem, Mode=TwoWay}",
            list.Attribute("SelectedItem")?.Value);
        Assert.Equal(
            "True",
            list.Attribute("VirtualizingPanel.IsVirtualizing")?.Value);
        Assert.Equal(
            "Recycling",
            list.Attribute("VirtualizingPanel.VirtualizationMode")?.Value);
        Assert.Contains(
            video.Descendants(),
            element => element.Name.LocalName == "FeedThumbnail"
                && element.Attribute("SourceUrl")?.Value
                == "{Binding PosterUrl}");
        Assert.DoesNotContain(
            video.Descendants(),
            element => element.Name.LocalName
                is "MediaElement" or "WebView2" or "SafeHtmlView");

        string[] accessibleActions =
        [
            "检查并下载所选视频",
            "取消视频下载或确认",
            "请求在浏览器打开视频原文",
            "确认下载较大视频",
            "取消下载较大视频",
            "确认在浏览器打开视频原文",
            "取消在浏览器打开视频原文"
        ];
        Assert.All(
            accessibleActions,
            name => Assert.Contains(
                video.Descendants(),
                element =>
                    element.Attribute("AutomationProperties.Name")?.Value
                    == name));

        string[] filterNames =
        [
            "视频分类筛选",
            "视频来源筛选",
            "视频日期筛选",
            "仅看收藏视频"
        ];
        Assert.All(
            filterNames,
            name => Assert.Contains(
                video.Descendants(),
                element =>
                    element.Attribute("AutomationProperties.Name")?.Value
                    == name));
    }

    [Fact]
    public void FeedViewsUseSharedNativeSelectionControlStyles()
    {
        XElement host = LoadFixture("FeedTimelineView.xaml");
        XElement tabs = Assert.Single(
            host.Descendants(),
            element => element.Name.LocalName == "TabControl");
        Assert.Equal(
            "{StaticResource SegmentedTabControlStyle}",
            tabs.Attribute("Style")?.Value);
        Assert.Equal(
            "Feed 视图切换",
            tabs.Attribute("AutomationProperties.Name")?.Value);
        Assert.All(
            host.Descendants().Where(element =>
                element.Name.LocalName == "TabItem"),
            tab => Assert.Equal(
                "{StaticResource SegmentedTabItemStyle}",
                tab.Attribute("Style")?.Value));

        XElement timelineFilters = LoadFixture("FeedTimelineFiltersView.xaml");
        XElement picture = LoadFixture("FeedPictureView.xaml");
        XElement audio = LoadFixture("FeedAudioView.xaml");
        XElement video = LoadFixture("FeedVideoView.xaml");
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");
        XElement[] controls = timelineFilters
            .Descendants()
            .Concat(picture.Descendants())
            .Concat(audio.Descendants())
            .Concat(video.Descendants())
            .Concat(timelineBrowser.Descendants())
            .Where(element =>
                element.Name.LocalName is
                    "ComboBox" or
                    "DatePicker" or
                    "CheckBox")
            .ToArray();

        Assert.NotEmpty(controls);
        Assert.All(controls, control =>
        {
            string expectedStyle = control.Name.LocalName switch
            {
                "ComboBox" => "{StaticResource CompactComboBoxStyle}",
                "DatePicker" => "{StaticResource CompactDatePickerStyle}",
                "CheckBox" => "{StaticResource CompactCheckBoxStyle}",
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(expectedStyle, control.Attribute("Style")?.Value);
            Assert.False(string.IsNullOrWhiteSpace(
                control.Attribute("AutomationProperties.Name")?.Value));
        });
    }

    [Fact]
    public void FeedTimelineProvidesReadOnlyFiltersAndNativeReader()
    {
        XElement template = LoadNewsCenterTemplate();
        XElement timelineTab = Assert.Single(
            template.Descendants(),
            element => element.Name.LocalName == "TabItem"
                && element.Attribute("Header")?.Value == "资讯列表");
        Assert.Contains(
            timelineTab.Descendants(),
            element => element.Name.LocalName == "FeedTimelineView");
        XElement timelineFilters = LoadFixture("FeedTimelineFiltersView.xaml");
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");
        string[] automationNames =
        [
            "Feed 分类筛选",
            "Feed 来源筛选",
            "Feed 日期筛选",
            "Feed 关键词筛选",
            "Feed 阅读状态筛选",
            "仅看 Feed 收藏",
            "Feed 标签筛选"
        ];

        Assert.All(
            automationNames,
            name => Assert.Contains(
                timelineFilters.Descendants(),
                element => element.Attribute("AutomationProperties.Name")?.Value == name));
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "RichArticleView"
                && element.Attribute("Article")?.Value == "{Binding SelectedFeedArticle}"
                && element.Attribute("Document")?.Value == "{Binding SelectedFeedArticleDocument}"
                && element.Attribute("ContentSourceLabel")?.Value == "{Binding FeedReaderSourceLabel}"
                && element.Attribute("ExtractedAt")?.Value == "{Binding FeedReaderExtractedAt}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "正文来源"
                && element.Attribute("ItemsSource")?.Value == "{Binding FeedReaderSourceOptions}"
                && element.Attribute("SelectedItem")?.Value
                    == "{Binding SelectedFeedReaderSource}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "在浏览器打开原文"
                && element.Attribute("Command")?.Value
                    == "{Binding OpenSelectedFeedOriginalCommand}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "生成当前 Feed 摘要"
                && element.Attribute("Command")?.Value
                    == "{Binding GenerateFeedSummaryCommand}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "Feed 阅读模式"
                && element.Attribute("ItemsSource")?.Value
                    == "{Binding FeedReaderLanguageOptions}"
                && element.Attribute("SelectedItem")?.Value
                    == "{Binding SelectedFeedReaderLanguage}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "Feed 译文目标语言"
                && element.Attribute("ItemsSource")?.Value
                    == "{Binding FeedTranslationTargetLanguages}"
                && element.Attribute("SelectedItem")?.Value
                    == "{Binding SelectedFeedTranslationTargetLanguage}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "生成当前 Feed 译文"
                && element.Attribute("Command")?.Value
                    == "{Binding GenerateFeedTranslationCommand}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding FeedTranslationStatus}");
        Assert.Contains(
            timelineFilters.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "摘要当前页 Feed"
                && element.Attribute("Command")?.Value
                    == "{Binding GenerateVisibleFeedSummariesCommand}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding SelectedFeedSummary.Content}");
        Assert.DoesNotContain(
            timelineFilters.Descendants().Concat(timelineBrowser.Descendants()),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Content")?.Value is "新增" or "编辑" or "删除" or "订阅管理");
    }

    [Fact]
    public void FeedFiltersRenderLabelsAndEmptyStatesCoverStaleContent()
    {
        XElement controls = LoadFixture("Controls.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement compactComboStyle = Assert.Single(
            controls.Descendants(),
            element => element.Name.LocalName == "Style"
                && element.Attribute(x + "Key")?.Value == "CompactComboBoxStyle");
        XElement selectionPresenter = Assert.Single(
            compactComboStyle.Descendants(),
            element => element.Name.LocalName == "ContentPresenter"
                && element.Attribute("Content")?.Value
                    == "{TemplateBinding SelectionBoxItem}");
        Assert.Equal(
            "{TemplateBinding ItemTemplateSelector}",
            selectionPresenter.Attribute("ContentTemplateSelector")?.Value);
        Assert.Equal(
            "{TemplateBinding SelectionBoxItemStringFormat}",
            selectionPresenter.Attribute("ContentStringFormat")?.Value);

        XElement filters = LoadFixture("FeedTimelineFiltersView.xaml");
        Assert.Contains(
            filters.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "资讯列表");
        Assert.All(
            filters.Descendants()
                .Where(element => element.Name.LocalName == "ComboBox"
                    && element.Attribute("AutomationProperties.Name")?.Value
                        != "选择已发布智能视图"),
            comboBox => Assert.Equal("Label", comboBox.Attribute("DisplayMemberPath")?.Value));
        Assert.Contains(
            filters.Descendants(),
            element => element.Name.LocalName == "ComboBox"
                && element.Attribute("AutomationProperties.Name")?.Value
                    == "选择已发布智能视图"
                && element.Attribute("DisplayMemberPath")?.Value == "Name");

        XElement browser = LoadFixture("FeedTimelineBrowserView.xaml");
        XElement[] emptyStates = browser.Descendants()
            .Where(element => element.Name.LocalName == "Border"
                && element.Attribute("Panel.ZIndex")?.Value == "10"
                && element.Elements().Any(child =>
                    child.Name.LocalName == "Border.Style"
                    && child.Descendants().Any(descendant =>
                        descendant.Name.LocalName == "DataTrigger"
                        && descendant.Attribute("Value")?.Value is "0" or "{x:Null}")))
            .ToArray();
        Assert.Equal(2, emptyStates.Length);
        Assert.All(emptyStates, emptyState =>
        {
            Assert.Equal("0", emptyState.Attribute("Margin")?.Value);
            Assert.Equal("10", emptyState.Attribute("Panel.ZIndex")?.Value);
        });
    }

    [Fact]
    public void FeedReaderProvidesKeyboardAccessiblePrivateNoteAndTagEditor()
    {
        XElement timelineBrowser = LoadFixture("FeedTimelineBrowserView.xaml");

        XElement note = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "Feed 私人备注");
        Assert.Contains("SelectedTimelineNote", note.Attribute("Text")?.Value);
        Assert.Equal("4000", note.Attribute("MaxLength")?.Value);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "保存 Feed 私人备注"
                && element.Attribute("Command")?.Value == "{Binding SaveTimelineNoteCommand}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "取消 Feed 私人备注编辑"
                && element.Attribute("Command")?.Value == "{Binding CancelTimelineNoteEditCommand}");

        XElement tagInput = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBox"
                && element.Attribute("AutomationProperties.Name")?.Value == "新增 Feed 标签");
        Assert.Contains("TimelineTagInput", tagInput.Attribute("Text")?.Value);
        Assert.Equal("80", tagInput.Attribute("MaxLength")?.Value);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding SelectedTimelineTags}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("Command")?.Value.Contains(
                    "RemoveTimelineTagCommand",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value == "{Binding TimelineEditorStatus}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "切换当前 Feed 已读状态"
                && element.Attribute("Content")?.Value == "{Binding SelectedTimelineEntry.ReadActionLabel}");
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "TextBlock"
                && element.Attribute("Text")?.Value.Contains(
                    "SelectedTimelineEntry.Progress",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "Button"
                && element.Attribute("AutomationProperties.Name")?.Value == "重置 Feed 阅读进度"
                && element.Attribute("Command")?.Value == "{Binding ResetTimelineProgressCommand}");
        XElement articleScrollViewer = Assert.Single(
            timelineBrowser.Descendants(),
            element => element.Name.LocalName == "ScrollViewer"
                && element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name")?.Value
                    == "ArticleScrollViewer");
        Assert.Equal(
            "ArticleScrollViewer_OnScrollChanged",
            articleScrollViewer.Attribute("ScrollChanged")?.Value);
    }

    [Fact]
    public void HistoryFeedResultProvidesKeyboardAccessiblePrivateStateEditor()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(xamlPath);
        XElement history = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "HistoryViewModel",
                    StringComparison.Ordinal) == true);
        string[] automationNames =
        [
            "切换历史 Feed 已读状态",
            "切换历史 Feed 收藏",
            "历史 Feed 私人备注",
            "保存历史 Feed 私人备注",
            "取消历史 Feed 私人备注编辑",
            "新增历史 Feed 标签",
            "添加历史 Feed 标签",
            "移除历史 Feed 标签"
        ];

        Assert.All(
            automationNames,
            name => Assert.Contains(
                history.Descendants(),
                element => element.Attribute("AutomationProperties.Name")?.Value == name));
        Assert.Contains(
            history.Descendants(),
            element => element.Name.LocalName == "ItemsControl"
                && element.Attribute("ItemsSource")?.Value == "{Binding SelectedSearchTags}");
    }

    private static XElement LoadNewsCenterTemplate()
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "App.xaml");
        XDocument document = XDocument.Load(xamlPath);
        return Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "DataTemplate"
                && element.Attribute("DataType")?.Value.Contains(
                    "NewsCenterViewModel",
                    StringComparison.Ordinal) == true);
    }

    private static XElement LoadFixture(string fileName)
    {
        string xamlPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return XDocument.Load(xamlPath).Root
            ?? throw new InvalidDataException($"{fileName} 没有根元素。");
    }
}
