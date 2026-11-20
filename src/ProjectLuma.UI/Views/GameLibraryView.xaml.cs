using System.Windows;
using System.Windows.Controls;
using ProjectLuma.UI.ViewModels;

namespace ProjectLuma.UI.Views;

public partial class GameLibraryView : UserControl
{
    public GameLibraryView() => InitializeComponent();

    private void OnLibraryGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.NewSize.Width > 0)
            vm.UpdateGridViewportWidth(e.NewSize.Width);
    }
}
