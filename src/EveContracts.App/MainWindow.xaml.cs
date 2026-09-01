using System.Windows;

namespace EveContracts.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        blazorView.Services = App.AppHost.Services;
    }
}
