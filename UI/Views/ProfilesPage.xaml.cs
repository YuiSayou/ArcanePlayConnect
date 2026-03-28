using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class ProfilesPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    public ProfilesPage()
    {
        InitializeComponent();
    }

    private void CloseEditor_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsProfileEditorOpen = false;
    }
}
