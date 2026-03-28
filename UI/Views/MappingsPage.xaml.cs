using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using ArcanePlayConnect.UI.ViewModels;
using Windows.Graphics;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class MappingsPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    public MappingsPage()
    {
        InitializeComponent();
    }

    private void TestMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ActionMappingItem item)
            _ = ViewModel.TestMappingCommand.ExecuteAsync(item);
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ActionMappingItem item)
            ViewModel.RemoveActionMapping(item);
    }

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        var page = new AddActionPage();

        var window = new Window
        {
            Title = "Add Action Mapping",
            ExtendsContentIntoTitleBar = true,
            Content = page
        };

        var appWindow = window.AppWindow;
        appWindow.Resize(new SizeInt32(700, 620));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        page.LoadSavedCommands(ViewModel.SavedCommands);
        page.LoadCommandButtons(ViewModel.CommandButtons);
        page.SaveCommandToLibrary += cmd => ViewModel.AddSavedCommand(cmd);
        page.DeleteCommandFromLibrary += cmd => ViewModel.DeleteSavedCommand(cmd);

        page.Confirmed += () =>
        {
            if (page.Result != null)
            {
                // Resolve button name for display
                if (!string.IsNullOrEmpty(page.Result.TargetButtonId))
                {
                    var btn = ViewModel.CommandButtons
                        .FirstOrDefault(b => b.Id == page.Result.TargetButtonId);
                    page.Result.TargetButtonName = btn?.Name ?? string.Empty;
                }
                ViewModel.AddActionMapping(page.Result);
            }
            window.Close();
        };

        page.Cancelled += () => window.Close();
        window.Activate();
    }
}
