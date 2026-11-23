using System.Windows;
using WpfControls = System.Windows.Controls;
using WpfDocuments = System.Windows.Documents;

namespace Quill.Windows.UI;

/// <summary>
/// Attached property that fills a TextBlock with <see cref="HighlightRun"/>s, painting the
/// matches with <c>Quill.Brush.WarningSubtle</c>:
/// <c>&lt;TextBlock local:HighlightedText.Runs="{Binding Runs}" /&gt;</c>.
/// </summary>
internal static class HighlightedText
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs",
        typeof(IReadOnlyList<HighlightRun>),
        typeof(HighlightedText),
        new PropertyMetadata(null, OnRunsChanged));

    public static IReadOnlyList<HighlightRun>? GetRuns(DependencyObject element) =>
        (IReadOnlyList<HighlightRun>?)element.GetValue(RunsProperty);

    public static void SetRuns(DependencyObject element, IReadOnlyList<HighlightRun>? value) =>
        element.SetValue(RunsProperty, value);

    private static void OnRunsChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not WpfControls.TextBlock text)
        {
            return;
        }

        text.Inlines.Clear();
        if (args.NewValue is not IReadOnlyList<HighlightRun> runs)
        {
            return;
        }

        foreach (var run in runs)
        {
            var inline = new WpfDocuments.Run(run.Text);
            if (run.IsMatch)
            {
                inline.SetResourceReference(WpfDocuments.TextElement.BackgroundProperty, "Quill.Brush.WarningSubtle");
                inline.FontWeight = FontWeights.SemiBold;
            }

            text.Inlines.Add(inline);
        }
    }
}
