using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DeveloperBrowser.Core.Security;
using DeveloperBrowser.Core.Networking;
using System.Text;
using System.Text.RegularExpressions;

namespace DeveloperBrowser.App;

public partial class NetworkInspectorView : UserControl
{
    private const string ComparisonGuidance = "HOW TO COMPARE\n\n1. Select the request you want to use as the baseline.\n2. Click Compare.\n3. Select a second request from Activity.\n\nDevBrowser will show only meaningful differences in status, headers, bodies, protocol, and recorded timing.";
    private NetworkCaptureService? _capture;
    private ICollectionView? _requestsView;
    private CapturedNetworkRequest? _selectedRequest;
    private CapturedNetworkRequest? _comparisonBaseRequest;
    private bool _comparisonAwaitingTarget;
    private NetworkProblemCategory _problemCategory = NetworkProblemCategory.All;
    private bool _followingRelatedRequest;
    private bool _synchronizingProblemFilter;
    private bool _synchronizingDetailSection;
    private NetworkInspectorDock? _currentDock;
    private double _bottomRequestRatio = 0.58;
    private double _rightRequestRatio = 0.52;
    private readonly List<ProblemCategoryItem> _problemCategories = [];
    private readonly List<DetailSectionItem> _detailSections = [];
    private readonly HttpRequestViewer _requestViewer = new();
    private readonly HttpResponseViewer _responseViewer = new();
    private readonly TabItem _requestTab = new() { Header = "Request" };
    private readonly TabItem _responseTab = new() { Header = "Response" };
    private readonly TabItem _securityTab = new() { Header = "Security" };
    private readonly TextBlock _securityOverviewText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Expander _policySecurityExpander = new() { IsExpanded = true };
    private readonly Expander _jwtSecurityExpander = new() { IsExpanded = false };
    private readonly TextBlock _policySecurityHeader = new() { Text = "CORS / CSP", FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _jwtSecurityHeader = new() { Text = "JWT · No token detected", FontWeight = FontWeights.SemiBold };
    private readonly Button _pinButton = new() { Content = "Pin", MinWidth = 52 };
    private readonly Button _compareButton = new() { Content = "Compare", MinWidth = 72 };
    private readonly RichTextBox _comparisonBox = new()
    {
        IsReadOnly = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        FontFamily = new FontFamily("Cascadia Mono"),
        FontSize = 11,
        Padding = new Thickness(10),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0)
    };
    private readonly TabItem _comparisonTab = new() { Header = "Comparison" };

    public event EventHandler<CapturedNetworkRequest>? OpenInRestClientRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler<NetworkInspectorDock>? DockRequested;

    public NetworkInspectorView()
    {
        InitializeComponent();
        ConfigureRequestActions();
        _comparisonTab.Content = _comparisonBox;
        ShowComparisonGuidance();
        ConfigureConsolidatedDetailSections();
        ScrollViewer.SetHorizontalScrollBarVisibility(FindingsList, ScrollBarVisibility.Disabled);
        FindingsList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        if (DiagnosisTab.Content is ScrollViewer diagnosisScroller)
            diagnosisScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _detailSections.AddRange(DetailTabs.Items.OfType<TabItem>()
            .Select(tab => new DetailSectionItem(tab.Header?.ToString() ?? "Details", tab)));
        DetailSectionBox.ItemsSource = _detailSections;
        DetailSectionBox.SelectedIndex = Math.Max(DetailTabs.SelectedIndex, 0);
        InspectorDetailSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        InspectorDetailSplitter.ShowsPreview = false;
        InspectorDetailSplitter.DragCompleted += InspectorDetailSplitter_DragCompleted;
        ProblemInbox.ItemsSource = _problemCategories;
        FocusFilterBox.ItemsSource = _problemCategories;
        ConfigureToolbar(NetworkInspectorDock.Bottom);
        UpdateProblemInbox();
    }

