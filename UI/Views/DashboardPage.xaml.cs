using Microsoft.UI.Xaml.Controls;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class DashboardPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    public DashboardPage()
    {
        InitializeComponent();
    }
}
