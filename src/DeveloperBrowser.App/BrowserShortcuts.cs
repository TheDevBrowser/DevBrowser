using System.Windows.Input;

namespace DeveloperBrowser.App;

public enum BrowserShortcutAction { NewTab, ReopenTab, CloseTab, FocusAddress, NextTab, PreviousTab, Reload }

public sealed record BrowserShortcut(BrowserShortcutAction Action, Key Key, ModifierKeys Modifiers, string Label, string[] Keys);

public static class BrowserShortcuts
{
    // The keyboard handler and the new-tab hints share this catalog.
    public static IReadOnlyList<BrowserShortcut> All { get; } = Array.AsReadOnly(new[]
    {
        new BrowserShortcut(BrowserShortcutAction.NewTab, Key.T, ModifierKeys.Control, "New tab", ["Ctrl", "T"]),
        new BrowserShortcut(BrowserShortcutAction.ReopenTab, Key.T, ModifierKeys.Control | ModifierKeys.Shift, "Reopen closed tab", ["Ctrl", "Shift", "T"]),
        new BrowserShortcut(BrowserShortcutAction.CloseTab, Key.W, ModifierKeys.Control, "Close tab", ["Ctrl", "W"]),
        new BrowserShortcut(BrowserShortcutAction.FocusAddress, Key.L, ModifierKeys.Control, "Address bar", ["Ctrl", "L"]),
        new BrowserShortcut(BrowserShortcutAction.NextTab, Key.Tab, ModifierKeys.Control, "Next tab", ["Ctrl", "Tab"]),
        new BrowserShortcut(BrowserShortcutAction.PreviousTab, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, "Previous tab", ["Ctrl", "Shift", "Tab"]),
        new BrowserShortcut(BrowserShortcutAction.Reload, Key.R, ModifierKeys.Control, "Reload", ["Ctrl", "R"])
    });
}
