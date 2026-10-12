using System.Windows;

namespace PCOptimizer.App;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this);
    }

    public bool StartWithWindows => StartupCheck.IsChecked == true;

    void Start_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
