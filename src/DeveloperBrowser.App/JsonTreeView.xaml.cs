using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeveloperBrowser.App;

public partial class JsonTreeView : UserControl
{
    public JsonTreeView() => InitializeComponent();

    public void SetJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            ShowEmpty("Valid JSON will appear as an expandable tree.");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            Tree.ItemsSource = new[] { CreateNode("root", document.RootElement) };
            Tree.Visibility = System.Windows.Visibility.Visible;
            EmptyText.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch (JsonException)
        {
            ShowEmpty("This body is not valid JSON.");
        }
    }

    private void ShowEmpty(string message)
    {
        Tree.ItemsSource = null;
        Tree.Visibility = System.Windows.Visibility.Collapsed;
        EmptyText.Text = message;
        EmptyText.Visibility = System.Windows.Visibility.Visible;
    }

    private static JsonTreeNode CreateNode(string key, JsonElement element)
    {
        var node = new JsonTreeNode(key, element.ValueKind switch
        {
            JsonValueKind.Object => $"{{ }}  ({element.EnumerateObject().Count()} keys)",
            JsonValueKind.Array => $"[ ]  ({element.GetArrayLength()} items)",
            JsonValueKind.String => $"\"{element.GetString()}\"",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        }, element.ValueKind is JsonValueKind.Object or JsonValueKind.Array);

        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) node.Children.Add(CreateNode(property.Name, property.Value));
        else if (element.ValueKind == JsonValueKind.Array)
            for (var index = 0; index < element.GetArrayLength(); index++) node.Children.Add(CreateNode($"[{index}]", element[index]));
        return node;
    }
}

public sealed class JsonTreeNode(string key, string value, bool isContainer)
{
    public string Key { get; } = key;
    public string Value { get; } = value;
    public string Separator => isContainer ? "  " : ": ";
    public Brush ValueBrush => isContainer ? new SolidColorBrush(Color.FromRgb(144, 152, 168)) : new SolidColorBrush(Color.FromRgb(147, 197, 253));
    public ObservableCollection<JsonTreeNode> Children { get; } = [];
}
