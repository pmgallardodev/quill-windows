using System.ComponentModel;
using System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

namespace Quill.Windows.UI;

/// <summary>
/// Transcript tab. Scrolls to the current search match (Enter / Shift+Enter move between
/// matches) and, while following playback, to the segment being heard.
/// </summary>
internal sealed partial class TranscriptTabView : WpfControls.UserControl
{
    private TranscriptViewModel? _viewModel;

    public TranscriptTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as TranscriptViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ScrollToMatch();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranscriptViewModel.CurrentMatch))
        {
            ScrollToMatch();
        }
        else if (e.PropertyName == nameof(TranscriptViewModel.PlayingEntry) &&
                 _viewModel is { FollowPlayback: true, PlayingEntry: { } playing, Player.IsPlaying: true })
        {
            Lines.ScrollIntoView(playing);
        }
    }

    private void ScrollToMatch()
    {
        if (_viewModel?.CurrentMatch is { } match)
        {
            // After layout, so a list that was just filled can scroll.
            Dispatcher.BeginInvoke(() => Lines.ScrollIntoView(match));
        }
    }

    /// <summary>Double-click on a line's text opens its editor.</summary>
    private void OnLineTextMouseDown(object sender, WpfInput.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: TranscriptEntryViewModel entry })
        {
            e.Handled = true;
            Execute(_viewModel?.BeginEditCommand, entry);
        }
    }

    private void OnEditMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TranscriptEntryViewModel entry })
        {
            Execute(_viewModel?.BeginEditCommand, entry);
        }
    }

    /// <summary>Enter saves, Esc cancels.</summary>
    private void OnEditorKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement { DataContext: TranscriptEntryViewModel entry })
        {
            return;
        }

        if (e.Key == WpfInput.Key.Enter)
        {
            e.Handled = true;
            Execute(_viewModel.SaveEditCommand, entry);
        }
        else if (e.Key == WpfInput.Key.Escape)
        {
            e.Handled = true;
            Execute(_viewModel.CancelEditCommand, entry);
        }
    }

    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is WpfControls.TextBox editor)
        {
            Dispatcher.BeginInvoke(() =>
            {
                editor.Focus();
                editor.SelectAll();
            });
        }
    }

    private static void Execute(WpfInput.ICommand? command, object parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }

    private void OnSearchKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (_viewModel is null || e.Key != WpfInput.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        var command = (WpfInput.Keyboard.Modifiers & WpfInput.ModifierKeys.Shift) != 0
            ? _viewModel.PreviousMatchCommand
            : _viewModel.NextMatchCommand;
        command.Execute(null);
    }
}