    private void ConfigureConsolidatedDetailSections()
    {
        var timingTab = (TabItem)TimingBox.Parent;
        var policyTab = (TabItem)PolicyAnalysisBox.Parent;
        var jwtScroll = (ScrollViewer)JwtDetailTab.Content;
        var jwtContent = (UIElement)jwtScroll.Content;
        policyTab.Content = null;
        jwtScroll.Content = null;

        _requestTab.Content = _requestViewer;
        _responseTab.Content = _responseViewer;
        _policySecurityExpander.Style = (Style)FindResource("InspectorAccordion");
        _policySecurityHeader.Foreground = (Brush)FindResource("PrimaryTextBrush");
        _policySecurityExpander.Header = _policySecurityHeader;
        _policySecurityExpander.Content = PolicyAnalysisBox;
        _jwtSecurityExpander.Style = (Style)FindResource("InspectorAccordion");
        _jwtSecurityHeader.Foreground = (Brush)FindResource("PrimaryTextBrush");
        _jwtSecurityExpander.Header = _jwtSecurityHeader;
        _jwtSecurityExpander.Content = jwtContent;

        var overviewCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 36, 50)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 70, 94)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 9, 11, 9),
            Margin = new Thickness(0, 0, 0, 10),
            Child = _securityOverviewText
        };
        var securityContent = new StackPanel();
        securityContent.Children.Add(overviewCard);
        securityContent.Children.Add(_policySecurityExpander);
        securityContent.Children.Add(_jwtSecurityExpander);
        _securityTab.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = securityContent
        };

        DetailTabs.Items.Clear();
        DetailTabs.Items.Add(DiagnosisTab);
        DetailTabs.Items.Add(_requestTab);
        DetailTabs.Items.Add(_responseTab);
        DetailTabs.Items.Add(timingTab);
        DetailTabs.Items.Add(_securityTab);
        DetailTabs.Items.Add(_comparisonTab);
    }

    public void Initialize(NetworkCaptureService capture)
    {
        _capture = capture;
        DataContext = capture;
        _requestsView = CollectionViewSource.GetDefaultView(capture.Requests);
        _requestsView.Filter = MatchesFilter;
        if (_requestsView is ListCollectionView requestListView)
            requestListView.CustomSort = PinnedRequestComparer.Instance;
        capture.Requests.CollectionChanged += Requests_CollectionChanged;
        RequestList.ItemsSource = _requestsView;
        UpdateRequestCount();
        UpdateProblemInbox();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not CapturedNetworkRequest request) return false;
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(search) && !request.Url.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        if (_problemCategory != NetworkProblemCategory.All && !NetworkInsightAnalyzer.Categories(request, _capture?.Requests.ToArray() ?? []).Contains(_problemCategory)) return false;
        return FilterBox?.SelectedIndex switch
        {
            1 => request.ResourceType is "XHR" or "Fetch",
            2 => request.ResourceType == "Document",
            3 => request.ResourceType == "Script",
            4 => request.ResourceType == "Stylesheet",
            5 => request.ResourceType == "Image",
            6 => request.IsFailed,
            _ => true
        };
    }

    private void Requests_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (CapturedNetworkRequest request in e.NewItems) request.PropertyChanged += Request_PropertyChanged;
        if (e.OldItems is not null)
            foreach (CapturedNetworkRequest request in e.OldItems) request.PropertyChanged -= Request_PropertyChanged;
        if (_comparisonBaseRequest is not null && e.OldItems?.Cast<CapturedNetworkRequest>().Any(request => ReferenceEquals(request, _comparisonBaseRequest)) == true)
            CancelComparison();
        UpdateRequestCount();
        UpdateProblemInbox();
        TrafficEmptyText.Visibility = _capture?.Requests.Count > 0 && _requestsView?.Cast<object>().Any() == false ? Visibility.Visible : _capture?.Requests.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Request_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CapturedNetworkRequest.IsFailed) or nameof(CapturedNetworkRequest.StatusCode) or nameof(CapturedNetworkRequest.DurationMs) or nameof(CapturedNetworkRequest.ResponseContentType) or nameof(CapturedNetworkRequest.ResponseBody) or nameof(CapturedNetworkRequest.Timing) or nameof(CapturedNetworkRequest.IsPinned))
        {
            RefreshFilter();
            if (_selectedRequest is not null && ReferenceEquals(sender, _selectedRequest)) PopulateDiagnosis(_selectedRequest);
        }
    }
    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFilter();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilter();
    private void RefreshFilter() { _requestsView?.Refresh(); UpdateRequestCount(); UpdateProblemInbox(); }
    private void UpdateRequestCount()
    {
        if (RequestCountText is null) return;
        var visible = _requestsView?.Cast<object>().Count() ?? 0;
        var total = _capture?.Requests.Count ?? 0;
        RequestCountText.Text = visible == total ? $"{total:N0} requests" : $"{visible:N0} of {total:N0}";
        TrafficEmptyText.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CancelComparison();
        ShowComparisonGuidance();
        _capture?.Clear();
        ClearDetail();
    }
    private void PreserveLogBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_capture is not null) _capture.PreserveLog = PreserveLogBox.IsChecked == true;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void DockBottom_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke(this, NetworkInspectorDock.Bottom);
    private void DockRight_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke(this, NetworkInspectorDock.Right);

    public void SetDock(NetworkInspectorDock dock)
    {
        RememberCurrentSplit();
        _currentDock = dock;
        DockBottomButton.Visibility = dock == NetworkInspectorDock.Bottom ? Visibility.Collapsed : Visibility.Visible;
        DockRightButton.Visibility = dock == NetworkInspectorDock.Right ? Visibility.Collapsed : Visibility.Visible;
        ProblemInboxHost.Visibility = dock == NetworkInspectorDock.Bottom ? Visibility.Visible : Visibility.Collapsed;
        FocusFilterHost.Visibility = dock == NetworkInspectorDock.Right ? Visibility.Visible : Visibility.Collapsed;
        DetailSectionHost.Visibility = dock == NetworkInspectorDock.Right ? Visibility.Visible : Visibility.Collapsed;
        DetailTabs.Style = (Style)FindResource(dock == NetworkInspectorDock.Right ? "DetailTabsCompact" : "DetailTabs");
        ConfigureToolbar(dock);

        InspectorContentGrid.ColumnDefinitions.Clear();
        InspectorContentGrid.RowDefinitions.Clear();
        if (dock == NetworkInspectorDock.Bottom)
        {
            InspectorContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_bottomRequestRatio, GridUnitType.Star), MinWidth = 180 });
            InspectorContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            InspectorContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - _bottomRequestRatio, GridUnitType.Star), MinWidth = 180 });
            Grid.SetRow(RequestListPanel, 0); Grid.SetColumn(RequestListPanel, 0);
            Grid.SetRow(InspectorDetailSplitter, 0); Grid.SetColumn(InspectorDetailSplitter, 1);
            Grid.SetRow(RequestDetailPanel, 0); Grid.SetColumn(RequestDetailPanel, 2);
            InspectorDetailSplitter.ResizeDirection = GridResizeDirection.Columns;
            InspectorDetailSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorDetailSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            InspectorDetailSplitter.Cursor = Cursors.SizeWE;
            InspectorDetailSplitter.Width = 10; InspectorDetailSplitter.Height = double.NaN;
        }
        else
        {
            InspectorContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_rightRequestRatio, GridUnitType.Star), MinHeight = 120 });
            InspectorContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            InspectorContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - _rightRequestRatio, GridUnitType.Star), MinHeight = 120 });
            Grid.SetRow(RequestListPanel, 0); Grid.SetColumn(RequestListPanel, 0);
            Grid.SetRow(InspectorDetailSplitter, 1); Grid.SetColumn(InspectorDetailSplitter, 0);
            Grid.SetRow(RequestDetailPanel, 2); Grid.SetColumn(RequestDetailPanel, 0);
            InspectorDetailSplitter.ResizeDirection = GridResizeDirection.Rows;
            InspectorDetailSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorDetailSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            InspectorDetailSplitter.Cursor = Cursors.SizeNS;
            InspectorDetailSplitter.Width = double.NaN; InspectorDetailSplitter.Height = 8;
        }
    }

    private void InspectorDetailSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => RememberCurrentSplit();

    private void RememberCurrentSplit()
    {
        if (_currentDock == NetworkInspectorDock.Bottom && InspectorContentGrid.ColumnDefinitions.Count == 3)
        {
            var requestWidth = InspectorContentGrid.ColumnDefinitions[0].ActualWidth;
            var detailWidth = InspectorContentGrid.ColumnDefinitions[2].ActualWidth;
            if (requestWidth + detailWidth > 0) _bottomRequestRatio = requestWidth / (requestWidth + detailWidth);
        }
        else if (_currentDock == NetworkInspectorDock.Right && InspectorContentGrid.RowDefinitions.Count == 3)
        {
            var requestHeight = InspectorContentGrid.RowDefinitions[0].ActualHeight;
            var detailHeight = InspectorContentGrid.RowDefinitions[2].ActualHeight;
            if (requestHeight + detailHeight > 0) _rightRequestRatio = requestHeight / (requestHeight + detailHeight);
        }
    }

    private void DetailSectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingDetailSection || DetailSectionBox.SelectedItem is not DetailSectionItem section) return;
        _synchronizingDetailSection = true;
        try
        {
            section.Tab.IsSelected = true;
        }
        finally
        {
            _synchronizingDetailSection = false;
        }
    }

    private void DetailTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingDetailSection || !ReferenceEquals(e.OriginalSource, DetailTabs) || DetailTabs.SelectedItem is not TabItem selectedTab) return;
        var section = _detailSections.FirstOrDefault(candidate => ReferenceEquals(candidate.Tab, selectedTab));
        if (section is null) return;

        _synchronizingDetailSection = true;
        try
        {
            DetailSectionBox.SelectedItem = section;
        }
        finally
        {
            _synchronizingDetailSection = false;
        }
    }

    private void ConfigureToolbar(NetworkInspectorDock dock)
    {
        NetworkToolbar.RowDefinitions.Clear();
        NetworkToolbar.ColumnDefinitions.Clear();
        ResetToolbarPlacement();

        if (dock == NetworkInspectorDock.Right)
        {
            ConfigureRightToolbar();
            return;
        }

        ConfigureBottomToolbar();
    }

    private void ConfigureRightToolbar()
    {
        NetworkToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        NetworkToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        NetworkToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        NetworkToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        NetworkToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        NetworkToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Place(NetworkTitleBlock, 0, 0);
        Place(DockBottomButton, 0, 1);
        Place(CloseButton, 0, 2);

        Place(SearchBox, 1, 0);
        SearchBox.Width = double.NaN;
        SearchBox.MinWidth = 150;
        SearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        SearchBox.Margin = new Thickness(0, 9, 12, 0);
        Place(PreserveLogBox, 1, 1);
        PreserveLogBox.Margin = new Thickness(0, 9, 12, 0);
        Place(ClearButton, 1, 2);
        ClearButton.Margin = new Thickness(0, 9, 0, 0);

        Place(FilterBox, 2, 0);
        FilterBox.Width = 150;
        FilterBox.HorizontalAlignment = HorizontalAlignment.Left;
        FilterBox.Margin = new Thickness(0, 9, 0, 0);
        Place(RequestCountText, 2, 1, columnSpan: 2);
        RequestCountText.HorizontalAlignment = HorizontalAlignment.Right;
        RequestCountText.Margin = new Thickness(12, 9, 0, 0);

        DockBottomButton.Margin = new Thickness(8, 0, 8, 0);
    }

    private void ConfigureBottomToolbar()
    {
        NetworkToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < 8; index++) NetworkToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        NetworkToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        NetworkToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Place(NetworkTitleBlock, 0, 0);
        Place(FilterBox, 0, 1);
        Place(SearchBox, 0, 2);
        Place(RequestCountText, 0, 3);
        Place(PreserveLogBox, 0, 4);
        Place(ClearButton, 0, 5);
        Place(DockBottomButton, 0, 6);
        Place(DockRightButton, 0, 7);
        Place(CloseButton, 0, 9);

        NetworkTitleBlock.Margin = new Thickness(0, 0, 16, 0);
        FilterBox.Width = 128;
        FilterBox.Margin = new Thickness(0, 0, 8, 0);
        SearchBox.Width = 205;
        SearchBox.MinWidth = 0;
        SearchBox.HorizontalAlignment = HorizontalAlignment.Left;
        SearchBox.Margin = new Thickness(0, 0, 9, 0);
        RequestCountText.HorizontalAlignment = HorizontalAlignment.Left;
        RequestCountText.Margin = new Thickness(0, 0, 10, 0);
        PreserveLogBox.Margin = new Thickness(0, 0, 11, 0);
        ClearButton.Margin = new Thickness(0, 0, 7, 0);
        DockBottomButton.Margin = new Thickness(0, 0, 7, 0);
        DockRightButton.Margin = new Thickness(0, 0, 7, 0);
    }

    private void ResetToolbarPlacement()
    {
        foreach (var element in new FrameworkElement[] { NetworkTitleBlock, FilterBox, SearchBox, RequestCountText, PreserveLogBox, ClearButton, DockBottomButton, DockRightButton, CloseButton })
        {
            Grid.SetRow(element, 0);
            Grid.SetColumn(element, 0);
            Grid.SetRowSpan(element, 1);
            Grid.SetColumnSpan(element, 1);
            element.Margin = new Thickness(0);
            element.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private static void Place(FrameworkElement element, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private async void RequestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRequest = RequestList.SelectedItem as CapturedNetworkRequest;
        if (_selectedRequest is null) { ClearDetail(); return; }
        PopulateDetail(_selectedRequest);
        await UpdatePolicyAnalysisAsync(_selectedRequest);
        if (_capture is not null)
        {
            await _capture.EnsureResponseBodyAsync(_selectedRequest);
            if (_selectedRequest == RequestList.SelectedItem) PopulateResponse(_selectedRequest);
        }
        if (_comparisonAwaitingTarget && _comparisonBaseRequest is not null && !ReferenceEquals(_comparisonBaseRequest, _selectedRequest))
            await CompleteComparisonAsync(_comparisonBaseRequest, _selectedRequest);
    }

    private void PopulateDetail(CapturedNetworkRequest request)
    {
        DetailMethodText.Text = request.Method;
        DetailMethodText.Foreground = HttpMethodPalette.Foreground(request.Method);
        DetailMethodBadge.Background = HttpMethodPalette.Background(request.Method);
        DetailUrlText.Text = request.Url;
        DetailStatusText.Text = request.StatusText;
        DetailStatusBadge.Background = new SolidColorBrush(request.IsFailed ? Color.FromRgb(128, 56, 64) : Color.FromRgb(29, 100, 70));
        DetailTimingText.Text = $"{request.ResourceType} · {request.DurationText}";
        PopulateDiagnosis(request);
        _requestViewer.SetRequest(request.Method, request.Url, request.QueryParameters(), request.RequestBody, request.RequestContentType, request.RequestHeaders);
        PopulateResponse(request);
        TimingBox.Text = BuildTimingReport(request);
        OpenInRestButton.IsEnabled = true;
        _pinButton.IsEnabled = true;
        _pinButton.Content = request.IsPinned ? "Unpin" : "Pin";
        _compareButton.IsEnabled = true;
        JwtInspectButton.IsEnabled = TryGetAuthorizationHeader(request, out var authorizationHeader) && JwtTokenInspector.IsBearerJwt(authorizationHeader);
        _jwtSecurityHeader.Text = JwtInspectButton.IsEnabled ? "JWT · Token detected" : "JWT · No token detected";
        _jwtSecurityExpander.IsEnabled = JwtInspectButton.IsEnabled;
        _securityOverviewText.Text = BuildSecurityOverview(request, JwtInspectButton.IsEnabled);
        ClearJwtDetail();
        PolicyAnalysisBox.Text = BuildPolicyReport(request).ToDisplayText();
    }

    private void PopulateResponse(CapturedNetworkRequest request) => _responseViewer.SetResponse(
        request.StatusCode, request.IsFailed, request.ResponseContentType, request.DurationText, request.Protocol,
        request.ResponseHeaders, request.ResponseBody);

    private static string BuildSecurityOverview(CapturedNetworkRequest request, bool hasJwt)
    {
        var policyState = request.BlockedReason?.Contains("csp", StringComparison.OrdinalIgnoreCase) == true
            ? "CSP blocked this request."
            : !string.IsNullOrWhiteSpace(request.CorsError) || request.BlockedReason?.Contains("cors", StringComparison.OrdinalIgnoreCase) == true
                ? "A CORS failure was reported by the browser."
                : "No CORS or CSP block was reported for this request.";
        var jwtState = hasJwt ? "A Bearer JWT is available for local inspection." : "No Bearer JWT was detected.";
        return $"{policyState} {jwtState}";
    }

    private void PopulateDiagnosis(CapturedNetworkRequest request)
    {
        var all = _capture?.Requests.ToArray() ?? [];
        var findings = NetworkInsightAnalyzer.Findings(request, all);
        FindingsList.ItemsSource = findings.Select(FindingItem.From).ToList();
        StoryList.ItemsSource = NetworkInsightAnalyzer.Story(request, all).Select(StoryItem.From).ToList();
        var related = NetworkInsightAnalyzer.Related(request, all).Select(RelatedItem.From).ToList();
        RelatedRequestsList.ItemsSource = related;
        RelatedRequestsList.SelectedItem = null;
        NoRelatedText.Visibility = related.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RelatedHintText.Visibility = related.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        DiagnosisHeadlineText.Text = findings[0].Title;
        DiagnosisHeadlineText.Foreground = Accent(findings[0].Severity);
    }

    private void ClearDetail()
    {
        DetailMethodText.Text = "Select a request"; DetailUrlText.Text = string.Empty; DetailStatusText.Text = "No request selected"; DetailTimingText.Text = string.Empty;
        DetailMethodBadge.Background = new SolidColorBrush(Color.FromRgb(37, 53, 72));
        DiagnosisHeadlineText.Text = "Select traffic to begin diagnosis";
        DiagnosisHeadlineText.Foreground = (Brush)FindResource("AccentBrush");
        FindingsList.ItemsSource = null; StoryList.ItemsSource = null; RelatedRequestsList.ItemsSource = null;
        NoRelatedText.Visibility = Visibility.Visible; RelatedHintText.Visibility = Visibility.Collapsed;
        TimingBox.Text = string.Empty;
        _requestViewer.Clear();
        _responseViewer.Clear();
        _securityOverviewText.Text = "Select a request to inspect its browser security evidence.";
        _jwtSecurityHeader.Text = "JWT · No token detected";
        _jwtSecurityExpander.IsEnabled = false;
        PolicyAnalysisBox.Text = string.Empty;
        OpenInRestButton.IsEnabled = false;
        _pinButton.IsEnabled = false;
        _pinButton.Content = "Pin";
        _compareButton.IsEnabled = false;
        JwtInspectButton.IsEnabled = false;
        ClearJwtDetail();
    }

    private void OpenInRest_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRequest is not null) OpenInRestClientRequested?.Invoke(this, _selectedRequest);
    }

    private void ConfigureRequestActions()
    {
        if (OpenInRestButton.Parent is not StackPanel actions) return;
        foreach (var button in new[] { _pinButton, _compareButton })
        {
            button.Style = (Style)FindResource("GhostButton");
            button.Margin = new Thickness(0, 0, 7, 0);
            button.IsEnabled = false;
        }
        _pinButton.ToolTip = "Keep this request across navigation and place it at the top";
        _compareButton.ToolTip = "Use this request as the comparison baseline";
        _pinButton.Click += PinRequest_Click;
        _compareButton.Click += CompareRequest_Click;
        actions.Children.Insert(0, _compareButton);
        actions.Children.Insert(0, _pinButton);
    }

    private void PinRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRequest is null) return;
        _selectedRequest.IsPinned = !_selectedRequest.IsPinned;
        _pinButton.Content = _selectedRequest.IsPinned ? "Unpin" : "Pin";
        _requestsView?.Refresh();
        RequestList.SelectedItem = _selectedRequest;
        RequestList.ScrollIntoView(_selectedRequest);
    }

    private void CompareRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_comparisonAwaitingTarget)
        {
            CancelComparison();
            PopulateDiagnosis(_selectedRequest!);
            return;
        }
        if (_selectedRequest is null) return;
        _comparisonBaseRequest = _selectedRequest;
        _comparisonAwaitingTarget = true;
        _compareButton.Content = "Cancel";
        _compareButton.ToolTip = "Cancel request comparison";
        DiagnosisHeadlineText.Text = $"Baseline selected: {_selectedRequest.DisplayName}. Select another request to compare.";
        DiagnosisHeadlineText.Foreground = (Brush)FindResource("AccentBrush");
    }

    private async Task CompleteComparisonAsync(CapturedNetworkRequest baseline, CapturedNetworkRequest target)
    {
        if (_capture is not null)
        {
            await _capture.EnsureResponseBodyAsync(baseline);
            await _capture.EnsureResponseBodyAsync(target);
        }
        if (!ReferenceEquals(target, _selectedRequest)) return;
        ShowComparisonReport(BuildComparisonReport(baseline, target));
        _comparisonTab.IsSelected = true;
        CancelComparison(clearBaseline: false);
    }

    private void ShowComparisonGuidance()
    {
        var document = CreateComparisonDocument();
        document.Blocks.Add(new Paragraph(new Run("HOW TO COMPARE"))
        {
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        document.Blocks.Add(new Paragraph(new Run(ComparisonGuidance["HOW TO COMPARE\n\n".Length..]))
        {
            Foreground = (Brush)FindResource("MutedTextBrush"),
            LineHeight = 20,
            Margin = new Thickness(0)
        });
        _comparisonBox.Document = document;
    }

    private void ShowComparisonReport(string report)
    {
        var baselineBrush = Frozen(91, 214, 255);
        var comparisonBrush = Frozen(255, 180, 84);
        var primaryBrush = (Brush)FindResource("PrimaryTextBrush");
        var mutedBrush = (Brush)FindResource("MutedTextBrush");
        var document = CreateComparisonDocument();
        var legend = new Paragraph { Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold };
        legend.Inlines.Add(new Run("A  BASELINE") { Foreground = baselineBrush });
        legend.Inlines.Add(new Run("       ") { Foreground = mutedBrush });
        legend.Inlines.Add(new Run("B  COMPARISON") { Foreground = comparisonBrush });
        document.Blocks.Add(legend);

        foreach (var line in report.Split('\n').Select(value => value.TrimEnd('\r')))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(line) ? 7 : 3) };
            var trimmed = line.TrimStart();
            if (IsBaselineComparisonLine(trimmed))
                paragraph.Inlines.Add(new Run(line) { Foreground = baselineBrush });
            else if (IsTargetComparisonLine(trimmed))
                paragraph.Inlines.Add(new Run(line) { Foreground = comparisonBrush });
            else if (TryAddColoredTransition(paragraph, line, baselineBrush, comparisonBrush, mutedBrush))
            {
                // The transition formatter added individually colored A and B values.
            }
            else
                paragraph.Inlines.Add(new Run(line) { Foreground = IsComparisonHeading(line) ? primaryBrush : mutedBrush, FontWeight = IsComparisonHeading(line) ? FontWeights.SemiBold : FontWeights.Normal });
            document.Blocks.Add(paragraph);
        }
        _comparisonBox.Document = document;
    }

    private FlowDocument CreateComparisonDocument() => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = _comparisonBox.FontFamily,
        FontSize = _comparisonBox.FontSize
    };

    private static bool TryAddColoredTransition(Paragraph paragraph, string line, Brush baseline, Brush comparison, Brush muted)
    {
        var colon = line.IndexOf(':');
        var arrow = line.IndexOf("  →  ", StringComparison.Ordinal);
        if (colon < 0 || arrow <= colon) return false;
        paragraph.Inlines.Add(new Run(line[..(colon + 1)] + " ") { Foreground = muted });
        paragraph.Inlines.Add(new Run(line[(colon + 1)..arrow].Trim()) { Foreground = baseline });
        paragraph.Inlines.Add(new Run("  →  ") { Foreground = muted });
        paragraph.Inlines.Add(new Run(line[(arrow + 5)..].Trim()) { Foreground = comparison });
        return true;
    }

    private static bool IsComparisonHeading(string line) => line.Length > 0 && line == line.ToUpperInvariant() && line.Any(char.IsLetter);

    private static bool IsBaselineComparisonLine(string line) =>
        line.StartsWith("A  ", StringComparison.Ordinal) || line.StartsWith("A:", StringComparison.Ordinal) || line.StartsWith("A (", StringComparison.Ordinal);

    private static bool IsTargetComparisonLine(string line) =>
        line.StartsWith("B  ", StringComparison.Ordinal) || line.StartsWith("B:", StringComparison.Ordinal) || line.StartsWith("B (", StringComparison.Ordinal);

    private void CancelComparison(bool clearBaseline = true)
    {
        _comparisonAwaitingTarget = false;
        if (clearBaseline) _comparisonBaseRequest = null;
        _compareButton.Content = "Compare";
        _compareButton.ToolTip = "Use this request as the comparison baseline";
    }

    private void ProblemInbox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingProblemFilter || ProblemInbox.SelectedItem is not ProblemCategoryItem selected) return;
        SelectProblemCategory(selected);
    }

    private void FocusFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingProblemFilter || FocusFilterBox.SelectedItem is not ProblemCategoryItem selected) return;
        SelectProblemCategory(selected);
    }

    private void SelectProblemCategory(ProblemCategoryItem selected)
    {
        _problemCategory = selected.Category;
        ActiveProblemLabel.Text = selected.Category == NetworkProblemCategory.All ? "All captured activity" : selected.Label;
        SynchronizeProblemFilterSelection(selected);
        _requestsView?.Refresh();
        UpdateRequestCount();
    }

    private void SynchronizeProblemFilterSelection(ProblemCategoryItem selected)
    {
        _synchronizingProblemFilter = true;
        try
        {
            ProblemInbox.SelectedItem = selected;
            FocusFilterBox.SelectedItem = selected;
        }
        finally { _synchronizingProblemFilter = false; }
    }

    private void RelatedRequestsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_followingRelatedRequest || RelatedRequestsList.SelectedItem is not RelatedItem related) return;
        _followingRelatedRequest = true;
        try
        {
            SelectProblemCategory(_problemCategories[0]);
            RequestList.SelectedItem = related.Request;
            RequestList.ScrollIntoView(related.Request);
            DiagnosisTab.IsSelected = true;
        }
        finally { _followingRelatedRequest = false; }
    }

    private void UpdateProblemInbox()
    {
        // Filter selection events can fire while InitializeComponent is still building the visual tree.
        if (ProblemInbox is null || FocusFilterBox is null) return;
        var requests = _capture?.Requests.ToArray() ?? [];
        var selected = _problemCategory;
        _problemCategories.Clear();
        foreach (var category in Enum.GetValues<NetworkProblemCategory>())
        {
            var count = category == NetworkProblemCategory.All ? requests.Length : requests.Count(request => NetworkInsightAnalyzer.Categories(request, requests).Contains(category));
            _problemCategories.Add(new(category, CategoryLabel(category), count, CategoryAccent(category)));
        }
        ProblemInbox.Items.Refresh();
        FocusFilterBox.Items.Refresh();
        SynchronizeProblemFilterSelection(_problemCategories.FirstOrDefault(item => item.Category == selected) ?? _problemCategories[0]);
        ActiveProblemLabel.Text = selected == NetworkProblemCategory.All ? "All captured activity" : CategoryLabel(selected);
        UpdateProblemSummary(requests);
    }

    private void UpdateProblemSummary(IReadOnlyList<CapturedNetworkRequest> requests)
    {
        if (ProblemSummaryText is null) return;
        if (requests.Count == 0)
        {
            ProblemSummaryText.Text = "Waiting for traffic";
            ProblemSummaryText.Foreground = (Brush)FindResource("MutedTextBrush");
            return;
        }

        var problems = _problemCategories
            .Where(item => item.Category is not NetworkProblemCategory.All and not NetworkProblemCategory.Healthy && item.Count > 0)
            .Select(item => $"{item.Count:N0} {SummaryLabel(item.Category, item.Count)}")
            .ToList();
        ProblemSummaryText.Text = problems.Count == 0 ? "No detected problems" : string.Join(" · ", problems);
        ProblemSummaryText.Foreground = problems.Count == 0 ? CategoryAccent(NetworkProblemCategory.Healthy) : CategoryAccent(NetworkProblemCategory.Suspicious);
    }

    private static string SummaryLabel(NetworkProblemCategory category, int count) => category switch
    {
        NetworkProblemCategory.Broken => "broken",
        NetworkProblemCategory.Suspicious => "suspicious",
        NetworkProblemCategory.Slow => "slow",
        NetworkProblemCategory.Authentication => "auth",
        NetworkProblemCategory.Content => "content",
        NetworkProblemCategory.Cache => count == 1 ? "cache issue" : "cache issues",
        _ => CategoryLabel(category).ToLowerInvariant()
    };

    private static string CategoryLabel(NetworkProblemCategory category) => category switch
    {
        NetworkProblemCategory.All => "All",
        NetworkProblemCategory.Content => "Unexpected content",
        _ => category.ToString()
    };

    private static Brush CategoryAccent(NetworkProblemCategory category) => category switch
    {
        NetworkProblemCategory.Broken => Frozen(239, 102, 102),
        NetworkProblemCategory.Suspicious => Frozen(245, 176, 65),
        NetworkProblemCategory.Slow => Frozen(238, 147, 75),
        NetworkProblemCategory.Authentication => Frozen(179, 139, 250),
        NetworkProblemCategory.Content => Frozen(255, 126, 182),
        NetworkProblemCategory.Cache => Frozen(84, 194, 205),
        NetworkProblemCategory.Healthy => Frozen(97, 208, 149),
        _ => Frozen(91, 214, 255)
    };

    private static Brush Accent(NetworkFindingSeverity severity) => severity switch
    {
        NetworkFindingSeverity.Error => Frozen(239, 102, 102),
        NetworkFindingSeverity.Warning => Frozen(245, 176, 65),
        NetworkFindingSeverity.Good => Frozen(97, 208, 149),
        _ => Frozen(91, 214, 255)
    };

    private static Brush Badge(NetworkFindingSeverity severity) => severity switch
    {
        NetworkFindingSeverity.Error => Frozen(65, 31, 40),
        NetworkFindingSeverity.Warning => Frozen(61, 47, 25),
        NetworkFindingSeverity.Good => Frozen(24, 55, 43),
        _ => Frozen(24, 48, 65)
    };

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record ProblemCategoryItem(NetworkProblemCategory Category, string Label, int Count, Brush Accent);
    private sealed record FindingItem(string Category, string Title, string Explanation, string NextStep, Brush Accent, Brush BadgeBackground)
    {
        public static FindingItem From(NetworkFinding finding) => new(NetworkInspectorView.CategoryLabel(finding.Category), finding.Title, finding.Explanation, finding.NextStep, NetworkInspectorView.Accent(finding.Severity), NetworkInspectorView.Badge(finding.Severity));
    }
    private sealed record StoryItem(string Stage, string Detail, Brush Accent)
    {
        public static StoryItem From(NetworkStoryStep step) => new(step.Stage, step.Detail, NetworkInspectorView.Accent(step.Severity));
    }
    private sealed record RelatedItem(CapturedNetworkRequest Request, string Relationship, string Name, string Summary, Brush Accent)
    {
        public static RelatedItem From(RelatedNetworkRequest related) => new(related.Request, related.Relationship, related.Request.DisplayName, related.Summary, NetworkInspectorView.CategoryAccent(NetworkInsightAnalyzer.PrimaryCategory(related.Request)));
    }

    private void InspectJwt_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRequest is null || !TryGetAuthorizationHeader(_selectedRequest, out var authorizationHeader) ||
            !JwtTokenInspector.TryInspectBearerToken(authorizationHeader, out var inspection) || inspection is null)
        {
            ClearJwtDetail("The Bearer value is not a decodable JWT.");
            ShowSecurityJwt();
            return;
        }

        ShowJwtInspection(inspection);
    }

    /// <summary>Displays a decoded storage JWT in the existing inspector UI without sending it anywhere.</summary>
    public void InspectJwtToken(string token)
    {
        if (!JwtTokenInspector.TryInspectToken(token, out var inspection) || inspection is null)
        {
            ClearJwtDetail("The stored value is not a decodable JWT.");
            ShowSecurityJwt();
            return;
        }

        ShowJwtInspection(inspection);
    }

    private void ShowJwtInspection(JwtTokenInspection inspection)
    {
        JwtTimeStatusText.Text = inspection.TimeStatus switch
        {
            JwtTimeStatus.Expired => "Expired",
            JwtTimeStatus.NotValidYet => "Not valid yet",
            _ => "Valid time range"
        };
        JwtTimeStatusBadge.Background = new SolidColorBrush(inspection.TimeStatus switch
        {
            JwtTimeStatus.Expired => Color.FromRgb(128, 56, 64),
            JwtTimeStatus.NotValidYet => Color.FromRgb(109, 77, 35),
            _ => Color.FromRgb(29, 100, 70)
        });
        JwtSignatureText.Text = inspection.SignatureStatus;
        JwtIssuerText.Text = DisplayClaim(inspection.Issuer);
        JwtAudienceText.Text = DisplayClaim(inspection.Audience);
        JwtSubjectText.Text = DisplayClaim(inspection.Subject);
        JwtScopesText.Text = DisplayClaim(inspection.Scopes);
        JwtRolesText.Text = DisplayClaim(inspection.Roles);
        JwtIssuedAtText.Text = JwtTokenInspection.LocalTime(inspection.IssuedAt);
        JwtNotBeforeText.Text = JwtTokenInspection.LocalTime(inspection.NotBefore);
        JwtExpiresText.Text = JwtTokenInspection.LocalTime(inspection.ExpiresAt);
        JwtRemainingText.Text = inspection.TimeRemaining;
        JwtHeaderBox.Text = inspection.HeaderJson;
        JwtPayloadBox.Text = inspection.PayloadJson;
        ShowSecurityJwt();
    }

    private void ShowSecurityJwt()
    {
        _securityTab.IsSelected = true;
        _jwtSecurityExpander.IsEnabled = true;
        _jwtSecurityExpander.IsExpanded = true;
    }

    private void ClearJwtDetail(string? status = null)
    {
        JwtTimeStatusText.Text = status ?? "JWT not inspected";
        JwtTimeStatusBadge.Background = new SolidColorBrush(Color.FromRgb(38, 50, 56));
        JwtSignatureText.Text = "Signature: not validated";
        JwtIssuerText.Text = JwtAudienceText.Text = JwtSubjectText.Text = JwtScopesText.Text = JwtRolesText.Text = "—";
        JwtIssuedAtText.Text = JwtNotBeforeText.Text = JwtExpiresText.Text = JwtRemainingText.Text = "—";
        JwtHeaderBox.Text = JwtPayloadBox.Text = string.Empty;
    }

    private static bool TryGetAuthorizationHeader(CapturedNetworkRequest request, out string? authorizationHeader) =>
        request.RequestHeaders.TryGetValue("Authorization", out authorizationHeader);

    private static string DisplayClaim(string? value) => string.IsNullOrWhiteSpace(value) ? "Not present" : value;

    private static string BuildTimingReport(CapturedNetworkRequest request)
    {
        var timing = request.Timing;
        var endpoint = string.IsNullOrWhiteSpace(request.RemoteAddress) ? "Not recorded" : request.RemoteAddress + (request.RemotePort is null ? string.Empty : $":{request.RemotePort}");
        var lines = new List<string>
        {
            "RECORDED BY CHROMIUM (CDP)",
            $"Total:                 {request.DurationText}",
            $"Queued / blocked:      {TimingValue(timing.BlockedMs)}",
            $"DNS lookup:            {TimingValue(timing.DnsMs)}",
            $"Connection (incl. TLS):{TimingValue(timing.ConnectMs)}",
            $"TLS negotiation:       {TimingValue(timing.TlsMs)}",
            $"Request upload:        {TimingValue(timing.SendMs)}",
            $"Waiting for server:    {TimingValue(timing.WaitMs)}",
            $"Response download:     {TimingValue(timing.ReceiveMs)}",
            string.Empty,
            "CONNECTION",
            $"Protocol:              {request.Protocol}",
            $"Remote endpoint:       {endpoint}",
            $"Connection reused:     {(request.ConnectionReused ? "Yes" : "No / not recorded")}",
            $"Response source:       {request.CacheSource}",
            string.Empty,
            "No phase is estimated. Durations are calculated only from Chromium-recorded timestamps.",
            "Unavailable phases are shown as Not recorded."
        };
        if (!string.IsNullOrEmpty(request.FailureReason)) lines.Add($"Failure: {request.FailureReason}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildComparisonReport(CapturedNetworkRequest baseline, CapturedNetworkRequest target)
    {
        var report = new StringBuilder();
        report.AppendLine("REQUEST COMPARISON");
        report.AppendLine($"A  {baseline.Method} {baseline.Url}");
        report.AppendLine($"B  {target.Method} {target.Url}");
        report.AppendLine();

        var differenceCount = 0;
        AddDifference(report, "Method", baseline.Method, target.Method, ref differenceCount);
        AddDifference(report, "URL", baseline.Url, target.Url, ref differenceCount);
        AddDifference(report, "Status", baseline.StatusText, target.StatusText, ref differenceCount);
        AddDifference(report, "Content type", baseline.ResponseContentType, target.ResponseContentType, ref differenceCount);
        AddDifference(report, "Protocol", baseline.Protocol, target.Protocol, ref differenceCount);
        AddTimingDifference(report, "Total time", baseline.DurationMs, target.DurationMs, ref differenceCount);

        AppendHeaderDifferences(report, "REQUEST HEADERS", baseline.RequestHeaders, target.RequestHeaders, ref differenceCount);
        AppendHeaderDifferences(report, "RESPONSE HEADERS", baseline.ResponseHeaders, target.ResponseHeaders, ref differenceCount);
        AppendBodyDifference(report, "REQUEST BODY", baseline.RequestBody, target.RequestBody, ref differenceCount);
        AppendBodyDifference(report, "RESPONSE BODY", baseline.ResponseBody, target.ResponseBody, ref differenceCount);
        AppendTimingDifferences(report, baseline.Timing, target.Timing, ref differenceCount);

        if (differenceCount == 0) report.AppendLine("No meaningful differences were found in the captured data.");
        else report.Insert(report.ToString().IndexOf(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal) + Environment.NewLine.Length,
            $"{differenceCount} meaningful difference{(differenceCount == 1 ? string.Empty : "s")} found.{Environment.NewLine}");
        return report.ToString().TrimEnd();
    }

    private static void AddDifference(StringBuilder report, string label, string? before, string? after, ref int count)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        report.AppendLine($"{label}: {DisplayValue(before)}  →  {DisplayValue(after)}");
        count++;
    }

    private static void AppendHeaderDifferences(StringBuilder report, string title, IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> target, ref int count)
    {
        var changed = baseline.Keys.Union(target.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(name => !string.Equals(baseline.GetValueOrDefault(name), target.GetValueOrDefault(name), StringComparison.Ordinal))
            .ToList();
        if (changed.Count == 0) return;
        report.AppendLine().AppendLine(title);
        foreach (var name in changed)
        {
            var sensitive = name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);
            report.AppendLine($"• {name}");
            report.AppendLine($"  A: {(sensitive ? "(sensitive value changed)" : DisplayValue(baseline.GetValueOrDefault(name)))}");
            report.AppendLine($"  B: {(sensitive ? "(sensitive value changed)" : DisplayValue(target.GetValueOrDefault(name)))}");
            count++;
        }
    }

    private static void AppendBodyDifference(StringBuilder report, string title, string? baseline, string? target, ref int count)
    {
        if (string.Equals(baseline, target, StringComparison.Ordinal)) return;
        report.AppendLine().AppendLine(title);
        report.AppendLine($"A ({baseline?.Length ?? 0:N0} chars): {BodyPreview(baseline)}");
        report.AppendLine($"B ({target?.Length ?? 0:N0} chars): {BodyPreview(target)}");
        count++;
    }

    private static void AppendTimingDifferences(StringBuilder report, NetworkTimingBreakdown baseline, NetworkTimingBreakdown target, ref int count)
    {
        var phases = new (string Label, double? A, double? B)[]
        {
            ("Blocked", baseline.BlockedMs, target.BlockedMs), ("DNS", baseline.DnsMs, target.DnsMs),
            ("Connect", baseline.ConnectMs, target.ConnectMs), ("TLS", baseline.TlsMs, target.TlsMs),
            ("Send", baseline.SendMs, target.SendMs), ("Wait", baseline.WaitMs, target.WaitMs),
            ("Receive", baseline.ReceiveMs, target.ReceiveMs)
        };
        var changed = phases.Where(phase => phase.A != phase.B).ToList();
        if (changed.Count == 0) return;
        report.AppendLine().AppendLine("TIMING PHASES");
        foreach (var phase in changed) AddTimingDifference(report, phase.Label, phase.A, phase.B, ref count);
    }

    private static void AddTimingDifference(StringBuilder report, string label, double? before, double? after, ref int count)
    {
        if (before == after) return;
        var delta = before is not null && after is not null ? $" ({after - before:+0.##;-0.##;0} ms)" : string.Empty;
        report.AppendLine($"{label}: {TimingValue(before)}  →  {TimingValue(after)}{delta}");
        count++;
    }

    private static string BodyPreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(empty)";
        var compact = Regex.Replace(value.Trim(), @"\s+", " ");
        return compact.Length <= 500 ? compact : compact[..500] + "…";
    }

    private static string DisplayValue(string? value) => string.IsNullOrEmpty(value) ? "(not present)" : value;

    private static string TimingValue(double? value) => value is null ? "Not recorded" : $"{value.Value:0.##} ms";

    private async Task UpdatePolicyAnalysisAsync(CapturedNetworkRequest request)
    {
        if (_capture is null) return;
        if (request.BlockedReason?.Contains("csp", StringComparison.OrdinalIgnoreCase) == true)
        {
            var pageDocument = _capture.Requests.LastOrDefault(candidate =>
                candidate.ResourceType.Equals("Document", StringComparison.OrdinalIgnoreCase) &&
                candidate.Url.Equals(request.PageUrl, StringComparison.OrdinalIgnoreCase));
            if (pageDocument is not null) await _capture.EnsureResponseBodyAsync(pageDocument);
        }
        if (request == _selectedRequest) PolicyAnalysisBox.Text = BuildPolicyReport(request).ToDisplayText();
    }

    private NetworkPolicyReport BuildPolicyReport(CapturedNetworkRequest request)
    {
        var captured = _capture?.Requests.ToArray() ?? [];
        var policies = GetPagePolicies(request, captured);
        var current = ToEvidence(request, policies);
        var all = captured.Select(candidate => ToEvidence(candidate, candidate == request ? policies : [])).ToArray();
        return NetworkPolicyAnalyzer.Analyze(current, all);
    }

    private static NetworkRequestEvidence ToEvidence(CapturedNetworkRequest request, IReadOnlyList<string> policies)
    {
        var credentials = request.RequestHeaders.Keys.Any(header => header.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            ? "Credentials may be included (Cookie or Authorization header observed; Fetch credentials mode is not directly exposed)."
            : "Not directly exposed by CDP; no Cookie or Authorization header observed.";
        return new NetworkRequestEvidence(
            request.RequestId, request.Url, request.Method, request.ResourceType, request.StartedAt, request.PageUrl,
            request.RequestHeaders, request.ResponseHeaders, request.StatusCode, request.IsFailed, request.FailureReason,
            request.BlockedReason, request.CorsError, credentials, policies);
    }

    private static IReadOnlyList<string> GetPagePolicies(CapturedNetworkRequest request, IEnumerable<CapturedNetworkRequest> all)
    {
        if (string.IsNullOrWhiteSpace(request.PageUrl)) return [];
        var pageDocuments = all.Where(candidate => candidate.ResourceType.Equals("Document", StringComparison.OrdinalIgnoreCase) &&
                                                   candidate.Url.Equals(request.PageUrl, StringComparison.OrdinalIgnoreCase));
        var policies = new List<string>();
        foreach (var document in pageDocuments)
        {
            foreach (var header in new[] { "Content-Security-Policy", "Content-Security-Policy-Report-Only" })
                if (document.ResponseHeaders.TryGetValue(header, out var policy) && !string.IsNullOrWhiteSpace(policy)) policies.Add(policy);
            if (!string.IsNullOrWhiteSpace(document.ResponseBody)) policies.AddRange(ExtractMetaPolicies(document.ResponseBody));
        }
        return policies;
    }

    private static IEnumerable<string> ExtractMetaPolicies(string html)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!Regex.IsMatch(tag.Value, @"http-equiv\s*=\s*(['""]?)content-security-policy\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            var content = Regex.Match(tag.Value, @"content\s*=\s*(['""])(?<value>.*?)\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (content.Success && !string.IsNullOrWhiteSpace(content.Groups["value"].Value)) yield return content.Groups["value"].Value;
        }
    }

    private sealed record DetailSectionItem(string Label, TabItem Tab);

    private sealed class PinnedRequestComparer : IComparer
    {
        public static PinnedRequestComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is not CapturedNetworkRequest left) return -1;
            if (y is not CapturedNetworkRequest right) return 1;
            var pinned = right.IsPinned.CompareTo(left.IsPinned);
            if (pinned != 0) return pinned;
            var started = left.StartedAt.CompareTo(right.StartedAt);
            return started != 0 ? started : string.Compare(left.RequestId, right.RequestId, StringComparison.Ordinal);
        }
    }
}

public enum NetworkInspectorDock { Bottom, Right }
