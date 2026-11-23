using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Quill.Windows.UI.Theme;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using FormsCursor = System.Windows.Forms.Cursor;
using FormsScreen = System.Windows.Forms.Screen;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

namespace Quill.Windows.UI.Companion;

/// <summary>
/// Floating, always-on-top companion panel. Appears without taking focus, stays out of the
/// taskbar and Alt+Tab (tool window), docks to the right edge of the monitor under the mouse
/// and remembers where the user dragged it. Closing it only collapses it to the pill; the host
/// closes it for real through <see cref="CompanionPanelModule.Close"/>.
/// </summary>
internal partial class CompanionWindow : FluentWindow
{
    private const double ExpandedWidth = 360;
    private const double ExpandedHeight = 620;
    private const double ClickSlop = 3;

    private readonly CompanionViewModel _viewModel;
    private readonly CompanionPlacementStore _placement;
    private readonly DispatcherTimer _ticker;
    private readonly AutoScrollPolicy _autoScroll = new();

    public CompanionWindow(CompanionViewModel viewModel, CompanionPlacementStore placement)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _placement = placement ?? throw new ArgumentNullException(nameof(placement));
        InitializeComponent();
        ThemeBootstrapper.Attach(this);
        DataContext = viewModel;

        // About 15 updates per second: smooth meters, negligible cost.
        _ticker = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(66) };
        _ticker.Tick += (_, _) => _viewModel.Tick();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => _ticker.Start();
        Closed += (_, _) =>
        {
            _ticker.Stop();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            SavePlacement();
        };
    }

    /// <summary>Set by the module before <see cref="Window.Close"/>; otherwise closing collapses.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            _viewModel.IsCollapsed = true;
            return;
        }

        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        HideFromAltTab();
        var saved = _placement.Load();
        _viewModel.IsCollapsed = saved?.Collapsed ?? false;
        ApplySize();

        var width = _viewModel.IsCollapsed ? 240 : ExpandedWidth;
        var height = _viewModel.IsCollapsed ? 48 : ExpandedHeight;
        if (saved is not null && WorkAreaContaining(saved.Left + 8, saved.Top + 8) is { } savedArea)
        {
            (Left, Top) = CompanionPlacement.Clamp(saved.Left, saved.Top, width, height, savedArea);
        }
        else
        {
            var area = WorkAreaUnderCursor();
            (Left, Top) = CompanionPlacement.DockRight(area, width);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CompanionViewModel.IsCollapsed) || !IsLoaded)
        {
            return;
        }

        var rightEdge = Left + ActualWidth;
        ApplySize();
        // Wait for the new size, then keep the right edge where it was (the panel is docked right).
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var area = WorkAreaContaining(Left + 8, Top + 8) ?? WorkAreaUnderCursor();
            (Left, Top) = CompanionPlacement.Clamp(rightEdge - ActualWidth, Top, ActualWidth, ActualHeight, area);
            SavePlacement();
        });
    }

    private void ApplySize()
    {
        if (_viewModel.IsCollapsed)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            Width = ExpandedWidth;
            Height = ExpandedHeight;
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, WpfInput.MouseButtonEventArgs e)
    {
        if (e.ButtonState == WpfInput.MouseButtonState.Pressed)
        {
            DragMove();
            SavePlacement();
        }
    }

    /// <summary>Drag moves the pill; a click (no movement) expands it.</summary>
    private void OnPillMouseLeftButtonDown(object sender, WpfInput.MouseButtonEventArgs e)
    {
        if (e.ButtonState != WpfInput.MouseButtonState.Pressed)
        {
            return;
        }

        var (left, top) = (Left, Top);
        DragMove();
        if (Math.Abs(Left - left) < ClickSlop && Math.Abs(Top - top) < ClickSlop)
        {
            _viewModel.IsCollapsed = false;
        }
        else
        {
            SavePlacement();
        }
    }

    private void OnTranscriptScrollChanged(object sender, WpfControls.ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not WpfControls.ScrollViewer scroller)
        {
            return;
        }

        if (e.ViewportHeightChange != 0 && e.ExtentHeightChange == 0 && _autoScroll.IsFollowing)
        {
            // Resized or the tab became visible: stay at the end.
            scroller.ScrollToEnd();
            return;
        }

        if (_autoScroll.OnScrollChanged(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight, e.ExtentHeightChange))
        {
            scroller.ScrollToEnd();
        }

        JumpToEnd.Visibility = _autoScroll.HasUnseen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnJumpToEndClick(object sender, RoutedEventArgs e)
    {
        _autoScroll.Resume();
        JumpToEnd.Visibility = Visibility.Collapsed;
        (TranscriptList.Template.FindName("Scroller", TranscriptList) as WpfControls.ScrollViewer)?.ScrollToEnd();
    }

    private void SavePlacement()
    {
        if (double.IsFinite(Left) && double.IsFinite(Top))
        {
            _placement.Save(new CompanionPanelState(Left, Top, _viewModel.IsCollapsed));
        }
    }

    private PanelRect WorkAreaUnderCursor() =>
        ToDips(FormsScreen.FromPoint(FormsCursor.Position).WorkingArea);

    private PanelRect? WorkAreaContaining(double left, double top)
    {
        foreach (var screen in FormsScreen.AllScreens)
        {
            var area = ToDips(screen.WorkingArea);
            if (area.Contains(left, top))
            {
                return area;
            }
        }

        return null;
    }

    /// <summary>Screen pixels to WPF units, with the DPI of this window's monitor.</summary>
    private PanelRect ToDips(System.Drawing.Rectangle pixels)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        var scaleX = transform?.M11 ?? 1;
        var scaleY = transform?.M22 ?? 1;
        return new PanelRect(pixels.Left * scaleX, pixels.Top * scaleY, pixels.Width * scaleX, pixels.Height * scaleY);
    }

    /// <summary>Tool window: no taskbar button and not listed in Alt+Tab.</summary>
    private void HideFromAltTab()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = (style | WsExToolWindow) & ~WsExAppWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
