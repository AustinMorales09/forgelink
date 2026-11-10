using System.Windows;
using System.Windows.Input;
using ForgeLink.Desktop.ViewModels;

namespace ForgeLink.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(StationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => ScanBox.Focus();

        // Scanners are keyboards: whatever the operator last touched, the next scan must land in the scan box.
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox) return;
            if (e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9) ScanBox.Focus();
        };
    }
}
