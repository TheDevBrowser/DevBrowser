using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using System.Xml.Linq;

namespace DeveloperBrowser.App;

/// <summary>Reusable local viewer for JSON and XML response bodies.</summary>
public partial class StructuredDataViewer : UserControl
{
    private const int StructuredParseLimit = 750_000;
    private StructuredDataKind _kind;
    private string _raw = string.Empty;
    private string _pretty = string.Empty;

    public StructuredDataViewer() => InitializeComponent();
    public string RawText => _raw;

    public void SetContent(string? content, string? contentType = null)
    {
        _raw = content ?? string.Empty;
        RawBox.Text = _raw;
        _kind = DetectKind(_raw, contentType);
        TreeTab.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(_raw))
        {
            _pretty = "(Empty response)";
            PrettyBox.Text = _pretty;
            TreeView.Clear("No structured content.");
            InfoText.Text = "Empty";
            return;
        }

        if (_raw.Length > StructuredParseLimit)
        {
            _pretty = _raw;
            PrettyBox.Text = _pretty;
            TreeTab.Visibility = Visibility.Collapsed;
            InfoText.Text = "Large response — raw mode";
            return;
        }

        try
        {
            _pretty = _kind switch
            {
                StructuredDataKind.Json => FormatJson(_raw, false),
                StructuredDataKind.Xml => FormatXml(_raw, false),
                _ => _raw
            };
            PrettyBox.Text = _pretty;
            if (_kind == StructuredDataKind.Json) TreeView.SetJson(_raw);
            else if (_kind == StructuredDataKind.Xml) TreeView.SetXml(_raw);
            else TreeTab.Visibility = Visibility.Collapsed;
            InfoText.Text = _kind switch { StructuredDataKind.Json => "JSON", StructuredDataKind.Xml => "XML", _ => "Text" };
        }
        catch (Exception) when (_kind is StructuredDataKind.Json or StructuredDataKind.Xml)
        {
            _kind = StructuredDataKind.Text;
            _pretty = _raw;
            PrettyBox.Text = _pretty;
            TreeTab.Visibility = Visibility.Collapsed;
            InfoText.Text = "Unstructured text";
        }
    }

    public void CopyFullContent() => Copy(_raw);

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_kind is StructuredDataKind.Json or StructuredDataKind.Xml) SetContent(_pretty, _kind == StructuredDataKind.Json ? "application/json" : "application/xml");
    }

    private void Minify_Click(object sender, RoutedEventArgs e)
    {
        if (_kind == StructuredDataKind.Json) SetContent(FormatJson(_raw, true), "application/json");
        else if (_kind == StructuredDataKind.Xml) SetContent(FormatXml(_raw, true), "application/xml");
    }

    private void CopyFull_Click(object sender, RoutedEventArgs e) => CopyFullContent();
    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        var box = ActiveTextBox();
        Copy(box?.SelectedText is { Length: > 0 } text ? text : TreeView.SelectedValue);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy(TreeView.SelectedPath);
    private void Find_Click(object sender, RoutedEventArgs e) => FindNext();
    private void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) FindNext(); }

    private void FindNext()
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query)) return;
        var box = ActiveTextBox();
        if (box is not null)
        {
            var index = box.Text.IndexOf(query, Math.Max(0, box.SelectionStart + box.SelectionLength), StringComparison.OrdinalIgnoreCase);
            if (index < 0) index = box.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                box.Focus();
                box.Select(index, query.Length);
                box.ScrollToLine(box.GetLineIndexFromCharacterIndex(index));
                InfoText.Text = "Match found";
                return;
            }
        }
        else if (TreeView.Find(query)) { InfoText.Text = "Match found"; return; }
        InfoText.Text = "No match";
    }

    private TextBox? ActiveTextBox() =>
        PrettyBox.IsVisible ? PrettyBox :
        RawBox.IsVisible ? RawBox : null;

    private static void Copy(string? value)
    {
        if (!string.IsNullOrEmpty(value)) Clipboard.SetText(value);
    }

    private static StructuredDataKind DetectKind(string content, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return StructuredDataKind.Json;
            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)) return StructuredDataKind.Xml;
        }
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return StructuredDataKind.Json;
        if (trimmed.StartsWith('<')) return StructuredDataKind.Xml;
        return StructuredDataKind.Text;
    }

    private static string FormatJson(string content, bool minify)
    {
        using var document = JsonDocument.Parse(content);
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = !minify });
    }

    private static string FormatXml(string content, bool minify)
    {
        var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        if (minify) return document.ToString(SaveOptions.DisableFormatting);
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = document.Declaration is null };
        using var writer = new StringWriter();
        using (var xml = XmlWriter.Create(writer, settings)) document.Save(xml);
        return writer.ToString();
    }

    private enum StructuredDataKind { Text, Json, Xml }
}
