using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeveloperBrowser.Core.Collections;

namespace DeveloperBrowser.App;

public partial class RestClientView : UserControl
{
    private readonly HttpClient _httpClient = new();
    private readonly List<RestRequestTab> _requestTabs = [];
    private RestRequestTab? _activeRequestTab;
    private bool _isRestoringTab;
    private ICollectionService? _collections;
    private IEnvironmentService? _environments;
    private IVariableResolver? _variables;
    private ICollectionImportExportService? _collectionImportExport;
    private RestEnvironment? _activeEnvironment;
    private RestEnvironment? _selectedEnvironment;
    private Guid? _savedRequestId;
    private bool _saveAsRequested;
    private bool _isDirty;
    private readonly List<LibraryItem> _libraryItems = [];
    private readonly List<RestCollection> _collectionTreeCollections = [];
    private readonly Dictionary<Guid, bool> _collectionExpansionStates = [];
    private readonly Dictionary<Guid, bool> _folderExpansionStates = [];
    private readonly List<RestEnvironment> _environmentItems = [];
    private RestCollection? _selectedCollection;
    private RestCollectionFolder? _selectedFolder;
    private SavedRestRequest? _selectedSavedRequest;
    private string _newEnvironmentColor = "#4ADE80";
    private CheckBox? _managerVariableSecretBox;
    private Button? _managerVariableSubmitButton;
    private RestEnvironmentVariable? _editingManagerVariable;
    private TextBlock? _environmentManagerActiveText;
    private StackPanel? _collectionTreePanel;
    private TextBox? _collectionManagerSearchBox;
    private TextBlock? _collectionManagerTitle;
    private TextBlock? _collectionManagerDescription;
    private StackPanel? _collectionManagerContent;
    private bool _isCollectionManagerSelection;
    private Grid? _collectionDialog;
    private TextBox? _collectionDialogNameBox;
    private TextBox? _collectionDialogDescriptionBox;
    private TextBlock? _collectionDialogErrorText;
    private TextBlock? _collectionDialogTitle;
    private Button? _collectionDialogSaveButton;
    private RestCollection? _collectionDialogTarget;
    private Grid? _folderDialog;
    private TextBox? _folderDialogNameBox;
    private TextBlock? _folderDialogErrorText;
    private TextBlock? _folderDialogTitle;
    private Button? _folderDialogSaveButton;
    private RestCollection? _folderDialogCollection;
    private RestCollectionFolder? _folderDialogParent;
    private RestCollectionFolder? _folderDialogTarget;
    private RestCollection? _saveDestinationCollection;
    private RestCollectionFolder? _saveDestinationFolder;
    private bool _selectNewCollectionForSaveDestination;
    private bool _selectNewFolderForSaveDestination;

    public event EventHandler<RestClientFooterStatus>? FooterStatusChanged;

    public bool HasUnsavedRequestChanges
    {
        get
        {
            CaptureActiveTab();
            return _requestTabs.Any(tab => tab.IsDirty);
        }
    }

    public ObservableCollection<RequestField> Parameters { get; } = [new()];
    public ObservableCollection<RequestField> Headers { get; } = [new()];

    public RestClientView()
    {
        InitializeComponent();
        ApplySearchStyle(LibrarySearchBox, "Search collections and requests…");
        ApplySearchStyle(EnvironmentSearchBox, "Search environments and variables…");
        ApplySearchStyle(SaveLocationSearchBox, "Search collections and folders…");
        ApplySearchStyle(EnvironmentManagerSearchBox, "Search environments…");
        DataContext = this;
        ConfigureEnvironmentManagerHeader();
        BuildEnvironmentVariableEditor();
        BuildCollectionsWorkspace();
        Loaded += async (_, _) =>
        {
            if (_requestTabs.Count == 0) CreateRequestTab();
            await RefreshLibraryAsync();
            WorkspaceTabs.SelectedIndex = 0;
            SetWorkspace(0);
        };
    }

