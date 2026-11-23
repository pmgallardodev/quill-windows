using WpfControls = System.Windows.Controls;

namespace Quill.Windows.UI;

/// <summary>"Mis tareas". All behaviour lives in <see cref="MyTasksViewModel"/>.</summary>
internal sealed partial class MyTasksView : WpfControls.UserControl
{
    public MyTasksView()
    {
        InitializeComponent();
    }
}
