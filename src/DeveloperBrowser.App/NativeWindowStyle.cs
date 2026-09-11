using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeveloperBrowser.App;

internal static class NativeWindowStyle
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int GwlExStyle = -20;
    private const int WsExDlgModalFrame = 0x00000001;
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    // COLORREF values are encoded as 0x00BBGGRR rather than the usual RGB order.
    private const int CaptionColor = 0x00271D18; // #181D27
    private const int BorderColor = 0x0058453A;  // #3A4558
    private const int TextColor = 0x00FCFAF8;    // #F8FAFC

    public static void ApplyModernDarkChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var enabled = 1;
        SetAttribute(handle, DwmUseImmersiveDarkMode, ref enabled);

        var cornerPreference = DwmWindowCornerPreferenceRound;
        SetAttribute(handle, DwmWindowCornerPreference, ref cornerPreference);

        var captionColor = CaptionColor;
        SetAttribute(handle, DwmCaptionColor, ref captionColor);

        var borderColor = BorderColor;
        SetAttribute(handle, DwmBorderColor, ref borderColor);

        var textColor = TextColor;
        SetAttribute(handle, DwmTextColor, ref textColor);

        // Suppress the caption icon while preserving the native caption buttons.
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle | WsExDlgModalFrame));
        // WPF explicitly sets a small caption icon even with WS_EX_DLGMODALFRAME.
        // Clear both window icons, otherwise Windows scales the large icon into
        // the caption. The executable and package still supply the shell icon.
        _ = SendMessage(handle, WmSetIcon, new IntPtr(IconSmall), IntPtr.Zero);
        _ = SendMessage(handle, WmSetIcon, new IntPtr(IconBig), IntPtr.Zero);
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    private static void SetAttribute(IntPtr handle, int attribute, ref int value)
    {
        // Unsupported attributes are intentionally ignored on older Windows versions.
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
