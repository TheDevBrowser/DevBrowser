using System.Windows.Controls;
using System.Windows.Input;

namespace DeveloperBrowser.App;

public partial class NewTabView : UserControl
{
    // Add or remove messages here. Pick once per new-tab view, not on tab switches.
    private static readonly string[] StatusMessages =
    [
        "awaiting destination_",
        "ready.",
        "session initialized.",
        "new tab initialized.",
        "localhost is calling.",
        "waiting for input_",
        "no requests pending. yet.",
        "environment: developer",
        "works on my machine.",
        "DNS is innocent. probably.",
        "cache cleared. confidence restored.",
        "break something responsibly.",
        "ship something.",
        "HTTP 200 — ready.",
        "connection established.",
        "where to, dev?",
        "compiling thoughts...",
        "new tab. clean state. questionable intentions.",
        "nothing to debug. yet."
    ];

    public event EventHandler<string>? NavigationRequested;

    public void FocusSearch() => SearchBox.Focus();

    public NewTabView()
    {
        InitializeComponent();
        StatusMessage.Text = StatusMessages[Random.Shared.Next(StatusMessages.Length)];
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SubmitSearch();
    }

    private void Search_Click(object sender, System.Windows.RoutedEventArgs e) => SubmitSearch();

    private void SubmitSearch()
    {
        var input = SearchBox.Text.Trim();
        if (input.Length > 0) NavigationRequested?.Invoke(this, input);
    }
}
