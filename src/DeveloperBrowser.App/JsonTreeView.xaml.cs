using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace DeveloperBrowser.App;

public partial class JsonTreeView : UserControl
{
    public JsonTreeView() => InitializeComponent();
    public string? SelectedValue => (Tree.SelectedItem as JsonTreeNode)?.Value;
    public string? SelectedPath => (Tree.SelectedItem as JsonTreeNode)?.Path;

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
            Tree.ItemsSource = new[] { CreateNode("root", "$", document.RootElement) };
            Tree.Visibility = System.Windows.Visibility.Visible;
            EmptyText.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch (JsonException)
        {
            ShowEmpty("This body is not valid JSON.");
        }
    }

    public void SetXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) { Clear("Valid XML will appear as an expandable tree."); return; }
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root is null) { Clear("This body has no XML root element."); return; }
            Tree.ItemsSource = new[] { CreateXmlNode(root, $"/{root.Name.LocalName}[1]") };
            Tree.Visibility = System.Windows.Visibility.Visible;
            EmptyText.Visibility = System.Windows.Visibility.Collapsed;
        }
        catch (Exception) { Clear("This body is not valid XML."); }
    }

    public void Clear(string message) => ShowEmpty(message);

    public bool Find(string query)
    {
        foreach (var root in Tree.Items.OfType<JsonTreeNode>())
        {
            var match = Find(root, query);
            if (match is null) continue;
            Tree.Focus();
            return SelectNode(Tree, match);
        }
        return false;
    }

    private void ShowEmpty(string message)
    {
        Tree.ItemsSource = null;
        Tree.Visibility = System.Windows.Visibility.Collapsed;
        EmptyText.Text = message;
        EmptyText.Visibility = System.Windows.Visibility.Visible;
    }

    private static JsonTreeNode CreateNode(string key, string path, JsonElement element)
    {
        var node = new JsonTreeNode(key, path, element.ValueKind switch
        {
            JsonValueKind.Object => $"{{ }}  ({element.EnumerateObject().Count()} keys)",
            JsonValueKind.Array => $"[ ]  ({element.GetArrayLength()} items)",
            JsonValueKind.String => $"\"{element.GetString()}\"",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        }, element.ValueKind is JsonValueKind.Object or JsonValueKind.Array);

        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) node.Children.Add(CreateNode(property.Name, $"{path}[{JsonSerializer.Serialize(property.Name)}]", property.Value));
        else if (element.ValueKind == JsonValueKind.Array)
            for (var index = 0; index < element.GetArrayLength(); index++) node.Children.Add(CreateNode($"[{index}]", $"{path}[{index}]", element[index]));
        return node;
    }

    private static JsonTreeNode CreateXmlNode(XElement element, string path)
    {
        var node = new JsonTreeNode(element.Name.LocalName, path, element.HasElements ? $"<{element.Name.LocalName}> ({element.Elements().Count()} children)" : element.Value.Trim(), element.HasElements);
        foreach (var attribute in element.Attributes()) node.Children.Add(new JsonTreeNode($"@{attribute.Name.LocalName}", $"{path}/@{attribute.Name.LocalName}", attribute.Value, false));
        var sameName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            sameName[name] = sameName.GetValueOrDefault(name) + 1;
            node.Children.Add(CreateXmlNode(child, $"{path}/{name}[{sameName[name]}]"));
        }
        return node;
    }

    private static JsonTreeNode? Find(JsonTreeNode node, string query)
    {
        if (node.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || node.Value.Contains(query, StringComparison.OrdinalIgnoreCase)) return node;
        return node.Children.Select(child => Find(child, query)).FirstOrDefault(match => match is not null);
    }

    private static bool SelectNode(ItemsControl parent, JsonTreeNode node)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item)
        {
            item.IsSelected = true;
            item.BringIntoView();
            return true;
        }
        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem childItem) continue;
            childItem.IsExpanded = true;
            if (child is JsonTreeNode childNode && (ReferenceEquals(childNode, node) || SelectNode(childItem, node))) return true;
        }
        return false;
    }
}

public sealed class JsonTreeNode(string key, string path, string value, bool isContainer)
{
    public string Key { get; } = key;
    public string Path { get; } = path;
    public string Value { get; } = value;
    public string Separator => isContainer ? "  " : ": ";
    public Brush ValueBrush => isContainer ? new SolidColorBrush(Color.FromRgb(144, 152, 168)) : new SolidColorBrush(Color.FromRgb(147, 197, 253));
    public ObservableCollection<JsonTreeNode> Children { get; } = [];
}
