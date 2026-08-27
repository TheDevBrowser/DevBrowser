using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeveloperBrowser.App;

public partial class RestClientView : UserControl
{
    private readonly HttpClient _httpClient = new();
    private readonly List<RestRequestTab> _requestTabs = [];
    private RestRequestTab? _activeRequestTab;
    private bool _isRestoringTab;

    public ObservableCollection<RequestField> Parameters { get; } = [new()];
    public ObservableCollection<RequestField> Headers { get; } = [new()];

    public RestClientView()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            if (_requestTabs.Count == 0) CreateRequestTab();
        };
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        try
        {
            var uri = BuildUri(UrlBox.Text);
            using var request = new HttpRequestMessage(new HttpMethod(SelectedContent(MethodBox)), uri);
            ConfigureAuth(request);
            var body = BodyBox.Text.Trim();
            if (!string.IsNullOrEmpty(body) && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
                request.Content = new StringContent(body, Encoding.UTF8, SelectedContent(ContentTypeBox));

            foreach (var header in Headers.Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.Key)))
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    request.Content ??= new StringContent(string.Empty);
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            StatusText.Text = "Sending";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(46, 58, 80));
            ResponseMetaText.Text = "Waiting for server…";
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request);
            stopwatch.Stop();

            var responseBody = await response.Content.ReadAsStringAsync();
            ResponseBodyBox.Text = FormatBody(responseBody);
            ResponseJsonTree.SetJson(responseBody);
            ResponseHeadersBox.Text = string.Join(Environment.NewLine, response.Headers.Concat(response.Content.Headers).Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"));
            StatusText.Text = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            StatusBadge.Background = new SolidColorBrush(response.IsSuccessStatusCode ? Color.FromRgb(29, 100, 70) : Color.FromRgb(128, 56, 64));
            ResponseMetaText.Text = $"{stopwatch.ElapsedMilliseconds} ms · {responseBody.Length:N0} B";
            CaptureActiveTab();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Request failed";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(128, 56, 64));
            ResponseMetaText.Text = exception.GetType().Name;
            ResponseBodyBox.Text = exception.Message;
            ResponseJsonTree.SetJson(null);
            ResponseHeadersBox.Text = string.Empty;
            CaptureActiveTab();
        }
    }

    private void CreateRequestTab()
    {
        CaptureActiveTab();
        var tab = new RestRequestTab { Url = _requestTabs.Count == 0 ? "https://api.github.com/repos/dotnet/runtime" : string.Empty };
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
        var close = new Button { Content = "×", Foreground = (Brush)FindResource("MutedTextBrush"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Width = 25, Height = 28, FontSize = 15, Margin = new Thickness(7, 0, 0, 0), ToolTip = "Close request tab" };
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
        ResponseBodyBox.Text = tab.ResponseBody;
        ResponseHeadersBox.Text = tab.ResponseHeaders;
        ResponseJsonTree.SetJson(tab.ResponseBody);
        StatusText.Text = tab.Status;
        ResponseMetaText.Text = tab.ResponseMeta;
        StatusBadge.Background = tab.StatusBrush;
        UpdateAuthenticationPanels();
        _isRestoringTab = false;
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
        tab.ResponseBody = ResponseBodyBox.Text;
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
    private void SaveRequest_Click(object sender, RoutedEventArgs e) => CaptureActiveTab();

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
        var query = string.Join("&", enabled.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));
        return new Uri(uri + separator + query);
    }

    private void ConfigureAuth(HttpRequestMessage request)
    {
        switch (AuthTypeBox.SelectedIndex)
        {
            case 1 when !string.IsNullOrWhiteSpace(TokenBox.Password): request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenBox.Password); break;
            case 2: request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UsernameBox.Text}:{PasswordBox.Password}"))); break;
        }
    }

    private static string SelectedContent(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
    private static string FormatBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(Empty response)";
        try { using var document = JsonDocument.Parse(body); return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return body; }
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { _ = SendAsync(); e.Handled = true; } }
    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) { if (!_isRestoringTab && _activeRequestTab is not null) { _activeRequestTab.Url = UrlBox.Text; UpdateRequestTabHeader(_activeRequestTab); } }
    private void MethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_isRestoringTab && _activeRequestTab is not null) { _activeRequestTab.MethodIndex = Math.Max(0, MethodBox.SelectedIndex); UpdateRequestTabHeader(_activeRequestTab); } }
    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e) => RequestJsonTree.SetJson(BodyBox.Text);
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

    private void CopyResponse_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(ResponseBodyBox.Text)) Clipboard.SetText(ResponseBodyBox.Text); }

    public sealed class RequestField { public bool IsEnabled { get; set; } = true; public string Key { get; set; } = string.Empty; public string? Value { get; set; } public RequestField Clone() => new() { IsEnabled = IsEnabled, Key = Key, Value = Value }; }

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
        public Border Header { get; set; } = null!;
        public TextBlock? MethodText { get; set; }
        public TextBlock? TitleText { get; set; }
    }
}
