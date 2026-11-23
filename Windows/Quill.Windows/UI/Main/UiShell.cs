using System.Runtime.InteropServices;

namespace Quill.Windows.UI;

/// <summary>Desktop services the view models need, kept behind an interface so they stay testable.</summary>
internal interface IUiShell
{
    /// <summary>False when the clipboard is held by another process.</summary>
    bool TryCopyText(string text);

    /// <summary>Destructive confirmation; true only on an explicit OK.</summary>
    bool ConfirmDestructive(string title, string message);
}

internal sealed class WpfUiShell : IUiShell
{
    public static WpfUiShell Instance { get; } = new();

    public bool TryCopyText(string text)
    {
        try
        {
            // The clipboard belongs to the whole desktop: any process may hold it right now.
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (Exception exception) when (exception is ExternalException or OutOfMemoryException)
        {
            return false;
        }
    }

    public bool ConfirmDestructive(string title, string message) =>
        System.Windows.MessageBox.Show(
            message,
            title,
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.Cancel) == System.Windows.MessageBoxResult.OK;
}
