using System.Text.Json;
using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using System.Text.RegularExpressions;
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
        PrettyBox.Visibility = Visibility.Visible;
        HtmlPrettyBox.Visibility = Visibility.Collapsed;
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
                StructuredDataKind.Html => FormatHtml(_raw),
                _ => _raw
            };
            PrettyBox.Text = _pretty;
            if (_kind == StructuredDataKind.Html)
            {
                PrettyBox.Visibility = Visibility.Collapsed;
                HtmlPrettyBox.Visibility = Visibility.Visible;
                ShowHighlightedHtml(_pretty);
            }
            if (_kind == StructuredDataKind.Json) TreeView.SetJson(_raw);
            else if (_kind == StructuredDataKind.Xml) TreeView.SetXml(_raw);
            else TreeTab.Visibility = Visibility.Collapsed;
            InfoText.Text = _kind switch { StructuredDataKind.Json => "JSON", StructuredDataKind.Xml => "XML", StructuredDataKind.Html => "HTML", _ => "Text" };
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
        if (_kind is StructuredDataKind.Json or StructuredDataKind.Xml or StructuredDataKind.Html)
            SetContent(_pretty, _kind == StructuredDataKind.Json ? "application/json" : _kind == StructuredDataKind.Xml ? "application/xml" : "text/html");
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
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return StructuredDataKind.Html;
            if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)) return StructuredDataKind.Xml;
        }
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) return StructuredDataKind.Json;
        if (Regex.IsMatch(trimmed, @"^<!doctype\s+html|^<html(?:\s|>)", RegexOptions.IgnoreCase)) return StructuredDataKind.Html;
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

    private static string FormatHtml(string content)
    {
        var compact = Regex.Replace(content, @">\s*<", "><").Trim();
        var output = new StringBuilder();
        var depth = 0;
        foreach (Match match in Regex.Matches(compact, @"<!--[\s\S]*?-->|<![^>]*>|<[^>]+>|[^<]+"))
        {
            var token = match.Value.Trim();
            if (token.Length == 0) continue;
            var closing = token.StartsWith("</", StringComparison.Ordinal);
            if (closing) depth = Math.Max(0, depth - 1);
            if (output.Length > 0) output.AppendLine();
            output.Append(' ', depth * 2).Append(token);
            if (token.StartsWith('<') && !closing && !token.StartsWith("<!", StringComparison.Ordinal) &&
                !token.EndsWith("/>", StringComparison.Ordinal) && !Regex.IsMatch(token, @"^<(area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)\b", RegexOptions.IgnoreCase))
                depth++;
        }
        return output.ToString();
    }

    private void ShowHighlightedHtml(string html)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12 };
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var pattern = @"<!--[\s\S]*?-->|</?[A-Za-z][^>]*>|[^<]+";
        foreach (Match token in Regex.Matches(html, pattern))
        {
            if (token.Value.StartsWith("<!--", StringComparison.Ordinal))
                paragraph.Inlines.Add(new Run(token.Value) { Foreground = new SolidColorBrush(Color.FromRgb(106, 153, 85)) });
            else if (token.Value.StartsWith('<'))
                AddHighlightedTag(paragraph, token.Value);
            else
                paragraph.Inlines.Add(new Run(token.Value) { Foreground = new SolidColorBrush(Color.FromRgb(220, 225, 232)) });
        }
        document.Blocks.Add(paragraph);
        HtmlPrettyBox.Document = document;
    }

    private static void AddHighlightedTag(Paragraph paragraph, string tag)
    {
        var position = 0;
        foreach (Match attribute in Regex.Matches(tag, "\\s+[A-Za-z_:][-A-Za-z0-9_:.]*(?:\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s>]+))?"))
        {
            if (attribute.Index > position) paragraph.Inlines.Add(new Run(tag[position..attribute.Index]) { Foreground = new SolidColorBrush(Color.FromRgb(86, 156, 214)) });
            var equals = attribute.Value.IndexOf('=');
            if (equals < 0) paragraph.Inlines.Add(new Run(attribute.Value) { Foreground = new SolidColorBrush(Color.FromRgb(156, 220, 254)) });
            else
            {
                paragraph.Inlines.Add(new Run(attribute.Value[..(equals + 1)]) { Foreground = new SolidColorBrush(Color.FromRgb(156, 220, 254)) });
                paragraph.Inlines.Add(new Run(attribute.Value[(equals + 1)..]) { Foreground = new SolidColorBrush(Color.FromRgb(206, 145, 120)) });
            }
            position = attribute.Index + attribute.Length;
        }
        if (position < tag.Length) paragraph.Inlines.Add(new Run(tag[position..]) { Foreground = new SolidColorBrush(Color.FromRgb(86, 156, 214)) });
    }

    private enum StructuredDataKind { Text, Json, Xml, Html }
}
