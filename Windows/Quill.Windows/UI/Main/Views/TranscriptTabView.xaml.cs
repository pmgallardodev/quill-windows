using System.ComponentModel;
using System.Windows;
using WpfControls = System.Windows.Controls;
using WpfInput = System.Windows.Input;

namespace Quill.Windows.UI;

/// <summary>Transcript tab. Scrolls to the current search match; Enter / Shift+Enter move between matches.</summary>
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
    }

    private void ScrollToMatch()
    {
        if (_viewModel?.CurrentMatch is { } match)
        {
            // After layout, so a list that was just filled can scroll.
            Dispatcher.BeginInvoke(() => Lines.ScrollIntoView(match));
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
