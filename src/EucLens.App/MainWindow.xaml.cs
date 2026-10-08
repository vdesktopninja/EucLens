using System.Windows;
namespace EucLens.App;
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Initial dimensions must also fit the smallest scaled desktop, before any resize.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
    }
}
