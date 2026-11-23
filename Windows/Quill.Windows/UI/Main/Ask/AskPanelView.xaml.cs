using WpfControls = System.Windows.Controls;

namespace Quill.Windows.UI;

/// <summary>Question box, streaming answer and citation chips. All behaviour lives in <see cref="AskViewModel"/>.</summary>
internal sealed partial class AskPanelView : WpfControls.UserControl
{
    public AskPanelView()
    {
        InitializeComponent();
    }
}
