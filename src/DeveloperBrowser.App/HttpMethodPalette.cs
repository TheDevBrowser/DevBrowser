using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DeveloperBrowser.App;

public static class HttpMethodPalette
{
    private static readonly Brush Get = Frozen(88, 214, 141);
    private static readonly Brush Post = Frozen(93, 173, 226);
    private static readonly Brush Put = Frozen(245, 176, 65);
    private static readonly Brush Patch = Frozen(187, 143, 206);
    private static readonly Brush Delete = Frozen(236, 112, 99);
    private static readonly Brush Head = Frozen(72, 201, 176);
    private static readonly Brush Options = Frozen(174, 182, 194);
    private static readonly Brush Other = Frozen(210, 190, 120);

    private static readonly Brush GetBackground = Frozen(20, 77, 67);
    private static readonly Brush PostBackground = Frozen(44, 68, 108);
    private static readonly Brush PutBackground = Frozen(91, 65, 29);
    private static readonly Brush PatchBackground = Frozen(67, 48, 88);
    private static readonly Brush DeleteBackground = Frozen(91, 42, 49);
    private static readonly Brush HeadBackground = Frozen(22, 72, 70);
    private static readonly Brush OptionsBackground = Frozen(53, 65, 82);
    private static readonly Brush OtherBackground = Frozen(70, 61, 38);

    public static Brush Foreground(string? method) => Normalize(method) switch
    {
        "GET" => Get,
        "POST" => Post,
        "PUT" => Put,
        "PATCH" => Patch,
        "DELETE" => Delete,
        "HEAD" => Head,
        "OPTIONS" => Options,
        _ => Other
    };

    public static Brush Background(string? method) => Normalize(method) switch
    {
        "GET" => GetBackground,
        "POST" => PostBackground,
        "PUT" => PutBackground,
        "PATCH" => PatchBackground,
        "DELETE" => DeleteBackground,
        "HEAD" => HeadBackground,
        "OPTIONS" => OptionsBackground,
        _ => OtherBackground
    };

    private static string Normalize(string? method) => method?.Trim().ToUpperInvariant() ?? string.Empty;

    private static Brush Frozen(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}

public sealed class HttpMethodBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        HttpMethodPalette.Foreground(value?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
