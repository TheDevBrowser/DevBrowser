using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeveloperBrowser.Core.Rest;

namespace DeveloperBrowser.App;

public partial class RestClientView
{
    private static string RedirectDefaultsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBrowser", "rest-redirects.json");

    private static RedirectSettings LoadRedirectDefaults()
    {
        try
        {
            if (!File.Exists(RedirectDefaultsPath)) return new();
            var settings = JsonSerializer.Deserialize<RedirectSettings>(File.ReadAllText(RedirectDefaultsPath)) ?? new();
            settings.Validate();
            // Trust is always request-specific, never granted through the defaults file.
            return settings with { TrustedOrigins = [] };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new();
        }
    }

    private void RedirectDefaults_Click(object sender, RoutedEventArgs e) => EditRedirectSettings(true);
    private void RequestRedirectSettings_Click(object sender, RoutedEventArgs e) => EditRedirectSettings(false);

    private Window RedirectWindow(string title, StackPanel panel)
    {
        var window = new Window
        {
            Title = title, Owner = Window.GetWindow(this), Width = 660, SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * 0.9, MaxWidth = SystemParameters.WorkArea.Width,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("PrimaryTextBrush"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        // A separate Window does not inherit the REST view's control resources.
        var checks = new Style(typeof(CheckBox));
        checks.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("PrimaryTextBrush")));
        checks.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        checks.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 2, 0, 2)));
        checks.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
        checks.Triggers.Add(disabled);
        window.Resources.Add(typeof(CheckBox), checks);
        var buttons = new Style(typeof(Button), (Style)FindResource("ResponseCopyButton"));
        buttons.Setters.Add(new Setter(Control.MinHeightProperty, 36d));
        buttons.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        buttons.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 8, 16, 8)));
        window.Resources.Add(typeof(Button), buttons);
        window.SourceInitialized += (_, _) => NativeWindowStyle.ApplyModernDarkChrome(window);
        return window;
    }

    private CheckBox RedirectOption(string title, string description)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
        var option = new CheckBox { Content = content, IsChecked = false, Margin = new Thickness(0, 10, 0, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(option, title);
        return option;
    }

    private Border RedirectCard(UIElement content) => new()
    {
        Child = content, Background = (Brush)FindResource("SurfaceBrush"), BorderBrush = (Brush)FindResource("BorderBrush"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Margin = new Thickness(0, 8, 0, 12)
    };

    private static TextBlock RedirectLabel(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
    };

    private void EditRedirectSettings(bool defaults)
    {
        if (_activeRequestTab is null && !defaults) return;
        var settings = defaults ? LoadRedirectDefaults() : _activeRequestTab!.RedirectSettings ?? LoadRedirectDefaults();
        var trusts = settings.TrustedOrigins.ToList();
        var panel = new StackPanel { Margin = new Thickness(24) };
        var window = RedirectWindow(defaults ? "REST client — Redirect defaults" : "Request — Redirect settings", panel);
        panel.Children.Add(RedirectLabel(defaults ? "Defaults for requests without an override. Ordinary browsing is unaffected."
            : _activeRequestTab!.RedirectSettings is null ? "This request currently uses REST client defaults. Save to create an override."
            : "This request has its own redirect settings. Save the request to keep these settings after reopening it."));
        var follow = new CheckBox { Content = "Automatically follow redirects", IsChecked = settings.FollowRedirects, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(follow);
        panel.Children.Add(RedirectLabel("Credentials and custom headers sent to another origin"));
        var headers = new ComboBox { ItemsSource = new[] { "Ask — offer to remove headers or approve forwarding", "Remove without asking" }, SelectedIndex = (int)settings.Headers };
        panel.Children.Add(headers);
        panel.Children.Add(RedirectLabel("Request body sent to another origin (when the redirect preserves it)"));
        var body = new ComboBox { ItemsSource = new[] { "Ask before sending", "Block", "Allow" }, SelectedIndex = (int)settings.Body };
        panel.Children.Add(body);
        var downgrade = new CheckBox { Content = "Allow HTTPS → HTTP redirects (unencrypted connection)", IsChecked = settings.AllowHttpsToHttp, Margin = new Thickness(0, 16, 0, 8) };
        panel.Children.Add(downgrade);
        panel.Children.Add(RedirectLabel("Maximum redirects (0–50)"));
        var maximum = new TextBox { Text = settings.MaxRedirects.ToString(), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(maximum);
        if (!defaults)
        {
            panel.Children.Add(RedirectLabel("Trusted origin pairs for this request (scheme, hostname and port must match exactly)"));
            var list = new ListBox { MaxHeight = 120, ItemsSource = trusts.Select(DescribeTrust).ToList() };
            panel.Children.Add(list);
            var revoke = new Button { Content = "Remove selected exception", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            revoke.Click += (_, _) =>
            {
                if (list.SelectedIndex < 0) return;
                trusts.RemoveAt(list.SelectedIndex);
                list.ItemsSource = trusts.Select(DescribeTrust).ToList();
            };
            panel.Children.Add(revoke);
        }
        var error = RedirectLabel(""); panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(4) };
        cancel.Click += (_, _) => window.Close(); buttons.Children.Add(cancel);
        if (!defaults)
        {
            var reset = new Button { Content = "Use defaults", Margin = new Thickness(4) };
            reset.Click += (_, _) => { _activeRequestTab!.RedirectSettings = null; MarkDirty(); window.Close(); };
            buttons.Children.Add(reset);
        }
        var save = new Button { Content = "Save", IsDefault = true, Margin = new Thickness(4) };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(maximum.Text, out var limit) || limit is < 0 or > 50) { error.Text = "Enter a redirect limit between 0 and 50."; return; }
            var updated = new RedirectSettings
            {
                FollowRedirects = follow.IsChecked == true, Headers = (CrossOriginHeaderPolicy)headers.SelectedIndex,
                Body = (CrossOriginBodyPolicy)body.SelectedIndex, AllowHttpsToHttp = downgrade.IsChecked == true,
                MaxRedirects = limit, TrustedOrigins = defaults ? [] : trusts
            };
            if ((updated.AllowHttpsToHttp && !settings.AllowHttpsToHttp) ||
                (updated.Body == CrossOriginBodyPolicy.Allow && settings.Body != CrossOriginBodyPolicy.Allow))
                if (MessageBox.Show(window, "These settings can send request data to another origin or over an unencrypted connection. Apply them?",
                    "Redirect settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                updated.Validate();
                if (defaults)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(RedirectDefaultsPath)!);
                    var temporary = RedirectDefaultsPath + ".tmp";
                    File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
                    File.Move(temporary, RedirectDefaultsPath, true);
                }
                else { _activeRequestTab!.RedirectSettings = updated; MarkDirty(); }
                window.Close();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            { error.Text = "Could not save redirect settings. " + exception.GetType().Name; }
        };
        buttons.Children.Add(save); panel.Children.Add(buttons);
        window.ShowDialog();
    }

    private static string DescribeTrust(RedirectTrust trust) => $"{trust.SourceOrigin} → {trust.DestinationOrigin}" +
        $"  ({string.Join(", ", new[] { trust.Credentials ? "credentials" : null, trust.Body ? "body" : null }.Where(x => x is not null))})";

    private RedirectDecision ConfirmRedirect(RedirectPrompt prompt, RedirectSettings effective)
    {
        var (window, result) = CreateRedirectConfirmation(prompt, effective);
        window.ShowDialog();
        return result();
    }

    private (Window Window, Func<RedirectDecision> Result) CreateRedirectConfirmation(RedirectPrompt prompt, RedirectSettings effective)
    {
        var decision = RedirectDecision.Stop;
        var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 24) };
        var window = RedirectWindow("Review redirect", panel);
        panel.Children.Add(new TextBlock { Text = "This request is changing destination", FontSize = 22,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var intro = RedirectLabel("Review what you want to share before continuing. Nothing has been sent to the new destination.");
        intro.Foreground = (Brush)FindResource("MutedTextBrush");
        panel.Children.Add(intro);
        var route = new Grid();
        route.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        route.ColumnDefinitions.Add(new ColumnDefinition());
        var destinations = new[] { ("FROM", RedirectSettings.Origin(prompt.Source)), ("TO", RedirectSettings.Origin(prompt.Destination)) };
        for (var index = 0; index < destinations.Length; index++)
        {
            route.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = destinations[index].Item1, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MutedTextBrush"), Margin = new Thickness(0, 6, 12, 6) };
            var value = new TextBlock { Text = destinations[index].Item2, FontFamily = new FontFamily("Cascadia Mono"),
                TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 4, 0, 6) };
            Grid.SetRow(label, index); Grid.SetRow(value, index); Grid.SetColumn(value, 1);
            route.Children.Add(label); route.Children.Add(value);
        }
        var routePanel = new StackPanel(); routePanel.Children.Add(route);
        routePanel.Children.Add(new TextBlock { Text = $"{prompt.StatusCode} redirect   ·   Next request: {prompt.Method}",
            Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(RedirectCard(routePanel));
        // Do not display URL paths/queries or any header/body values in approval UI.
        var credentials = RedirectOption("Include credentials and custom headers", "Only enable this if you trust the destination with these values.");
        if (prompt.SensitiveHeaders.Count > 0)
        {
            var protection = new StackPanel();
            protection.Children.Add(new TextBlock { Text = "Excluded by default", FontWeight = FontWeights.SemiBold });
            var names = RedirectLabel(string.Join(", ", prompt.SensitiveHeaders));
            names.FontFamily = new FontFamily("Cascadia Mono"); names.Foreground = (Brush)FindResource("MutedTextBrush");
            protection.Children.Add(names); protection.Children.Add(credentials);
            panel.Children.Add(RedirectCard(protection));
        }
        var body = RedirectOption("Send the request body", "This redirect keeps the body, which may contain passwords or other private data.");
        if (prompt.NeedsBodyApproval) panel.Children.Add(RedirectCard(body));
        var remember = RedirectOption("Remember these permissions for this request", "Applies only to this source and destination. Save the request to keep the exception.");
        panel.Children.Add(remember);
        var error = RedirectLabel(""); error.Visibility = Visibility.Collapsed; panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var stop = new Button { Content = "Stop redirect", IsCancel = true, IsDefault = true, Margin = new Thickness(0, 4, 10, 4) };
        stop.Click += (_, _) => window.Close(); buttons.Children.Add(stop);
        var follow = new Button { Style = (Style)FindResource("PrimaryButton"), MinHeight = 38, Margin = new Thickness(0, 4, 0, 4) };
        void UpdateChoices()
        {
            follow.Content = prompt.SensitiveHeaders.Count > 0
                ? credentials.IsChecked == true ? "Continue with credentials" : "Continue without credentials"
                : "Continue with body";
            remember.IsEnabled = credentials.IsChecked == true || body.IsChecked == true;
            if (!remember.IsEnabled) remember.IsChecked = false;
        }
        credentials.Checked += (_, _) => UpdateChoices(); credentials.Unchecked += (_, _) => UpdateChoices();
        body.Checked += (_, _) => UpdateChoices(); body.Unchecked += (_, _) => UpdateChoices();
        UpdateChoices();
        follow.Click += (_, _) =>
        {
            if (prompt.NeedsBodyApproval && body.IsChecked != true) { error.Text = "Select ‘Send the request body’ to continue, or stop the redirect."; error.Visibility = Visibility.Visible; body.Focus(); return; }
            decision = new(true, credentials.IsChecked == true, body.IsChecked == true);
            if (remember.IsChecked == true && (decision.ForwardCredentials || decision.ForwardBody) && _activeRequestTab is not null)
            {
                var from = RedirectSettings.Origin(prompt.Source); var to = RedirectSettings.Origin(prompt.Destination);
                var previous = effective.TrustedOrigins.FirstOrDefault(t => t.SourceOrigin == from && t.DestinationOrigin == to);
                var trust = new RedirectTrust(from, to, decision.ForwardCredentials || previous?.Credentials == true,
                    decision.ForwardBody || previous?.Body == true);
                effective.TrustedOrigins.RemoveAll(t => t.SourceOrigin == from && t.DestinationOrigin == to);
                effective.TrustedOrigins.Add(trust);
                _activeRequestTab.RedirectSettings = effective with { TrustedOrigins = effective.TrustedOrigins.ToList() };
                MarkDirty();
            }
            window.Close();
        };
        buttons.Children.Add(follow); panel.Children.Add(buttons);
        window.ContentRendered += (_, _) => stop.Focus();
        return (window, () => decision);
    }
}
