using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class ShellPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private Button? _activeNav;

    public ShellPage()
    {
        InitializeComponent();
        NavigateTo("Dashboard");
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        // Reset previous
        if (_activeNav != null)
            _activeNav.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var targetBtn = tag switch
        {
            "Dashboard" => NavDashboard,
            "EventLog"  => NavEventLog,
            "Mappings"  => NavMappings,
            "Buttons"   => NavButtons,
            "Arena"     => NavArena,
            "Profiles"  => NavProfiles,
            _ => NavDashboard
        };

        targetBtn.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(30, 0, 200, 255));
        _activeNav = targetBtn;

        var pageType = tag switch
        {
            "Dashboard" => typeof(DashboardPage),
            "EventLog"  => typeof(EventLogPage),
            "Mappings"  => typeof(MappingsPage),
            "Buttons"   => typeof(CommandButtonsPage),
            "Arena"     => typeof(CreatureArenaPage),
            "Profiles"  => typeof(ProfilesPage),
            _ => typeof(DashboardPage)
        };

        ContentFrame.Navigate(pageType);
    }
}