    public void Configure(ICollectionService collections, IEnvironmentService environments, IVariableResolver variables, ICollectionImportExportService collectionImportExport)
    {
        _collections = collections; _environments = environments; _variables = variables; _collectionImportExport = collectionImportExport;
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        try
        {
            var resolved = ResolveEditorValues();
            var missing = resolved.Missing.Concat(Headers.Where(x => x.IsEnabled).SelectMany(x => ResolveText(x.Value).MissingVariables)).Concat(Parameters.Where(x => x.IsEnabled).SelectMany(x => ResolveText(x.Value).MissingVariables)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException($"Missing environment variable: {string.Join(", ", missing)}");
            var uri = BuildUri(resolved.Url);
            using var request = new HttpRequestMessage(new HttpMethod(SelectedContent(MethodBox)), uri);
            ConfigureAuth(request, resolved.Token, resolved.Username, resolved.Password);
            var body = resolved.Body.Trim();
            if (!string.IsNullOrEmpty(body) && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                request.Content = new StringContent(body, Encoding.UTF8, SelectedContent(ContentTypeBox));

            foreach (var header in Headers.Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.Key)))
            {
                var value = ResolveText(header.Value).Value;
                if (!request.Headers.TryAddWithoutValidation(header.Key, value))
                {
                    request.Content ??= new StringContent(string.Empty);
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            StatusText.Text = "Sending";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(46, 58, 80));
            ResponseMetaText.Text = "Waiting for server…";
            SetFooterStatus("Sending request", "Waiting for the server", isBusy: true);
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request);
            stopwatch.Stop();

            var responseBody = await response.Content.ReadAsStringAsync();
            ResponseDataViewer.SetContent(responseBody, response.Content.Headers.ContentType?.MediaType);
            ResponseHeadersBox.Text = string.Join(Environment.NewLine, response.Headers.Concat(response.Content.Headers).Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"));
            StatusText.Text = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            StatusBadge.Background = new SolidColorBrush(response.IsSuccessStatusCode ? Color.FromRgb(29, 100, 70) : Color.FromRgb(128, 56, 64));
            ResponseMetaText.Text = $"{stopwatch.ElapsedMilliseconds} ms · {responseBody.Length:N0} B";
            SetFooterStatus($"{request.Method} {(int)response.StatusCode}", ResponseMetaText.Text, isFailure: !response.IsSuccessStatusCode);
            CaptureActiveTab();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Request failed";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(128, 56, 64));
            ResponseMetaText.Text = exception.GetType().Name;
            ResponseDataViewer.SetContent(exception.Message, "text/plain");
            ResponseHeadersBox.Text = string.Empty;
            SetFooterStatus("Request failed", exception.GetType().Name, isFailure: true);
            CaptureActiveTab();
        }
    }

    private void CreateRequestTab()
    {
        CaptureActiveTab();
        var tab = new RestRequestTab { Url = _requestTabs.Count == 0 ? "https://thedevbrowser.com/api/space/planets" : string.Empty };
        AddRequestTab(tab);
    }

    private void AddRequestTab(RestRequestTab tab)
    {
        tab.Header = CreateRequestTabHeader(tab);
        _requestTabs.Add(tab);
        RequestTabStrip.Children.Add(tab.Header);
        SelectRequestTab(tab);
    }

    public void OpenCapturedRequest(CapturedNetworkRequest request)
    {
        CaptureActiveTab();
        var url = request.Url;
        var parameters = request.QueryParameters().Select(pair => new RequestField { Key = pair.Key, Value = pair.Value }).ToList();
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)) url = uri.GetLeftPart(UriPartial.Path);
        var tab = new RestRequestTab
        {
            MethodIndex = Math.Max(0, Array.IndexOf(Methods, request.Method.ToUpperInvariant())),
            Url = url,
            Body = request.RequestBody ?? string.Empty,
            Parameters = parameters,
            Headers = request.RequestHeaders.Select(header => new RequestField { Key = header.Key, Value = header.Value }).ToList(),
            ContentTypeIndex = request.RequestContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? 2 : request.RequestContentType.Contains("text", StringComparison.OrdinalIgnoreCase) ? 1 : 0
        };
        AddRequestTab(tab);
    }

    private Border CreateRequestTabHeader(RestRequestTab tab)
    {
        var header = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 0, 4, 0), Height = 36, Cursor = Cursors.Hand };
        var layout = new StackPanel { Orientation = Orientation.Horizontal };
        tab.MethodText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono"), FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 0, 9, 0) };
        tab.TitleText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("PrimaryTextBrush"), FontSize = 12, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "×", Style = (Style)FindResource("RequestTabCloseButton"), Width = 25, Height = 28, FontSize = 15, Margin = new Thickness(7, 0, 0, 0), ToolTip = "Close request tab" };
        close.Click += (_, e) => { e.Handled = true; CloseRequestTab(tab); };
        layout.Children.Add(tab.MethodText);
        layout.Children.Add(tab.TitleText);
        layout.Children.Add(close);
        header.Child = layout;
        header.MouseLeftButtonUp += (_, _) => SelectRequestTab(tab);
        UpdateRequestTabHeader(tab);
        return header;
    }

    private void SelectRequestTab(RestRequestTab tab)
    {
        if (_activeRequestTab == tab) return;
        CaptureActiveTab();
        _activeRequestTab = tab;
        _isRestoringTab = true;
        MethodBox.SelectedIndex = tab.MethodIndex;
        UrlBox.Text = tab.Url;
        ContentTypeBox.SelectedIndex = tab.ContentTypeIndex;
        BodyBox.Text = tab.Body;
        AuthTypeBox.SelectedIndex = tab.AuthTypeIndex;
        TokenBox.Password = tab.Token;
        UsernameBox.Text = tab.Username;
        PasswordBox.Password = tab.Password;
        Parameters.Clear();
        foreach (var field in tab.Parameters) Parameters.Add(field.Clone());
        Headers.Clear();
        foreach (var field in tab.Headers) Headers.Add(field.Clone());
        ResponseDataViewer.SetContent(tab.ResponseBody);
        ResponseHeadersBox.Text = tab.ResponseHeaders;
        StatusText.Text = tab.Status;
        ResponseMetaText.Text = tab.ResponseMeta;
        StatusBadge.Background = tab.StatusBrush;
        UpdateAuthenticationPanels();
        _isRestoringTab = false;
        _isDirty = tab.IsDirty;
        foreach (var item in _requestTabs) item.Header.Background = item == tab ? new SolidColorBrush(Color.FromRgb(32, 35, 43)) : Brushes.Transparent;
    }

    private void CaptureActiveTab()
    {
        if (_activeRequestTab is null || _isRestoringTab) return;
        var tab = _activeRequestTab;
        tab.MethodIndex = Math.Max(0, MethodBox.SelectedIndex);
        tab.Url = UrlBox.Text;
        tab.ContentTypeIndex = Math.Max(0, ContentTypeBox.SelectedIndex);
        tab.Body = BodyBox.Text;
        tab.AuthTypeIndex = Math.Max(0, AuthTypeBox.SelectedIndex);
        tab.Token = TokenBox.Password;
        tab.Username = UsernameBox.Text;
        tab.Password = PasswordBox.Password;
        tab.Parameters = Parameters.Select(field => field.Clone()).ToList();
        tab.Headers = Headers.Select(field => field.Clone()).ToList();
        tab.ResponseBody = ResponseDataViewer.RawText;
        tab.ResponseHeaders = ResponseHeadersBox.Text;
        tab.Status = StatusText.Text;
        tab.ResponseMeta = ResponseMetaText.Text;
        tab.StatusBrush = StatusBadge.Background;
        UpdateRequestTabHeader(tab);
    }

    private void UpdateRequestTabHeader(RestRequestTab tab)
    {
        if (tab.MethodText is null || tab.TitleText is null) return;
        var method = Methods.ElementAtOrDefault(tab.MethodIndex) ?? "GET";
        tab.MethodText.Text = method;
        tab.MethodText.Foreground = MethodBrush(method);
        tab.TitleText.Text = TitleFor(tab.Url);
    }

    private void CloseRequestTab(RestRequestTab tab)
    {
        var index = _requestTabs.IndexOf(tab);
        _requestTabs.Remove(tab);
        RequestTabStrip.Children.Remove(tab.Header);
        if (_requestTabs.Count == 0)
        {
            _activeRequestTab = null;
            CreateRequestTab();
            return;
        }
        if (_activeRequestTab == tab) SelectRequestTab(_requestTabs[Math.Clamp(index - 1, 0, _requestTabs.Count - 1)]);
    }

    private void NewRequestTab_Click(object sender, RoutedEventArgs e) => CreateRequestTab();
    private void SaveRequest_Click(object sender, RoutedEventArgs e)
    {
        CaptureActiveTab();
        _saveAsRequested = false;
        if (_savedRequestId is null) SaveNameBox.Text = string.Empty;
        SaveUrlBox.Text = UrlBox.Text;
        SaveHintText.Text = string.Empty;
        EnsureSaveDestination();
        SaveFlyout.Visibility = Visibility.Visible;
    }

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    private static string TitleFor(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase) : string.IsNullOrWhiteSpace(url) ? "Untitled request" : url;
    private static Brush MethodBrush(string method) => method switch { "GET" => new SolidColorBrush(Color.FromRgb(88, 214, 141)), "POST" => new SolidColorBrush(Color.FromRgb(93, 173, 226)), "DELETE" => new SolidColorBrush(Color.FromRgb(236, 112, 99)), _ => new SolidColorBrush(Color.FromRgb(245, 176, 65)) };

    private Uri BuildUri(string input)
    {
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) throw new UriFormatException("Enter a complete URL, for example https://api.example.com/items.");
        var enabled = Parameters.Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.Key)).ToList();
        if (enabled.Count == 0) return uri;
        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        var query = string.Join("&", enabled.Select(item => { var value = ResolveText(item.Value).Value; return $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(value)}"; }));
        return new Uri(uri + separator + query);
    }

    private void ConfigureAuth(HttpRequestMessage request, string? token, string? username, string? password)
    {
        switch (AuthTypeBox.SelectedIndex)
        {
            case 1 when !string.IsNullOrWhiteSpace(token): request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); break;
            case 2: request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))); break;
        }
    }

    private static string SelectedContent(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    private void UrlBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { _ = SendAsync(); e.Handled = true; } }
    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_isRestoringTab && _activeRequestTab is not null) { _activeRequestTab.Url = UrlBox.Text; UpdateRequestTabHeader(_activeRequestTab); MarkDirty(); } UpdateVariablePreview(); }
    private void MethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_isRestoringTab && _activeRequestTab is not null) { _activeRequestTab.MethodIndex = Math.Max(0, MethodBox.SelectedIndex); UpdateRequestTabHeader(_activeRequestTab); MarkDirty(); } }
    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e) { RequestJsonTree.SetJson(BodyBox.Text); MarkDirty(); }
    private void AddParameter_Click(object sender, RoutedEventArgs e) => Parameters.Add(new RequestField());
    private void AddHeader_Click(object sender, RoutedEventArgs e) => Headers.Add(new RequestField());

    private void AuthTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAuthenticationPanels();
    }

    private void UpdateAuthenticationPanels()
    {
        if (BearerPanel is null || BasicPanel is null) return;
        BearerPanel.Visibility = AuthTypeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BasicPanel.Visibility = AuthTypeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyResponse_Click(object sender, RoutedEventArgs e) => ResponseDataViewer.CopyFullContent();

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == WorkspaceTabs) SetWorkspace(WorkspaceTabs.SelectedIndex);
    }

    private void SetWorkspace(int selectedIndex)
    {
        if (RestContentGrid is null) return;
        var library = RestContentGrid.Children.OfType<Border>().FirstOrDefault(x => Grid.GetColumn(x) == 0);
        var request = RestContentGrid.Children.OfType<Border>().FirstOrDefault(x => Grid.GetColumn(x) == 2);
        var response = RestContentGrid.Children.OfType<Border>().FirstOrDefault(x => Grid.GetColumn(x) == 4);
        var splitters = RestContentGrid.Children.OfType<GridSplitter>().ToArray();
        if (library is null || request is null || response is null) return;
        var isRequest = selectedIndex == 0;
        var isEnvironment = selectedIndex == 2;
        RequestComposerHost.Visibility = isRequest ? Visibility.Visible : Visibility.Collapsed;
        library.Visibility = selectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentManagerHost.Visibility = isEnvironment ? Visibility.Visible : Visibility.Collapsed;
        request.Visibility = isRequest ? Visibility.Visible : Visibility.Collapsed;
        response.Visibility = isRequest ? Visibility.Visible : Visibility.Collapsed;
        foreach (var splitter in splitters) splitter.Visibility = isRequest ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.Width = isRequest ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        LibrarySplitterColumn.Width = new GridLength(0);
        RequestColumn.MinWidth = isRequest ? 400 : 0;
        ResponseColumn.MinWidth = isRequest ? 400 : 0;
        RequestColumn.Width = isRequest ? new GridLength(5, GridUnitType.Star) : new GridLength(0);
        ResponseSplitterColumn.Width = isRequest ? new GridLength(10) : new GridLength(0);
        ResponseColumn.Width = isRequest ? new GridLength(6, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumnSpan(library, isRequest ? 1 : 5);
        if (selectedIndex == 1)
        {
            var libraryTabs = FindVisualChild<TabControl>(library);
            if (libraryTabs is not null) libraryTabs.SelectedIndex = selectedIndex == 2 ? 1 : 0;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result) return result;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void BuildCollectionsWorkspace()
    {
        var library = RestContentGrid.Children.OfType<Border>().FirstOrDefault(item => Grid.GetColumn(item) == 0);
        if (library is null) return;

        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(336) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Border { Background = new SolidColorBrush(Color.FromRgb(17, 23, 33)), BorderBrush = new SolidColorBrush(Color.FromRgb(43, 55, 72)), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(16, 18, 14, 14) };
        var sidebarGrid = new Grid();
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var sidebarHeader = new Grid { Margin = new Thickness(2, 0, 2, 0) };
        sidebarHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sidebarHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerCopy = new StackPanel();
        headerCopy.Children.Add(new TextBlock { Text = "Collections", Foreground = new SolidColorBrush(Color.FromRgb(247, 249, 252)), FontSize = 17, FontWeight = FontWeights.SemiBold });
        headerCopy.Children.Add(new TextBlock { Text = "Your saved API workspace", Foreground = new SolidColorBrush(Color.FromRgb(132, 148, 169)), FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        sidebarHeader.Children.Add(headerCopy);
        var quickCreate = new Button { Content = "+", ToolTip = "New collection", Width = 32, Height = 32, Padding = new Thickness(0), FontSize = 19, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        quickCreate.Style = (Style)FindResource("PrimaryButton");
        quickCreate.Click += NewCollectionDialog_Click;
        Grid.SetColumn(quickCreate, 1);
        sidebarHeader.Children.Add(quickCreate);
        sidebarGrid.Children.Add(sidebarHeader);
        _collectionManagerSearchBox = new TextBox { Margin = new Thickness(0, 18, 0, 12), ToolTip = "Search by collection, folder, method, or URL", Height = 38 };
        ApplySearchStyle(_collectionManagerSearchBox, "Search collections and requests…");
        _collectionManagerSearchBox.TextChanged += (_, _) => ApplyCollectionManagerSearch();
        Grid.SetRow(_collectionManagerSearchBox, 1);
        sidebarGrid.Children.Add(_collectionManagerSearchBox);
        _collectionTreePanel = new StackPanel { Margin = new Thickness(0, 1, 2, 0) };
        var treeScroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _collectionTreePanel };
        Grid.SetRow(treeScroller, 2);
        sidebarGrid.Children.Add(treeScroller);
        sidebar.Child = sidebarGrid;
        workspace.Children.Add(sidebar);

        var details = new Border { Background = new SolidColorBrush(Color.FromRgb(16, 23, 32)), Padding = new Thickness(22) };
        Grid.SetColumn(details, 1);
        var detailsGrid = new Grid();
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid();
        var heading = new StackPanel();
        _collectionManagerTitle = new TextBlock { Text = "Select a collection", Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.SemiBold };
        _collectionManagerDescription = new TextBlock { Text = "Choose a collection from the left, or create a new one.", Foreground = new SolidColorBrush(Color.FromRgb(184, 197, 214)), Margin = new Thickness(0, 5, 0, 0) };
        heading.Children.Add(_collectionManagerTitle);
        heading.Children.Add(_collectionManagerDescription);
        header.Children.Add(heading);
        detailsGrid.Children.Add(header);
        _collectionManagerContent = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        var detailsScroller = new ScrollViewer { Content = _collectionManagerContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(detailsScroller, 1); detailsGrid.Children.Add(detailsScroller);
        details.Child = detailsGrid;
        workspace.Children.Add(details);

        _collectionDialog = CreateCollectionDialog();
        workspace.Children.Add(_collectionDialog);
        Grid.SetColumnSpan(_collectionDialog, 2);
        _folderDialog = CreateFolderDialog();
        workspace.Children.Add(_folderDialog);
        Grid.SetColumnSpan(_folderDialog, 2);
        library.Padding = new Thickness(0);
        library.Child = workspace;
    }

    private Button CreateCollectionActionButton(string text, RoutedEventHandler handler, string toolTip)
    {
        var button = new Button { Content = text, ToolTip = toolTip };
        button.Style = (Style)FindResource("StorageActionButton");
        button.Click += handler;
        return button;
    }

    private void ApplySearchStyle(TextBox box, string placeholder)
    {
        box.Style = (Style)FindResource("SearchInput");
        box.Tag = placeholder;
    }

    private Grid CreateCollectionDialog()
    {
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(170, 8, 11, 16)), Visibility = Visibility.Collapsed };
        Panel.SetZIndex(overlay, 50);
        var dialog = new Border { Width = 410, Background = new SolidColorBrush(Color.FromRgb(24, 34, 49)), BorderBrush = new SolidColorBrush(Color.FromRgb(64, 81, 106)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _collectionDialogTitle = new TextBlock { Text = "Create collection", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold };
        content.Children.Add(_collectionDialogTitle);
        var fields = new StackPanel { Margin = new Thickness(0, 17, 0, 0) };
        fields.Children.Add(new TextBlock { Text = "Name *", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold });
        _collectionDialogNameBox = new TextBox { Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 6, 0, 0), ToolTip = "Collection name" };
        fields.Children.Add(_collectionDialogNameBox);
        fields.Children.Add(new TextBlock { Text = "Description", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 0) });
        _collectionDialogDescriptionBox = new TextBox { Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 6, 0, 0), Height = 62, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, ToolTip = "Optional description" };
        fields.Children.Add(_collectionDialogDescriptionBox);
        Grid.SetRow(fields, 1); content.Children.Add(fields);
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _collectionDialogErrorText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 180)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(_collectionDialogErrorText);
        var cancel = CreateCollectionActionButton("Cancel", CollectionDialogCancel_Click, "Cancel");
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        _collectionDialogSaveButton = new Button { Content = "Create", Margin = new Thickness(7, 0, 0, 0) };
        _collectionDialogSaveButton.Style = (Style)FindResource("PrimaryButton");
        _collectionDialogSaveButton.Click += CollectionDialogSave_Click;
        Grid.SetColumn(_collectionDialogSaveButton, 2); footer.Children.Add(_collectionDialogSaveButton);
        Grid.SetRow(footer, 2); content.Children.Add(footer);
        dialog.Child = content;
        overlay.Children.Add(dialog);
        return overlay;
    }

    private Grid CreateFolderDialog()
    {
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(170, 8, 11, 16)), Visibility = Visibility.Collapsed };
        Panel.SetZIndex(overlay, 51);
        var dialog = new Border { Width = 360, Background = new SolidColorBrush(Color.FromRgb(24, 34, 49)), BorderBrush = new SolidColorBrush(Color.FromRgb(64, 81, 106)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _folderDialogTitle = new TextBlock { Text = "Create new folder", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold };
        content.Children.Add(_folderDialogTitle);
        var fields = new StackPanel { Margin = new Thickness(0, 17, 0, 0) };
        fields.Children.Add(new TextBlock { Text = "Folder name *", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold });
        _folderDialogNameBox = new TextBox { Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 6, 0, 0), ToolTip = "Folder name" };
        _folderDialogNameBox.KeyDown += FolderDialogNameBox_KeyDown;
        fields.Children.Add(_folderDialogNameBox);
        Grid.SetRow(fields, 1); content.Children.Add(fields);
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _folderDialogErrorText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 180)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(_folderDialogErrorText);
        var cancel = CreateCollectionActionButton("Cancel", FolderDialogCancel_Click, "Cancel");
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        _folderDialogSaveButton = new Button { Content = "Create", Margin = new Thickness(7, 0, 0, 0) };
        _folderDialogSaveButton.Style = (Style)FindResource("PrimaryButton");
        _folderDialogSaveButton.Click += FolderDialogSave_Click;
        Grid.SetColumn(_folderDialogSaveButton, 2); footer.Children.Add(_folderDialogSaveButton);
        Grid.SetRow(footer, 2); content.Children.Add(footer);
        dialog.Child = content;
        overlay.Children.Add(dialog);
        return overlay;
    }

    private void ApplyCollectionManagerSearch()
    {
        if (_collectionTreePanel is null) return;
        var search = _collectionManagerSearchBox?.Text.Trim() ?? string.Empty;
        var nodes = BuildCollectionTreeNodes(!string.IsNullOrWhiteSpace(search));
        if (!string.IsNullOrWhiteSpace(search)) nodes = FilterCollectionTreeNodes(nodes, search);
        _collectionTreePanel.Children.Clear();
        foreach (var node in nodes) _collectionTreePanel.Children.Add(CreateCollectionTreeRow(node));
        if (nodes.Count == 0)
        {
            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var empty = new Border { Background = new SolidColorBrush(Color.FromRgb(21, 29, 41)), BorderBrush = new SolidColorBrush(Color.FromRgb(39, 52, 69)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18), Margin = new Thickness(2, 8, 2, 0) };
            var copy = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            copy.Children.Add(new TextBlock { Text = hasSearch ? "No matching items" : "No collections yet", Foreground = new SolidColorBrush(Color.FromRgb(229, 235, 243)), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            copy.Children.Add(new TextBlock { Text = hasSearch ? "Try a different name, method, or URL." : "Create a collection to organize your requests.", Foreground = new SolidColorBrush(Color.FromRgb(132, 148, 169)), FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            empty.Child = copy;
            _collectionTreePanel.Children.Add(empty);
        }
    }

    private List<CollectionTreeNode> BuildCollectionTreeNodes(bool expandAll)
    {
        var nodes = new List<CollectionTreeNode>();
        foreach (var collection in _collectionTreeCollections.OrderBy(collection => collection.SortOrder))
        {
            if (!_collectionExpansionStates.TryGetValue(collection.Id, out var collectionState)) collectionState = _collectionExpansionStates[collection.Id] = true;
            var collectionExpanded = expandAll || collectionState;
            nodes.Add(CollectionTreeNode.ForCollection(collection, collectionExpanded));
            if (!collectionExpanded) continue;
            foreach (var request in collection.Requests.Where(request => request.FolderId is null).OrderBy(request => request.SortOrder)) nodes.Add(CollectionTreeNode.ForRequest(collection, request, 1));
            foreach (var folder in collection.Folders.Where(folder => folder.ParentFolderId is null).OrderBy(folder => folder.SortOrder)) AddFolder(collection, folder, 1, nodes, expandAll);
        }
        return nodes;
    }

    private void AddFolder(RestCollection collection, RestCollectionFolder folder, int indent, List<CollectionTreeNode> nodes, bool expandAll)
    {
        if (!_folderExpansionStates.TryGetValue(folder.Id, out var folderState)) folderState = _folderExpansionStates[folder.Id] = true;
        var expanded = expandAll || folderState;
        nodes.Add(CollectionTreeNode.ForFolder(collection, folder, indent, expanded));
        if (!expanded) return;
        foreach (var request in collection.Requests.Where(request => request.FolderId == folder.Id).OrderBy(request => request.SortOrder)) nodes.Add(CollectionTreeNode.ForRequest(collection, request, indent + 1));
        foreach (var child in collection.Folders.Where(child => child.ParentFolderId == folder.Id).OrderBy(child => child.SortOrder)) AddFolder(collection, child, indent + 1, nodes, expandAll);
    }

    private Border CreateCollectionTreeRow(CollectionTreeNode node)
    {
        var isSelected = node.IsRequest ? _selectedSavedRequest?.Id == node.Request!.Id : node.IsFolder ? _selectedFolder?.Id == node.Folder!.Id : _selectedCollection?.Id == node.Collection.Id;
        var row = new Border { Tag = node, Background = isSelected ? new SolidColorBrush(node.IsCollection ? Color.FromRgb(27, 49, 74) : Color.FromRgb(27, 39, 56)) : Brushes.Transparent, BorderBrush = isSelected ? new SolidColorBrush(node.IsCollection ? Color.FromRgb(50, 115, 174) : Color.FromRgb(48, 65, 87)) : Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, node.IsRequest ? 7 : 8, 7, node.IsRequest ? 7 : 8), Margin = new Thickness(node.Indent * 17 + 1, 2, 2, 2), MinHeight = node.IsRequest ? 34 : 46, HorizontalAlignment = HorizontalAlignment.Stretch, Cursor = Cursors.Hand };
        row.MouseLeftButtonUp += CollectionTreeNodeSelected_Click;
        row.MouseEnter += CollectionTreeRow_MouseEnter;
        row.MouseLeave += CollectionTreeRow_MouseLeave;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        if (!node.IsRequest)
        {
            var chevron = CreateChevronIcon(node.IsExpanded);
            chevron.VerticalAlignment = VerticalAlignment.Top;
            var toggle = new Border
            {
                Tag = node,
                Background = Brushes.Transparent,
                Width = 20,
                Height = 22,
                Margin = new Thickness(0, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = node.IsExpanded ? "Collapse" : "Expand",
                Child = chevron
            };
            toggle.MouseLeftButtonDown += CollectionTreeToggle_Click;
            grid.Children.Add(toggle);
        }
        FrameworkElement icon = node.IsRequest ? new Border() : CreateCollectionTreeIcon(node.IsCollection);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, node.IsFolder ? 1 : 2, 0, 0);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(icon, 1); grid.Children.Add(icon);
        if (node.IsRequest)
        {
            var requestLine = new Grid { VerticalAlignment = VerticalAlignment.Center };
            requestLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            requestLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var methodBadge = new Border { Background = MethodBadgeBrush(node.Request!.Method), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            methodBadge.Child = new TextBlock { Text = node.Request.Method.ToUpperInvariant(), Foreground = MethodBrush(node.Request.Method), FontWeight = FontWeights.Bold, FontSize = 9 };
            requestLine.Children.Add(methodBadge);
            var requestName = new TextBlock { Text = node.Request.Name, Foreground = new SolidColorBrush(Color.FromRgb(218, 227, 239)), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(requestName, 1);
            requestLine.Children.Add(requestName);
            Grid.SetColumn(requestLine, 3); grid.Children.Add(requestLine);
        }
        else
        {
            var labels = new StackPanel();
            labels.Children.Add(new TextBlock { Text = node.DisplayName, Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249)), FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = $"{node.RequestCount} request{(node.RequestCount == 1 ? string.Empty : "s")}", Foreground = new SolidColorBrush(Color.FromRgb(132, 148, 169)), FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
            Grid.SetColumn(labels, 3); grid.Children.Add(labels);
            var menu = new Border
            {
                Tag = node,
                Background = Brushes.Transparent,
                Width = 28,
                Height = 30,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "More options",
                Child = CreateMoreIcon()
            };
            menu.MouseLeftButtonDown += CollectionNodeMenu_Click;
            Grid.SetColumn(menu, 4); grid.Children.Add(menu);
        }
        row.Child = grid;
        return row;
    }

    private static Viewbox CreateChevronIcon(bool expanded)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(expanded ? "M4,6 L8,10 L12,6" : "M6,4 L10,8 L6,12"),
            Stroke = new SolidColorBrush(Color.FromRgb(196, 211, 229)),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        return new Viewbox { Width = 13, Height = 13, Child = canvas };
    }

    private static Viewbox CreateCollectionTreeIcon(bool isCollection)
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var stroke = new SolidColorBrush(Color.FromRgb(209, 213, 219));
        var shape = isCollection
            ? new System.Windows.Shapes.Path { Data = Geometry.Parse("M3,3 L15,3 L15,15 L3,15 Z M6,6 L12,6 M6,9 L12,9 M6,12 L10,12"), Stroke = stroke, StrokeThickness = 1.35, StrokeLineJoin = PenLineJoin.Round }
            : new System.Windows.Shapes.Path { Data = Geometry.Parse("M2,5 L7,5 L9,7 L16,7 L16,15 L2,15 Z"), Stroke = stroke, StrokeThickness = 1.45, StrokeLineJoin = PenLineJoin.Round };
        canvas.Children.Add(shape);
        return new Viewbox { Width = 16, Height = 16, Child = canvas };
    }

    private static Viewbox CreateMoreIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var brush = new SolidColorBrush(Color.FromRgb(205, 217, 233));
        foreach (var left in new[] { 3.0, 8.0, 13.0 })
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 2.5, Height = 2.5, Fill = brush };
            Canvas.SetLeft(dot, left); Canvas.SetTop(dot, 7.75);
            canvas.Children.Add(dot);
        }
        return new Viewbox { Width = 14, Height = 14, Child = canvas };
    }

    private static Brush MethodBadgeBrush(string method)
    {
        var color = method.ToUpperInvariant() switch
        {
            "GET" => Color.FromRgb(20, 77, 67),
            "POST" => Color.FromRgb(44, 68, 108),
            "PUT" or "PATCH" => Color.FromRgb(91, 65, 29),
            "DELETE" => Color.FromRgb(91, 42, 49),
            _ => Color.FromRgb(53, 65, 82)
        };
        return new SolidColorBrush(color);
    }

    private static List<CollectionTreeNode> FilterCollectionTreeNodes(IEnumerable<CollectionTreeNode> nodes, string search)
    {
        var source = nodes.ToList();
        var visible = new HashSet<CollectionTreeNode>(source.Where(node => node.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)));
        foreach (var match in visible.ToArray())
        {
            var collection = source.FirstOrDefault(node => node.IsCollection && node.Collection.Id == match.Collection.Id);
            if (collection is not null) visible.Add(collection);
            if (match.Folder is not null)
                foreach (var ancestor in source.Where(node => node.IsFolder && IsFolderAncestor(node.Folder!, match.Folder!, match.Collection))) visible.Add(ancestor);
        }
        return source.Where(visible.Contains).ToList();
    }

    private static bool IsFolderAncestor(RestCollectionFolder candidate, RestCollectionFolder folder, RestCollection collection)
    {
        var parentId = folder.ParentFolderId;
        while (parentId is Guid id)
        {
            if (candidate.Id == id) return true;
            parentId = collection.Folders.FirstOrDefault(item => item.Id == id)?.ParentFolderId;
        }
        return false;
    }

    private void CollectionTreeRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: CollectionTreeNode node }) return;
        var selected = node.IsRequest ? _selectedSavedRequest?.Id == node.Request!.Id : node.IsFolder ? _selectedFolder?.Id == node.Folder!.Id : _selectedCollection?.Id == node.Collection.Id;
        if (!selected) nodeRowBackground(sender, Color.FromRgb(23, 33, 47));
    }

    private void CollectionTreeRow_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: CollectionTreeNode node }) return;
        var selected = node.IsRequest ? _selectedSavedRequest?.Id == node.Request!.Id : node.IsFolder ? _selectedFolder?.Id == node.Folder!.Id : _selectedCollection?.Id == node.Collection.Id;
        if (!selected) nodeRowBackground(sender, null);
    }

    private static void nodeRowBackground(object sender, Color? color)
    {
        if (sender is Border row) row.Background = color is Color value ? new SolidColorBrush(value) : Brushes.Transparent;
    }

    private void CollectionTreeNodeSelected_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CollectionTreeNode node }) return;
        _selectedCollection = node.Collection;
        _selectedFolder = node.Folder;
        _selectedSavedRequest = node.Request;
        _isCollectionManagerSelection = node.IsCollection;
        UpdateCollectionManagerDetails();
        ApplyCollectionManagerSearch();
        if (node.Request is not null)
        {
            WorkspaceTabs.SelectedIndex = 0;
            LoadSavedRequest(node.Request);
        }
    }

    private void CollectionTreeToggle_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CollectionTreeNode node }) return;
        var expanded = node.IsCollection ? _collectionExpansionStates : _folderExpansionStates;
        var isSearching = !string.IsNullOrWhiteSpace(_collectionManagerSearchBox?.Text);
        if (isSearching)
        {
            // Search deliberately reveals matching branches. Clearing it makes the
            // user's explicit expand/collapse choice visible instead of overriding it.
            _collectionManagerSearchBox!.Clear();
        }
        expanded[node.Id] = !node.IsExpanded;
        ApplyCollectionManagerSearch();
        e.Handled = true;
    }

    private void CollectionNodeMenu_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CollectionTreeNode node } button) return;
        var menuItemStyle = (Style)FindResource("CollectionTreeContextMenuItem");
        var separatorStyle = (Style)FindResource("CollectionTreeContextMenuSeparator");
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Style = (Style)FindResource("CollectionTreeContextMenu")
        };
        var addFolder = new MenuItem { Header = node.IsCollection ? "Add folder" : "Add subfolder", Style = menuItemStyle };
        addFolder.Click += (_, _) => OpenFolderDialog(node.Collection, node.Folder, null);
        var addRequest = new MenuItem { Header = "Add request", Style = menuItemStyle };
        addRequest.Click += (_, _) => AddRequestFromMenu(node);
        var rename = new MenuItem { Header = "Edit", Style = menuItemStyle };
        rename.Click += (_, _) =>
        {
            if (node.IsCollection) OpenCollectionDialog(node.Collection);
            else OpenFolderDialog(node.Collection, node.Folder, node.Folder);
        };
        var delete = new MenuItem { Header = "Delete", Style = menuItemStyle, Foreground = new SolidColorBrush(Color.FromRgb(255, 177, 177)) };
        delete.Click += async (_, _) => await DeleteNodeFromMenuAsync(node);
        menu.Items.Add(addFolder); menu.Items.Add(addRequest); menu.Items.Add(new Separator { Style = separatorStyle }); menu.Items.Add(rename); menu.Items.Add(new Separator { Style = separatorStyle }); menu.Items.Add(delete);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddRequestFromMenu(CollectionTreeNode node)
    {
        _selectedCollection = node.Collection;
        _selectedFolder = node.Folder;
        _saveDestinationCollection = node.Collection;
        _saveDestinationFolder = node.Folder;
        UpdateSaveLocationDisplay();
        WorkspaceTabs.SelectedIndex = 0;
        CreateRequestTab();
    }

    private async Task DeleteNodeFromMenuAsync(CollectionTreeNode node)
    {
        if (_collections is null) return;
        var message = node.IsCollection
            ? $"Delete collection '{node.Collection.Name}' and everything inside it? This cannot be undone."
            : $"Delete folder '{node.Folder!.Name}' and everything inside it? This cannot be undone.";
        if (MessageBox.Show(message, "DevBrowser", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (node.IsCollection) await _collections.DeleteCollectionAsync(node.Collection.Id);
        else await _collections.DeleteFolderAsync(node.Folder!.Id);
        _selectedCollection = null;
        _selectedFolder = null;
        _selectedSavedRequest = null;
        await RefreshLibraryAsync();
        UpdateCollectionManagerDetails();
    }

    private void UpdateCollectionManagerDetails()
    {
        if (_collectionManagerTitle is null || _collectionManagerDescription is null || _collectionManagerContent is null) return;
        var collection = _isCollectionManagerSelection && _selectedCollection is not null
            ? _collectionTreeCollections.FirstOrDefault(item => item.Id == _selectedCollection.Id) ?? _selectedCollection
            : null;
        _collectionManagerTitle.Text = collection?.Name ?? "Select a collection";
        _collectionManagerDescription.Text = collection?.Description ?? "Choose a collection from the left to view its details and saved requests.";
        _collectionManagerContent.Children.Clear();

        if (collection is null)
        {
            _collectionManagerContent.Children.Add(new TextBlock { Text = "Select a collection to view its basics and manage the saved requests inside it.", Foreground = new SolidColorBrush(Color.FromRgb(159, 176, 197)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0) });
            return;
        }

        _collectionManagerContent.Children.Add(CreateCollectionBasicsPanel(collection));
        _collectionManagerContent.Children.Add(CreateCollectionRequestsPanel(collection));
    }

    private Border CreateCollectionBasicsPanel(RestCollection collection)
    {
        var panel = new Border { Background = new SolidColorBrush(Color.FromRgb(18, 28, 41)), BorderBrush = new SolidColorBrush(Color.FromRgb(47, 62, 82)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(15, 13, 15, 13) };
        var contents = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = CreateCollectionTreeIcon(true);
        icon.Margin = new Thickness(0, 0, 8, 0);
        heading.Children.Add(icon);
        heading.Children.Add(new TextBlock { Text = collection.Name, Foreground = new SolidColorBrush(Color.FromRgb(242, 246, 252)), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        contents.Children.Add(heading);
        contents.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(collection.Description) ? "No description yet." : collection.Description, Foreground = new SolidColorBrush(Color.FromRgb(184, 197, 214)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 0, 0) });
        var facts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        facts.Children.Add(CreateCollectionFact("requests", $"{collection.Requests.Count} request{(collection.Requests.Count == 1 ? string.Empty : "s")}"));
        facts.Children.Add(CreateCollectionFact("folders", $"{collection.Folders.Count} folder{(collection.Folders.Count == 1 ? string.Empty : "s")}"));
        facts.Children.Add(CreateCollectionFact("created", "Created: " + collection.CreatedAt.LocalDateTime.ToString("dd MMM yyyy")));
        facts.Children.Add(CreateCollectionFact("updated", "Updated: " + collection.UpdatedAt.LocalDateTime.ToString("dd MMM yyyy")));
        contents.Children.Add(facts);
        panel.Child = contents;
        return panel;
    }

    private static StackPanel CreateCollectionFact(string type, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(type == "requests" ? 0 : 15, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(CreateCollectionFactIcon(type));
        content.Children.Add(new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(171, 188, 209)), FontSize = 10, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private static Viewbox CreateCollectionFactIcon(string type)
    {
        var geometry = type switch
        {
            "requests" => "M3,2 L12,2 L12,14 L3,14 Z M5,5 L10,5 M5,8 L10,8 M5,11 L8,11",
            "folders" => "M2,5 L6,5 L8,7 L14,7 L14,13 L2,13 Z",
            "created" => "M3,4 L13,4 L13,14 L3,14 Z M3,7 L13,7 M5,2 L5,6 M11,2 L11,6",
            _ => "M8,2 A6,6 0 1,1 2,8 A6,6 0 0,1 8,2 M8,4 L8,8 L11,10"
        };
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = new SolidColorBrush(Color.FromRgb(140, 162, 189)), StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return new Viewbox { Width = 13, Height = 13, Child = canvas };
    }

    private Border CreateCollectionRequestsPanel(RestCollection collection)
    {
        var panel = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 33, 47)), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 73, 96)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(15), Margin = new Thickness(0, 14, 0, 0) };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "SAVED REQUESTS", Foreground = new SolidColorBrush(Color.FromRgb(184, 197, 214)), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var table = new StackPanel();
        table.Children.Add(CreateCollectionRequestTableHeader());
        foreach (var request in collection.Requests.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt)) table.Children.Add(CreateCollectionRequestTableRow(request));
        if (collection.Requests.Count == 0) table.Children.Add(new TextBlock { Text = "No saved requests in this collection yet.", Foreground = new SolidColorBrush(Color.FromRgb(159, 176, 197)), FontSize = 11, Margin = new Thickness(10, 14, 0, 10) });
        content.Children.Add(table);
        panel.Child = content;
        return panel;
    }

    private static Grid CreateCollectionRequestTableHeader()
    {
        var header = CreateCollectionRequestTableGrid();
        header.Background = new SolidColorBrush(Color.FromRgb(30, 43, 60));
        header.Children.Add(CreateTableText("METHOD", new SolidColorBrush(Color.FromRgb(184, 197, 214)), 10, true, 0));
        header.Children.Add(CreateTableText("REQUEST", new SolidColorBrush(Color.FromRgb(184, 197, 214)), 10, true, 1));
        header.Children.Add(CreateTableText("CREATED", new SolidColorBrush(Color.FromRgb(184, 197, 214)), 10, true, 2));
        header.Children.Add(CreateTableText("ACTION", new SolidColorBrush(Color.FromRgb(184, 197, 214)), 10, true, 3));
        return header;
    }

    private Border CreateCollectionRequestTableRow(SavedRestRequest request)
    {
        var row = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 29, 42)), BorderBrush = new SolidColorBrush(Color.FromRgb(47, 62, 82)), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2, 0, 2) };
        var grid = CreateCollectionRequestTableGrid();
        grid.Children.Add(CreateTableText(request.Method, MethodBrush(request.Method), 11, true, 0));
        var requestName = CreateTableText(request.Name, new SolidColorBrush(Color.FromRgb(230, 238, 248)), 12, false, 1);
        requestName.ToolTip = request.Url;
        grid.Children.Add(requestName);
        grid.Children.Add(CreateTableText(request.CreatedAt.LocalDateTime.ToString("dd MMM yyyy"), new SolidColorBrush(Color.FromRgb(184, 197, 214)), 10, false, 2));
        var open = new Button { Content = CreatePlayIcon(), ToolTip = "Open request", Width = 32, Height = 28, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center, Tag = request };
        open.Style = (Style)FindResource("StorageActionButton");
        open.Click += OpenCollectionRequest_Click;
        Grid.SetColumn(open, 3); grid.Children.Add(open);
        row.Child = grid;
        return row;
    }

    private static Grid CreateCollectionRequestTableGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        return grid;
    }

    private static TextBlock CreateTableText(string text, Brush foreground, double fontSize, bool bold, int column)
    {
        var block = new TextBlock { Text = text, Foreground = foreground, FontSize = fontSize, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 7, 8, 7), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(block, column);
        return block;
    }

    private static Viewbox CreatePlayIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M5,3 L13,8 L5,13 Z"), Fill = new SolidColorBrush(Color.FromRgb(125, 211, 252)) });
        return new Viewbox { Width = 14, Height = 14, Child = canvas };
    }

    private void OpenCollectionRequest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SavedRestRequest request }) return;
        _selectedSavedRequest = request;
        WorkspaceTabs.SelectedIndex = 0;
        LoadSavedRequest(request);
    }

    private void NewCollectionDialog_Click(object sender, RoutedEventArgs e) => OpenCollectionDialog(null);

    private void EditCollectionDialog_Click(object sender, RoutedEventArgs e)
    {
        if (_isCollectionManagerSelection && _selectedCollection is not null) OpenCollectionDialog(_selectedCollection);
    }

    private void OpenCollectionDialog(RestCollection? collection)
    {
        if (_collectionDialog is null || _collectionDialogNameBox is null || _collectionDialogDescriptionBox is null) return;
        _collectionDialogTarget = collection;
        _collectionDialogTitle!.Text = collection is null ? "Create new collection" : "Edit collection";
        _collectionDialogSaveButton!.Content = collection is null ? "Create" : "Save changes";
        _collectionDialogNameBox.Text = collection?.Name ?? string.Empty;
        _collectionDialogDescriptionBox.Text = collection?.Description ?? string.Empty;
        _collectionDialogErrorText!.Text = string.Empty;
        _collectionDialog.Visibility = Visibility.Visible;
        _collectionDialogNameBox.Focus();
    }

    private void CollectionDialogCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionDialog is not null) _collectionDialog.Visibility = Visibility.Collapsed;
    }

    private void OpenFolderDialog(RestCollection collection, RestCollectionFolder? parentFolder, RestCollectionFolder? folder)
    {
        if (_folderDialog is null || _folderDialogNameBox is null || _folderDialogErrorText is null) return;
        _folderDialogCollection = collection;
        _folderDialogParent = parentFolder;
        _folderDialogTarget = folder;
        _folderDialogTitle!.Text = folder is null ? "Create new folder" : "Rename folder";
        _folderDialogSaveButton!.Content = folder is null ? "Create" : "Save changes";
        _folderDialogNameBox.Text = folder?.Name ?? string.Empty;
        _folderDialogErrorText.Text = string.Empty;
        _folderDialog.Visibility = Visibility.Visible;
        _folderDialogNameBox.Focus();
        _folderDialogNameBox.SelectAll();
    }

    private void FolderDialogCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_folderDialog is not null) _folderDialog.Visibility = Visibility.Collapsed;
    }

    private void FolderDialogNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FolderDialogSave_Click(sender, e);
        e.Handled = true;
    }

    private async void FolderDialogSave_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || _folderDialogCollection is null || _folderDialogNameBox is null || _folderDialogErrorText is null) return;
        var name = _folderDialogNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _folderDialogErrorText.Text = "A folder name is required.";
            _folderDialogNameBox.Focus();
            return;
        }

        try
        {
            RestCollectionFolder? createdFolder = null;
            if (_folderDialogTarget is null)
            {
                createdFolder = await _collections.CreateFolderAsync(_folderDialogCollection.Id, name, _folderDialogParent?.Id);
                _selectedCollection = _folderDialogCollection;
                _selectedFolder = createdFolder;
                _collectionExpansionStates[_folderDialogCollection.Id] = true;
                if (_folderDialogParent is not null) _folderExpansionStates[_folderDialogParent.Id] = true;
            }
            else
            {
                await _collections.RenameFolderAsync(_folderDialogTarget.Id, name);
                _selectedCollection = _folderDialogCollection;
                _folderDialogTarget.Name = name;
                _selectedFolder = _folderDialogTarget;
            }

            _folderDialog!.Visibility = Visibility.Collapsed;
            await RefreshLibraryAsync();
            if (_selectNewFolderForSaveDestination && createdFolder is not null)
            {
                _saveDestinationCollection = _collectionTreeCollections.FirstOrDefault(collection => collection.Id == _folderDialogCollection.Id);
                _saveDestinationFolder = _saveDestinationCollection?.Folders.FirstOrDefault(folder => folder.Id == createdFolder.Id);
                _selectNewFolderForSaveDestination = false;
                UpdateSaveLocationDisplay();
                RenderSaveLocationTree();
            }
            UpdateCollectionManagerDetails();
        }
        catch (InvalidOperationException exception)
        {
            _folderDialogErrorText.Text = exception.Message;
        }
    }

    private async void CollectionDialogSave_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || _collectionDialogNameBox is null || _collectionDialogDescriptionBox is null || _collectionDialogErrorText is null) return;
        var name = _collectionDialogNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _collectionDialogErrorText.Text = "A collection name is required.";
            _collectionDialogNameBox.Focus();
            return;
        }

        try
        {
            if (_collectionDialogTarget is null)
            {
                _selectedCollection = await _collections.CreateCollectionAsync(name, _collectionDialogDescriptionBox.Text.Trim());
                _isCollectionManagerSelection = true;
            }
            else
            {
                await _collections.UpdateCollectionAsync(_collectionDialogTarget.Id, name, _collectionDialogDescriptionBox.Text.Trim());
                _selectedCollection = new RestCollection { Id = _collectionDialogTarget.Id, Name = name, Description = _collectionDialogDescriptionBox.Text.Trim() };
            }

            _collectionDialog!.Visibility = Visibility.Collapsed;
            await RefreshLibraryAsync();
            if (_selectNewCollectionForSaveDestination && _selectedCollection is not null)
            {
                _saveDestinationCollection = _collectionTreeCollections.FirstOrDefault(collection => collection.Id == _selectedCollection.Id);
                _saveDestinationFolder = null;
                _selectNewCollectionForSaveDestination = false;
                UpdateSaveLocationDisplay();
                RenderSaveLocationTree();
            }
            UpdateCollectionManagerDetails();
        }
        catch (InvalidOperationException exception)
        {
            _collectionDialogErrorText.Text = exception.Message;
        }
    }

    private async void DeleteSelectedCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || !_isCollectionManagerSelection || _selectedCollection is null) return;
        if (MessageBox.Show($"Delete collection '{_selectedCollection.Name}' and its saved requests?", "DevBrowser", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _collections.DeleteCollectionAsync(_selectedCollection.Id);
        _selectedCollection = null;
        _selectedFolder = null;
        _selectedSavedRequest = null;
        _isCollectionManagerSelection = false;
        await RefreshLibraryAsync();
        UpdateCollectionManagerDetails();
    }

    private async Task RefreshLibraryAsync()
    {
        if (_collections is null || _environments is null || !IsLoaded) return;
        try
        {
            var collections = await _collections.GetCollectionsAsync();
            _collectionTreeCollections.Clear(); _collectionTreeCollections.AddRange(collections);
            var entries = new List<LibraryItem>();
            foreach (var collection in collections)
            {
                entries.Add(LibraryItem.ForCollection(collection));
                foreach (var folder in collection.Folders.OrderBy(x => x.SortOrder))
                {
                    entries.Add(LibraryItem.ForFolder(folder, collection));
                entries.AddRange(collection.Requests.Where(x => x.FolderId == folder.Id).OrderBy(x => x.SortOrder).Select(x => LibraryItem.ForRequest(x, collection)));
                }
                entries.AddRange(collection.Requests.Where(x => x.FolderId is null).OrderBy(x => x.SortOrder).Select(x => LibraryItem.ForRequest(x, collection)));
            }
            _libraryItems.Clear(); _libraryItems.AddRange(entries);
            ApplyLibrarySearch();
            ApplyCollectionManagerSearch();
            if (_saveDestinationCollection is not null)
            {
                _saveDestinationCollection = collections.FirstOrDefault(item => item.Id == _saveDestinationCollection.Id);
                _saveDestinationFolder = _saveDestinationCollection?.Folders.FirstOrDefault(item => item.Id == _saveDestinationFolder?.Id);
            }
            EnsureSaveDestination();

            var environments = await _environments.GetEnvironmentsAsync(true);
            _environmentItems.Clear(); _environmentItems.AddRange(environments);
            var choices = new List<EnvironmentChoice> { EnvironmentChoice.None };
            choices.AddRange(environments.Select(x => new EnvironmentChoice(x)));
            EnvironmentBox.ItemsSource = choices;
            var activeId = environments.FirstOrDefault(x => x.IsActive)?.Id;
            _activeEnvironment = environments.FirstOrDefault(x => x.Id == activeId);
            var selectedId = _selectedEnvironment?.Id;
            _selectedEnvironment = environments.FirstOrDefault(x => x.Id == selectedId) ?? _activeEnvironment;
            EnvironmentBox.SelectedItem = choices.FirstOrDefault(x => x.Environment?.Id == activeId) ?? choices[0];
            EnvironmentBox.Visibility = Visibility.Collapsed;
            TopEnvironmentBox.ItemsSource = choices;
            TopEnvironmentBox.SelectedItem = choices.FirstOrDefault(x => x.Environment?.Id == activeId) ?? choices[0];
            ApplyEnvironmentSearch();
            EnvironmentVariablesList.ItemsSource = _activeEnvironment?.Variables ?? [];
            SetEnvironmentManagerItems(_environmentItems);
            UpdateEnvironmentManager();
            CollectionsEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception) { }
    }

    private async void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null) return;
        var name = string.IsNullOrWhiteSpace(NewCollectionNameBox.Text) ? "New Collection" : NewCollectionNameBox.Text;
        await _collections.CreateCollectionAsync(name);
        NewCollectionNameBox.Text = string.Empty;
        await RefreshLibraryAsync();
    }

    private async void RenameCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || _selectedCollection is null || string.IsNullOrWhiteSpace(NewCollectionNameBox.Text)) return;
        await _collections.RenameCollectionAsync(_selectedCollection.Id, NewCollectionNameBox.Text);
        NewCollectionNameBox.Text = string.Empty;
        await RefreshLibraryAsync();
    }

    private async void DeleteCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || _selectedCollection is null) return;
        var requests = _selectedCollection.Requests.Count;
        if (MessageBox.Show($"Delete '{_selectedCollection.Name}'? This permanently removes {requests} saved request(s).", "Delete collection", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _collections.DeleteCollectionAsync(_selectedCollection.Id);
        _selectedCollection = null; _selectedFolder = null; _selectedSavedRequest = null;
        await RefreshLibraryAsync();
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCollection is not null) OpenFolderDialog(_selectedCollection, _selectedFolder, null);
    }

    private async void DuplicateSavedRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || _selectedSavedRequest is null) return;
        await _collections.DuplicateRequestAsync(_selectedSavedRequest.Id);
        await RefreshLibraryAsync();
    }

    private async void DeleteSavedRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_collections is null || _selectedSavedRequest is null) return;
        if (MessageBox.Show($"Delete saved request '{_selectedSavedRequest.Name}'?", "Delete saved request", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _collections.DeleteRequestAsync(_selectedSavedRequest.Id);
        _selectedSavedRequest = null;
        await RefreshLibraryAsync();
    }

    private void LibrarySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyLibrarySearch();
    private void ApplyLibrarySearch()
    {
        if (CollectionList is null) return;
        var search = LibrarySearchBox?.Text.Trim() ?? string.Empty;
        CollectionList.ItemsSource = string.IsNullOrWhiteSpace(search) ? _libraryItems.ToList() : _libraryItems.Where(item => item.Label.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void EnvironmentSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyEnvironmentSearch();
    private void ApplyEnvironmentSearch()
    {
        if (EnvironmentList is null) return;
        var search = EnvironmentSearchBox?.Text.Trim() ?? string.Empty;
        EnvironmentList.ItemsSource = string.IsNullOrWhiteSpace(search) ? _environmentItems.ToList() : _environmentItems.Where(environment => environment.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || environment.Variables.Any(variable => variable.Key.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void EnvironmentManagerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var search = EnvironmentManagerSearchBox.Text.Trim();
        SetEnvironmentManagerItems(string.IsNullOrWhiteSpace(search)
            ? _environmentItems
            : _environmentItems.Where(environment => environment.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || environment.Variables.Any(variable => variable.Key.Contains(search, StringComparison.OrdinalIgnoreCase))));
    }

    private void EnvironmentManagerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentManagerList.SelectedItem is not FrameworkElement { Tag: RestEnvironment environment }) return;
        _selectedEnvironment = environment;
        UpdateEnvironmentManager();
    }

    private void SetEnvironmentManagerItems(IEnumerable<RestEnvironment> environments)
    {
        var rows = environments.Select(CreateEnvironmentManagerRow).ToList();
        EnvironmentManagerList.ItemTemplate = null;
        EnvironmentManagerList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        EnvironmentManagerList.ItemsSource = rows;
        EnvironmentManagerList.SelectedItem = rows.FirstOrDefault(row => row.Tag is RestEnvironment environment && environment.Id == _selectedEnvironment?.Id);
    }

    private Border CreateEnvironmentManagerRow(RestEnvironment environment)
    {
        var row = new Border { Tag = environment, Background = new SolidColorBrush(Color.FromRgb(29, 38, 52)), CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 8, 6, 8), Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        var color = Brushes.Gray;
        try { color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(environment.Color)); }
        catch (FormatException) { }
        var dot = new Border { Width = 9, Height = 9, Background = color, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(dot);
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = environment.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        labels.Children.Add(new TextBlock { Text = $"{environment.Variables.Count} variable{(environment.Variables.Count == 1 ? string.Empty : "s")}", Foreground = new SolidColorBrush(Color.FromRgb(184, 197, 214)), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        if (environment.IsActive)
        {
            var active = new Border { Background = new SolidColorBrush(Color.FromRgb(20, 77, 67)), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            active.Child = new TextBlock { Text = "Active", Foreground = new SolidColorBrush(Color.FromRgb(134, 239, 172)), FontSize = 9, FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(active, 2);
            grid.Children.Add(active);
        }
        var menu = new Border
        {
            Tag = environment,
            Background = Brushes.Transparent,
            Width = 28,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "More options",
            Child = CreateMoreIcon()
        };
        menu.MouseLeftButtonDown += EnvironmentNodeMenu_Click;
        Grid.SetColumn(menu, 3);
        grid.Children.Add(menu);
        row.Child = grid;
        return row;
    }

    private void EnvironmentNodeMenu_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RestEnvironment environment } button) return;
        var menuItemStyle = (Style)FindResource("CollectionTreeContextMenuItem");
        var separatorStyle = (Style)FindResource("CollectionTreeContextMenuSeparator");
        var menu = new ContextMenu { PlacementTarget = button, Style = (Style)FindResource("CollectionTreeContextMenu") };
        var setActive = new MenuItem { Header = "Set active", Style = menuItemStyle, IsEnabled = !environment.IsActive };
        setActive.Click += async (_, _) => await SetActiveEnvironmentAsync(environment);
        var duplicate = new MenuItem { Header = "Duplicate", Style = menuItemStyle };
        duplicate.Click += async (_, _) => await DuplicateEnvironmentAsync(environment);
        var delete = new MenuItem { Header = "Delete", Style = menuItemStyle, Foreground = new SolidColorBrush(Color.FromRgb(255, 177, 177)) };
        delete.Click += async (_, _) => await DeleteEnvironmentAsync(environment);
        menu.Items.Add(setActive);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(delete);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void UpdateEnvironmentManager()
    {
        var environment = _selectedEnvironment ?? _activeEnvironment;
        EnvironmentManagerTitle.Text = environment?.Name ?? "No environment selected";
        if (_environmentManagerActiveText is not null)
            _environmentManagerActiveText.Visibility = environment?.IsActive == true ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentManagerDescription.Text = environment?.Description ?? "Create an environment to reuse URLs, tokens and other values.";
        EnvironmentManagerDates.Text = environment is null ? string.Empty : $"Created: {environment.CreatedAt.LocalDateTime:g}  •  Updated: {environment.UpdatedAt.LocalDateTime:g}";
        EnvironmentManagerCount.Text = environment is null ? "0 variables" : $"{environment.Variables.Count} variable{(environment.Variables.Count == 1 ? string.Empty : "s")}";
        EnvironmentManagerVariables.ItemsSource = environment?.Variables.Select(CreateManagerVariableRow).ToList() ?? [];
        var preview = environment?.Variables.ToDictionary(variable => variable.Key, variable => variable.IsSecret ? "••••••••••••" : variable.Value ?? string.Empty) ?? new Dictionary<string, string>();
        EnvironmentManagerPreview.Text = JsonSerializer.Serialize(preview, new JsonSerializerOptions { WriteIndented = true });
    }

    private void ConfigureEnvironmentManagerHeader()
    {
        if (EnvironmentManagerTitle.Parent is StackPanel details)
        {
            _environmentManagerActiveText = new TextBlock
            {
                Text = "Active",
                Foreground = new SolidColorBrush(Color.FromRgb(134, 239, 172)),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(9, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            details.Children.Remove(EnvironmentManagerTitle);
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
            titleLine.Children.Add(EnvironmentManagerTitle);
            titleLine.Children.Add(_environmentManagerActiveText);
            details.Children.Insert(0, titleLine);
        }

    }

    private async void SetActiveEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEnvironment is not null) await SetActiveEnvironmentAsync(_selectedEnvironment);
    }

    private async Task SetActiveEnvironmentAsync(RestEnvironment environment)
    {
        if (_environments is null) return;
        _selectedEnvironment = environment;
        await _environments.SetActiveAsync(environment.Id);
        _activeEnvironment = environment;
        await RefreshLibraryAsync();
    }

    private async void AddManagerVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_environments is null || _selectedEnvironment is null || string.IsNullOrWhiteSpace(ManagerVariableKeyBox.Text)) return;
        var editing = _editingManagerVariable;
        await _environments.SaveVariableAsync(new RestEnvironmentVariable
        {
            Id = editing?.Id ?? Guid.NewGuid(),
            EnvironmentId = _selectedEnvironment.Id,
            Key = ManagerVariableKeyBox.Text.Trim(),
            Value = ManagerVariableValueBox.Text,
            Description = editing?.Description,
            IsSecret = _managerVariableSecretBox?.IsChecked == true,
            IsEnabled = editing?.IsEnabled ?? true,
            CreatedAt = editing?.CreatedAt ?? DateTimeOffset.UtcNow
        });
        ResetManagerVariableEditor();
        await RefreshLibraryAsync();
    }

    private void EditManagerVariable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RestEnvironmentVariable variable }) return;
        _editingManagerVariable = variable;
        ManagerVariableKeyBox.Text = variable.Key;
        ManagerVariableValueBox.Text = variable.Value ?? string.Empty;
        if (_managerVariableSecretBox is not null) _managerVariableSecretBox.IsChecked = variable.IsSecret;
        if (_managerVariableSubmitButton is not null) _managerVariableSubmitButton.Content = "Save changes";
        ManagerVariableKeyBox.Focus();
    }

    private async void DeleteManagerVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_environments is null || sender is not Button { Tag: RestEnvironmentVariable variable }) return;
        if (MessageBox.Show($"Delete variable '{variable.Key}'?", "DevBrowser", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _environments.DeleteVariableAsync(variable.Id);
        if (_editingManagerVariable?.Id == variable.Id) ResetManagerVariableEditor();
        await RefreshLibraryAsync();
    }

    private void ResetManagerVariableEditor()
    {
        _editingManagerVariable = null;
        ManagerVariableKeyBox.Text = string.Empty;
        ManagerVariableValueBox.Text = string.Empty;
        if (_managerVariableSecretBox is not null) _managerVariableSecretBox.IsChecked = false;
        if (_managerVariableSubmitButton is not null) _managerVariableSubmitButton.Content = "Add variable";
    }

    private void BuildEnvironmentVariableEditor()
    {
        if (EnvironmentManagerVariables.Parent is not StackPanel panel) return;

        if (EnvironmentManagerCount.Parent is Panel countParent) countParent.Children.Remove(EnvironmentManagerCount);
        if (ManagerVariableKeyBox.Parent is Panel keyParent) keyParent.Children.Remove(ManagerVariableKeyBox);
        if (ManagerVariableValueBox.Parent is Panel valueParent) valueParent.Children.Remove(ManagerVariableValueBox);
        panel.Children.Clear();
        var title = new Grid();
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        title.Children.Add(new TextBlock { Text = "VARIABLES", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        EnvironmentManagerCount.Margin = new Thickness(0, 3, 0, 0);
        EnvironmentManagerCount.HorizontalAlignment = HorizontalAlignment.Right;
        title.Children.Add(EnvironmentManagerCount);
        panel.Children.Add(title);

        var editor = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ManagerVariableKeyBox.Margin = new Thickness(0, 0, 6, 0);
        ManagerVariableKeyBox.Padding = new Thickness(8, 6, 8, 6);
        ManagerVariableKeyBox.ToolTip = "Variable name, e.g. baseUrl";
        editor.Children.Add(ManagerVariableKeyBox);
        Grid.SetColumn(ManagerVariableValueBox, 1);
        ManagerVariableValueBox.Margin = new Thickness(0, 0, 8, 0);
        ManagerVariableValueBox.Padding = new Thickness(8, 6, 8, 6);
        ManagerVariableValueBox.ToolTip = "Variable value";
        editor.Children.Add(ManagerVariableValueBox);
        _managerVariableSecretBox = new CheckBox
        {
            Content = "Secret",
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            ToolTip = "Mask and protect this value locally"
        };
        Grid.SetColumn(_managerVariableSecretBox, 2);
        editor.Children.Add(_managerVariableSecretBox);
        _managerVariableSubmitButton = new Button { Content = "Add variable", ToolTip = "Add this variable" };
        _managerVariableSubmitButton.Style = (Style)FindResource("PrimaryButton");
        _managerVariableSubmitButton.Click += AddManagerVariable_Click;
        Grid.SetColumn(_managerVariableSubmitButton, 3);
        editor.Children.Add(_managerVariableSubmitButton);
        panel.Children.Add(editor);

        var table = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 30, 41)), BorderBrush = new SolidColorBrush(Color.FromRgb(53, 68, 91)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        var tableGrid = new Grid();
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tableGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = CreateManagerVariableGrid();
        header.Background = new SolidColorBrush(Color.FromRgb(29, 38, 52));
        AddManagerVariableHeader(header, "Variable", 0);
        AddManagerVariableHeader(header, "Value", 1);
        AddManagerVariableHeader(header, "Type", 2);
        AddManagerVariableHeader(header, "Actions", 3);
        tableGrid.Children.Add(header);
        EnvironmentManagerVariables.ItemTemplate = null;
        EnvironmentManagerVariables.Background = Brushes.Transparent;
        EnvironmentManagerVariables.BorderThickness = new Thickness(0);
        EnvironmentManagerVariables.Margin = new Thickness(6, 4, 6, 6);
        EnvironmentManagerVariables.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        EnvironmentManagerVariables.ItemContainerStyle = new Style(typeof(ListBoxItem));
        EnvironmentManagerVariables.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        EnvironmentManagerVariables.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        EnvironmentManagerVariables.ItemContainerStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0)));
        EnvironmentManagerVariables.ItemContainerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        EnvironmentManagerVariables.ItemContainerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        Grid.SetRow(EnvironmentManagerVariables, 1);
        tableGrid.Children.Add(EnvironmentManagerVariables);
        table.Child = tableGrid;
        panel.Children.Add(table);
        panel.Children.Add(new TextBlock { Text = "Secret values are masked and stored locally only.", Foreground = new SolidColorBrush(Color.FromRgb(159, 176, 197)), FontSize = 10, Margin = new Thickness(0, 9, 0, 0) });
    }

    private static Grid CreateManagerVariableGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(165) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        return grid;
    }

    private static void AddManagerVariableHeader(Grid grid, string text, int column)
    {
        var label = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(184, 197, 214)), FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 8, 8, 8) };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private UIElement CreateManagerVariableRow(RestEnvironmentVariable variable)
    {
        var row = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 38, 52)), CornerRadius = new CornerRadius(5), Padding = new Thickness(0, 7, 0, 7), Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        var grid = CreateManagerVariableGrid();
        AddManagerVariableCell(grid, variable.Key, 0, Brushes.White, "Cascadia Mono");
        AddManagerVariableCell(grid, variable.IsSecret ? "••••••••••••" : variable.Value ?? string.Empty, 1, new SolidColorBrush(Color.FromRgb(158, 230, 184)), "Cascadia Mono");
        AddManagerVariableCell(grid, variable.IsSecret ? "Secret" : "Plain", 2, variable.IsSecret ? new SolidColorBrush(Color.FromRgb(134, 239, 172)) : new SolidColorBrush(Color.FromRgb(184, 197, 214)), null);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
        actions.Children.Add(CreateManagerVariableActionButton(false, "Edit variable", variable, EditManagerVariable_Click));
        actions.Children.Add(CreateManagerVariableActionButton(true, "Delete variable", variable, DeleteManagerVariable_Click));
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);
        row.Child = grid;
        return row;
    }

    private static void AddManagerVariableCell(Grid grid, string text, int column, Brush foreground, string? fontFamily)
    {
        var cell = new TextBlock { Text = text, Foreground = foreground, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        if (fontFamily is not null) cell.FontFamily = new FontFamily(fontFamily);
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private Button CreateManagerVariableActionButton(bool isDelete, string toolTip, RestEnvironmentVariable variable, RoutedEventHandler handler)
    {
        var canvas = new Canvas { Width = 20, Height = 20 };
        var brush = new SolidColorBrush(isDelete ? Color.FromRgb(248, 113, 113) : Color.FromRgb(207, 222, 240));
        if (isDelete)
        {
            var bin = new System.Windows.Shapes.Rectangle { Width = 10, Height = 11, RadiusX = 1, RadiusY = 1, Stroke = brush, StrokeThickness = 1.6 };
            Canvas.SetLeft(bin, 5); Canvas.SetTop(bin, 7);
            canvas.Children.Add(bin);
            var lid = new System.Windows.Shapes.Rectangle { Width = 14, Height = 1.8, RadiusX = 0.8, RadiusY = 0.8, Fill = brush };
            Canvas.SetLeft(lid, 3); Canvas.SetTop(lid, 5);
            canvas.Children.Add(lid);
            var handle = new System.Windows.Shapes.Rectangle { Width = 6, Height = 1.8, RadiusX = 0.8, RadiusY = 0.8, Fill = brush };
            Canvas.SetLeft(handle, 7); Canvas.SetTop(handle, 2.8);
            canvas.Children.Add(handle);
            for (var index = 0; index < 2; index++)
            {
                var line = new System.Windows.Shapes.Rectangle { Width = 1.2, Height = 6.5, RadiusX = 0.6, RadiusY = 0.6, Fill = brush };
                Canvas.SetLeft(line, 8 + index * 3); Canvas.SetTop(line, 9.2);
                canvas.Children.Add(line);
            }
        }
        else
        {
            var pen = new System.Windows.Shapes.Path { Data = Geometry.Parse("M4,15.5 L4,18 L6.5,18 L16.3,8.2 L13.8,5.7 Z M14.7,4.8 L16,3.5 L18.5,6 L17.2,7.3 Z"), Fill = brush };
            canvas.Children.Add(pen);
        }
        var button = new Button { Content = new Viewbox { Width = 15, Height = 15, Child = canvas }, Tag = variable, ToolTip = toolTip, Width = 29, Height = 26, Margin = new Thickness(2, 0, 0, 0) };
        button.Style = (Style)FindResource("StorageActionButton");
        button.Click += handler;
        return button;
    }

    private void NewEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (_environments is null) return;
        _newEnvironmentColor = "#4ADE80";
        NewEnvironmentNameBox.Text = string.Empty;
        NewEnvironmentDescriptionBox.Text = string.Empty;
        NewEnvironmentActiveBox.IsChecked = _activeEnvironment is null;
        NewEnvironmentErrorText.Text = string.Empty;
        UpdateEnvironmentColorSelection();
        NewEnvironmentDialog.Visibility = Visibility.Visible;
        NewEnvironmentNameBox.Focus();
    }

    private void NewEnvironmentCancel_Click(object sender, RoutedEventArgs e)
        => NewEnvironmentDialog.Visibility = Visibility.Collapsed;

    private void EnvironmentColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            _newEnvironmentColor = color;
            NewEnvironmentErrorText.Text = string.Empty;
            UpdateEnvironmentColorSelection();
        }
    }

    private void UpdateEnvironmentColorSelection()
    {
        foreach (var button in EnvironmentColorChoices.Children.OfType<Button>())
            button.BorderBrush = string.Equals(button.Tag as string, _newEnvironmentColor, StringComparison.OrdinalIgnoreCase)
                ? Brushes.White
                : Brushes.Transparent;
    }

    private async void NewEnvironmentCreate_Click(object sender, RoutedEventArgs e)
    {
        if (_environments is null) return;

        var name = NewEnvironmentNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NewEnvironmentErrorText.Text = "An environment name is required.";
            NewEnvironmentNameBox.Focus();
            return;
        }

        try
        {
            var environment = await _environments.CreateEnvironmentAsync(
                name,
                NewEnvironmentDescriptionBox.Text.Trim(),
                _newEnvironmentColor,
                NewEnvironmentActiveBox.IsChecked == true);

            _selectedEnvironment = environment;
            NewEnvironmentDialog.Visibility = Visibility.Collapsed;
            await RefreshLibraryAsync();
            UpdateEnvironmentManager();
        }
        catch (InvalidOperationException exception)
        {
            NewEnvironmentErrorText.Text = exception.Message;
        }
    }

    private async void DuplicateEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var environment = _selectedEnvironment ?? _activeEnvironment;
        if (environment is not null) await DuplicateEnvironmentAsync(environment);
    }

    private async Task DuplicateEnvironmentAsync(RestEnvironment environment)
    {
        if (_environments is null) return;
        _selectedEnvironment = await _environments.DuplicateEnvironmentAsync(environment.Id);
        await RefreshLibraryAsync();
    }

    private async void DeleteEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var environment = _selectedEnvironment ?? _activeEnvironment;
        if (environment is not null) await DeleteEnvironmentAsync(environment);
    }

    private async Task DeleteEnvironmentAsync(RestEnvironment environment)
    {
        if (_environments is null) return;
        if (MessageBox.Show($"Delete environment '{environment.Name}'?", "DevBrowser", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _environments.DeleteEnvironmentAsync(environment.Id);
        if (_activeEnvironment?.Id == environment.Id) _activeEnvironment = null;
        _selectedEnvironment = null;
        await RefreshLibraryAsync();
    }

    private async void EnvironmentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentBox.SelectedItem is not EnvironmentChoice choice) return;
        _activeEnvironment = choice.Environment;
        if (_environments is not null) await _environments.SetActiveAsync(_activeEnvironment?.Id);
        EnvironmentVariablesList.ItemsSource = _activeEnvironment?.Variables ?? [];
        UpdateVariablePreview();
        await Task.CompletedTask;
    }

    private async void TopEnvironmentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TopEnvironmentBox.SelectedItem is not EnvironmentChoice choice) return;
        _activeEnvironment = choice.Environment;
        if (_environments is not null) await _environments.SetActiveAsync(_activeEnvironment?.Id);
        EnvironmentBox.SelectedItem = (EnvironmentBox.ItemsSource as IEnumerable<EnvironmentChoice>)?.FirstOrDefault(x => x.Environment?.Id == choice.Environment?.Id);
        EnvironmentVariablesList.ItemsSource = _activeEnvironment?.Variables ?? [];
        UpdateVariablePreview();
    }

    private void EnvironmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnvironmentList.SelectedItem is not RestEnvironment environment) return;
        _activeEnvironment = environment;
        EnvironmentBox.SelectedItem = (EnvironmentBox.ItemsSource as IEnumerable<EnvironmentChoice>)?.FirstOrDefault(x => x.Environment?.Id == environment.Id);
        EnvironmentVariablesList.ItemsSource = environment.Variables;
        UpdateVariablePreview();
    }

    private async void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_environments is null || _activeEnvironment is null || string.IsNullOrWhiteSpace(VariableKeyBox.Text)) return;
        await _environments.SaveVariableAsync(new RestEnvironmentVariable { EnvironmentId = _activeEnvironment.Id, Key = VariableKeyBox.Text, Value = VariableValueBox.Text, IsSecret = VariableSecretBox.IsChecked == true });
        VariableKeyBox.Text = string.Empty; VariableValueBox.Text = string.Empty; VariableSecretBox.IsChecked = false;
        _activeEnvironment = (await _environments.GetEnvironmentsAsync(true)).Single(x => x.Id == _activeEnvironment.Id);
        await RefreshLibraryAsync();
        UpdateVariablePreview();
    }

    private void SaveLocationButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureSaveDestination();
        SaveLocationSearchBox.Text = string.Empty;
        SaveLocationHintText.Text = string.Empty;
        RenderSaveLocationTree();
        SaveLocationDialog.Visibility = Visibility.Visible;
        SaveLocationSearchBox.Focus();
    }

    private void SaveLocationCancel_Click(object sender, RoutedEventArgs e) => SaveLocationDialog.Visibility = Visibility.Collapsed;

    private void SaveLocationSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderSaveLocationTree();

    private void EnsureSaveDestination()
    {
        if (_saveDestinationCollection is not null) return;
        _saveDestinationCollection = _selectedCollection ?? _collectionTreeCollections.FirstOrDefault();
        _saveDestinationFolder = null;
        UpdateSaveLocationDisplay();
    }

    private void UpdateSaveLocationDisplay()
    {
        if (SaveLocationButton is null) return;
        SaveLocationButton.Content = _saveDestinationCollection is null
            ? "Choose a collection or folder"
            : _saveDestinationCollection.Name + (_saveDestinationFolder is null ? "  /  Collection root" : "  /  " + _saveDestinationFolder.Name);
        SaveLocationButton.ToolTip = "Choose where this request will be saved";
    }

    private void RenderSaveLocationTree()
    {
        if (SaveLocationTreePanel is null) return;
        var search = SaveLocationSearchBox?.Text.Trim() ?? string.Empty;
        SaveLocationTreePanel.Children.Clear();
        foreach (var collection in _collectionTreeCollections.OrderBy(item => item.SortOrder))
        {
            if (!CollectionMatchesSaveLocationSearch(collection, search)) continue;
            SaveLocationTreePanel.Children.Add(CreateSaveLocationRow(new SaveLocation(collection, null), 0));
            foreach (var folder in collection.Folders.Where(item => item.ParentFolderId is null).OrderBy(item => item.SortOrder)) AddSaveLocationFolderRows(collection, folder, 1, search);
        }

        if (SaveLocationTreePanel.Children.Count == 0)
        {
            SaveLocationTreePanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(search) ? "No collections yet. Create one to save this request." : "No matching collection or folder.",
                Foreground = new SolidColorBrush(Color.FromRgb(159, 176, 197)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(9)
            });
        }
    }

    private static bool CollectionMatchesSaveLocationSearch(RestCollection collection, string search) =>
        string.IsNullOrWhiteSpace(search) || collection.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || collection.Folders.Any(folder => folder.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    private void AddSaveLocationFolderRows(RestCollection collection, RestCollectionFolder folder, int indent, string search)
    {
        var hasMatch = string.IsNullOrWhiteSpace(search) || folder.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || collection.Folders.Any(child => child.ParentFolderId == folder.Id && FolderOrDescendantMatches(collection, child, search));
        if (!hasMatch) return;
        SaveLocationTreePanel.Children.Add(CreateSaveLocationRow(new SaveLocation(collection, folder), indent));
        foreach (var child in collection.Folders.Where(item => item.ParentFolderId == folder.Id).OrderBy(item => item.SortOrder)) AddSaveLocationFolderRows(collection, child, indent + 1, search);
    }

    private static bool FolderOrDescendantMatches(RestCollection collection, RestCollectionFolder folder, string search) =>
        folder.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || collection.Folders.Where(child => child.ParentFolderId == folder.Id).Any(child => FolderOrDescendantMatches(collection, child, search));

    private Border CreateSaveLocationRow(SaveLocation destination, int indent)
    {
        var selected = _saveDestinationCollection?.Id == destination.Collection.Id && _saveDestinationFolder?.Id == destination.Folder?.Id;
        var row = new Border
        {
            Tag = destination,
            Background = selected ? new SolidColorBrush(Color.FromRgb(26, 57, 85)) : Brushes.Transparent,
            BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(67, 174, 242)) : Brushes.Transparent,
            BorderThickness = selected ? new Thickness(2, 0, 0, 0) : new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 7, 8, 7),
            Margin = new Thickness(indent * 20, 1, 0, 1),
            Cursor = Cursors.Hand
        };
        var label = destination.Folder is null
            ? new TextBlock { Text = destination.Collection.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 }
            : new TextBlock { Text = "⌁  " + destination.Folder.Name, Foreground = new SolidColorBrush(Color.FromRgb(220, 230, 242)), FontSize = 12 };
        var root = destination.Folder is null ? new TextBlock { Text = "Collection root", Foreground = new SolidColorBrush(Color.FromRgb(159, 176, 197)), FontSize = 10, Margin = new Thickness(7, 2, 0, 0) } : null;
        var contents = new StackPanel();
        contents.Children.Add(label);
        if (root is not null) contents.Children.Add(root);
        row.Child = contents;
        row.MouseLeftButtonUp += SaveLocationRow_Click;
        row.MouseEnter += (_, _) => { if (!selected) row.Background = new SolidColorBrush(Color.FromRgb(29, 42, 58)); };
        row.MouseLeave += (_, _) => { if (!selected) row.Background = Brushes.Transparent; };
        return row;
    }

    private void SaveLocationRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: SaveLocation destination }) return;
        _saveDestinationCollection = destination.Collection;
        _saveDestinationFolder = destination.Folder;
        _selectedCollection = destination.Collection;
        _selectedFolder = destination.Folder;
        UpdateSaveLocationDisplay();
        SaveLocationDialog.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void NewSaveLocationCollection_Click(object sender, RoutedEventArgs e)
    {
        _selectNewCollectionForSaveDestination = true;
        OpenCollectionDialog(null);
    }

    private void NewSaveLocationFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_saveDestinationCollection is null)
        {
            SaveLocationHintText.Text = "Select a collection first, or create a new one.";
            return;
        }

        _selectNewFolderForSaveDestination = true;
        OpenFolderDialog(_saveDestinationCollection, _saveDestinationFolder, null);
    }

    private async void SaveFlyoutSave_Click(object sender, RoutedEventArgs e) => await SaveCurrentAsync(_saveAsRequested);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) { _saveAsRequested = true; SaveNameBox.Text = string.Empty; SaveUrlBox.Text = UrlBox.Text; SaveHintText.Text = string.Empty; EnsureSaveDestination(); SaveFlyout.Visibility = Visibility.Visible; await Task.CompletedTask; }
    private void SaveFlyoutCancel_Click(object sender, RoutedEventArgs e) { _saveAsRequested = false; SaveFlyout.Visibility = Visibility.Collapsed; }
    private async Task SaveCurrentAsync(bool forceNew)
    {
        if (_collections is null || _saveDestinationCollection is null) { SaveHintText.Text = "Choose a save location."; return; }
        var name = string.IsNullOrWhiteSpace(SaveNameBox.Text) ? UrlBox.Text.Trim() : SaveNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { SaveHintText.Text = "Enter a URL or request name."; return; }
        var item = new SavedRestRequest { Id = forceNew || _savedRequestId is null ? Guid.NewGuid() : _savedRequestId.Value, CollectionId = _saveDestinationCollection.Id, FolderId = _saveDestinationFolder?.Id, Name = name, Method = SelectedContent(MethodBox), Url = UrlBox.Text, Parameters = Parameters.Select(x => new SavedRequestField(x.IsEnabled, x.Key, x.Value)).ToList(), Headers = Headers.Select(x => new SavedRequestField(x.IsEnabled, x.Key, x.Value)).ToList(), Body = BodyBox.Text, ContentType = SelectedContent(ContentTypeBox), AuthType = SelectedContent(AuthTypeBox), AuthToken = TokenBox.Password, AuthUsername = UsernameBox.Text, AuthPassword = PasswordBox.Password };
        var saved = await _collections.SaveRequestAsync(item);
        _savedRequestId = saved.Id; _saveAsRequested = false; _isDirty = false; if (_activeRequestTab is not null) _activeRequestTab.IsDirty = false; SaveFlyout.Visibility = Visibility.Collapsed;
        await RefreshLibraryAsync(); UpdateRequestTabHeader(_activeRequestTab!);
    }

    private void CollectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CollectionList.SelectedItem is not LibraryItem item) return;
        _selectedCollection = item.Collection ?? _selectedCollection;
        _selectedFolder = item.Folder;
        _selectedSavedRequest = item.Request;
        if (item.Collection is not null) { _saveDestinationCollection = item.Collection; _saveDestinationFolder = item.Folder; UpdateSaveLocationDisplay(); return; }
        if (item.Request is not { } request) return;
        LoadSavedRequest(request);
        CollectionList.SelectedItem = null;
    }

    private void LoadSavedRequest(SavedRestRequest request)
    {
        CaptureActiveTab();
        var tab = new RestRequestTab { MethodIndex = Math.Max(0, Array.IndexOf(Methods, request.Method)), Url = request.Url, Body = request.Body, Parameters = request.Parameters.Select(x => new RequestField { IsEnabled = x.IsEnabled, Key = x.Key, Value = x.Value }).ToList(), Headers = request.Headers.Select(x => new RequestField { IsEnabled = x.IsEnabled, Key = x.Key, Value = x.Value }).ToList(), ContentTypeIndex = request.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ? 2 : request.ContentType.Contains("text", StringComparison.OrdinalIgnoreCase) ? 1 : 0, AuthTypeIndex = request.AuthType.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) ? 1 : request.AuthType.StartsWith("Basic", StringComparison.OrdinalIgnoreCase) ? 2 : 0, Token = request.AuthToken ?? string.Empty, Username = request.AuthUsername ?? string.Empty, Password = request.AuthPassword ?? string.Empty };
        _saveDestinationCollection = _collectionTreeCollections.FirstOrDefault(collection => collection.Id == request.CollectionId);
        _saveDestinationFolder = _saveDestinationCollection?.Folders.FirstOrDefault(folder => folder.Id == request.FolderId);
        UpdateSaveLocationDisplay();
        AddRequestTab(tab); _savedRequestId = request.Id; _isDirty = false;
    }

    private async void ExportCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionImportExport is null || _saveDestinationCollection is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "DevBrowser collection (*.json)|*.json", FileName = _saveDestinationCollection.Name + ".devbrowser.json" };
        if (dialog.ShowDialog() != true) return;
        var include = MessageBox.Show("Include sensitive headers and authentication values? Choose No to redact them (recommended).", "Export collection", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        await File.WriteAllTextAsync(dialog.FileName, await _collectionImportExport.ExportAsync(_saveDestinationCollection.Id, include));
    }

    private async void ImportCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_collectionImportExport is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "DevBrowser collection (*.json)|*.json" }; if (dialog.ShowDialog() != true) return;
        try { await _collectionImportExport.ImportAsync(await File.ReadAllTextAsync(dialog.FileName)); await RefreshLibraryAsync(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Import collection", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private VariableResolution ResolveText(string? value) => _variables?.Resolve(value, ActiveValues()) ?? new VariableResolution(value ?? string.Empty, []);
    private ResolvedEditorValues ResolveEditorValues() { var url = ResolveText(UrlBox.Text); var body = ResolveText(BodyBox.Text); var token = ResolveText(TokenBox.Password); var user = ResolveText(UsernameBox.Text); var password = ResolveText(PasswordBox.Password); return new(url.Value, body.Value, token.Value, user.Value, password.Value, url.MissingVariables.Concat(body.MissingVariables).Concat(token.MissingVariables).Concat(user.MissingVariables).Concat(password.MissingVariables).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()); }
    private IReadOnlyDictionary<string, string> ActiveValues() => _activeEnvironment?.Variables.Where(x => x.IsEnabled && x.Value is not null).ToDictionary(x => x.Key, x => x.Value!, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
    private void UpdateVariablePreview() { if (VariableStatusText is null) return; var result = ResolveText(UrlBox?.Text); VariableStatusText.Text = result.IsResolved && !string.Equals(result.Value, UrlBox?.Text, StringComparison.Ordinal) ? result.Value : result.MissingVariables.Count > 0 ? "Missing: " + string.Join(", ", result.MissingVariables) : string.Empty; VariableStatusText.Foreground = new SolidColorBrush(result.MissingVariables.Count > 0 ? Color.FromRgb(253, 186, 116) : Color.FromRgb(174, 204, 238)); }
    private void MarkDirty() { if (_isRestoringTab) return; _isDirty = true; if (_activeRequestTab is not null) { _activeRequestTab.IsDirty = true; UpdateRequestTabHeader(_activeRequestTab); } }

    private void SetFooterStatus(string primary, string detail, bool isFailure = false, bool isBusy = false) =>
        FooterStatusChanged?.Invoke(this, new RestClientFooterStatus(primary, detail, isFailure, isBusy));

    public sealed class RequestField { public bool IsEnabled { get; set; } = true; public string Key { get; set; } = string.Empty; public string? Value { get; set; } public RequestField Clone() => new() { IsEnabled = IsEnabled, Key = Key, Value = Value }; }
    private sealed record ResolvedEditorValues(string Url, string Body, string Token, string Username, string Password, IReadOnlyList<string> Missing);
    private sealed record SaveLocation(RestCollection Collection, RestCollectionFolder? Folder);
    private sealed class CollectionChoice { public CollectionChoice(RestCollection collection) => Collection = collection; public RestCollection Collection { get; } public string Name => Collection.Name; public override string ToString() => Name; }
    private sealed class EnvironmentChoice(RestEnvironment? environment) { public static EnvironmentChoice None { get; } = new(null); public RestEnvironment? Environment { get; } = environment; public override string ToString() => Environment?.Name ?? "No environment"; }
    private sealed class LibraryItem { public string Label { get; private init; } = string.Empty; public bool IsHeading { get; private init; } public bool IsFolder { get; private init; } public RestCollection? Collection { get; private init; } public RestCollectionFolder? Folder { get; private init; } public SavedRestRequest? Request { get; private init; } public static LibraryItem ForCollection(RestCollection value) => new() { Label = value.Name, IsHeading = true, Collection = value }; public static LibraryItem ForFolder(RestCollectionFolder value, RestCollection collection) => new() { Label = "   " + value.Name, IsFolder = true, Folder = value, Collection = collection }; public static LibraryItem ForRequest(SavedRestRequest value, RestCollection collection) => new() { Label = "      HTTP " + value.Method + "  " + value.Name, Request = value, Collection = collection }; }
    private sealed class CollectionTreeNode
    {
        private CollectionTreeNode(RestCollection collection, RestCollectionFolder? folder, SavedRestRequest? request, int indent, bool isExpanded) { Collection = collection; Folder = folder; Request = request; Indent = indent; IsExpanded = isExpanded; }
        public RestCollection Collection { get; }
        public RestCollectionFolder? Folder { get; }
        public SavedRestRequest? Request { get; }
        public int Indent { get; }
        public bool IsExpanded { get; }
        public bool IsCollection => Folder is null && Request is null;
        public bool IsFolder => Folder is not null;
        public bool IsRequest => Request is not null;
        public Guid Id => IsCollection ? Collection.Id : Folder!.Id;
        public string DisplayName => IsCollection ? Collection.Name : Folder!.Name;
        public int RequestCount => IsCollection ? Collection.Requests.Count : Collection.Requests.Count(request => request.FolderId == Folder!.Id);
        public string SearchText => IsRequest ? $"{Request!.Method} {Request.Name} {Request.Url}" : DisplayName;
        public static CollectionTreeNode ForCollection(RestCollection collection, bool expanded) => new(collection, null, null, 0, expanded);
        public static CollectionTreeNode ForFolder(RestCollection collection, RestCollectionFolder folder, int indent, bool expanded) => new(collection, folder, null, indent, expanded);
        public static CollectionTreeNode ForRequest(RestCollection collection, SavedRestRequest request, int indent) => new(collection, null, request, indent, false);
    }

    private sealed class RestRequestTab
    {
        public int MethodIndex { get; set; }
        public string Url { get; set; } = string.Empty;
        public int ContentTypeIndex { get; set; }
        public string Body { get; set; } = string.Empty;
        public int AuthTypeIndex { get; set; }
        public string Token { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public List<RequestField> Parameters { get; set; } = [];
        public List<RequestField> Headers { get; set; } = [];
        public string ResponseBody { get; set; } = "Response body will appear here.";
        public string ResponseHeaders { get; set; } = string.Empty;
        public string Status { get; set; } = "Ready";
        public string ResponseMeta { get; set; } = "Send a request to begin";
        public Brush StatusBrush { get; set; } = new SolidColorBrush(Color.FromRgb(38, 50, 56));
        public bool IsDirty { get; set; }
        public Border Header { get; set; } = null!;
        public TextBlock? MethodText { get; set; }
        public TextBlock? TitleText { get; set; }
    }
}

public sealed record RestClientFooterStatus(string Primary, string Detail, bool IsFailure, bool IsBusy);
