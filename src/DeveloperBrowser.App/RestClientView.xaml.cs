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

    public ObservableCollection<RequestField> Parameters { get; } = [new()];
    public ObservableCollection<RequestField> Headers { get; } = [new()];

    public RestClientView()
    {
        InitializeComponent();
        DataContext = this;
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
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(66, 58, 105));
            ResponseMetaText.Text = "Waiting for server…";
            var stopwatch = Stopwatch.StartNew();
            using var response = await _httpClient.SendAsync(request);
            stopwatch.Stop();

            var responseBody = await response.Content.ReadAsStringAsync();
            ResponseBodyBox.Text = FormatBody(responseBody);
            ResponseHeadersBox.Text = string.Join(Environment.NewLine, response.Headers.Concat(response.Content.Headers).Select(header => $"{header.Key}: {string.Join(", ", header.Value)}"));
            StatusText.Text = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            StatusBadge.Background = new SolidColorBrush(response.IsSuccessStatusCode ? Color.FromRgb(29, 100, 70) : Color.FromRgb(128, 56, 64));
            ResponseMetaText.Text = $"{stopwatch.ElapsedMilliseconds} ms · {responseBody.Length:N0} B";
        }
        catch (Exception exception)
        {
            StatusText.Text = "Request failed";
            StatusBadge.Background = new SolidColorBrush(Color.FromRgb(128, 56, 64));
            ResponseMetaText.Text = exception.GetType().Name;
            ResponseBodyBox.Text = exception.Message;
            ResponseHeadersBox.Text = string.Empty;
        }
    }

    private Uri BuildUri(string input)
    {
        input = input.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            throw new UriFormatException("Enter a complete URL, for example https://api.example.com/items.");

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
            case 1 when !string.IsNullOrWhiteSpace(TokenBox.Password):
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenBox.Password);
                break;
            case 2:
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UsernameBox.Text}:{PasswordBox.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                break;
        }
    }

    private static string SelectedContent(ComboBox comboBox) => (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

    private static string FormatBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(Empty response)";
        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return body; }
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = SendAsync();
        e.Handled = true;
    }

    private void AddParameter_Click(object sender, RoutedEventArgs e) => Parameters.Add(new RequestField());
    private void AddHeader_Click(object sender, RoutedEventArgs e) => Headers.Add(new RequestField());

    private void AuthTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BearerPanel is null || BasicPanel is null) return;
        BearerPanel.Visibility = AuthTypeBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BasicPanel.Visibility = AuthTypeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyResponse_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ResponseBodyBox.Text)) Clipboard.SetText(ResponseBodyBox.Text);
    }

    public sealed class RequestField
    {
        public bool IsEnabled { get; set; } = true;
        public string Key { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}
