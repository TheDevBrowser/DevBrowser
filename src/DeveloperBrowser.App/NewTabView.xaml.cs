using System.Windows.Controls;
using System.Windows.Input;

namespace DeveloperBrowser.App;

public partial class NewTabView : UserControl
{
    public event EventHandler<string>? NavigationRequested;

    public void FocusSearch() => SearchBox.Focus();

    public NewTabView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current is App app)
        {
            var greeting = app.Greetings.CurrentGreeting;
            GreetingText.Text = greeting.Text;
            GreetingText.ToolTip = $"{greeting.Language} · {greeting.Pronunciation}";
        }
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
