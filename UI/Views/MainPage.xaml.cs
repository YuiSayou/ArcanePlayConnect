using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.UI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private int _previousLogCount;

    public MainPage()
    {
        InitializeComponent();

        _previousLogCount = ViewModel.FilteredLogs.Count;

        // Initialize the test command box with the ViewModel value
        TestCommandBox.Text = ViewModel.TestCommandText;

        ViewModel.FilteredLogs.CollectionChanged += (s, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                _previousLogCount = ViewModel.FilteredLogs.Count;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModel.FilteredLogs.Count > 0)
                    {
                        LogListView.ScrollIntoView(ViewModel.FilteredLogs[^1]);
                    }
                });
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _previousLogCount = 0;
            }
        };

        ViewModel.AddActionRequested += OnAddActionRequested;
    }

    private void OnAddActionRequested()
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
                ViewModel.AddActionMapping(page.Result);
            window.Close();
        };

        page.Cancelled += () => window.Close();

        window.Activate();
    }

    private void LogListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
            return;

        var container = args.ItemContainer;
        var itemIndex = args.ItemIndex;

        // Only animate items that were just added (at or beyond the previous count)
        if (itemIndex >= _previousLogCount - 1 && _previousLogCount > 0)
        {
            container.Opacity = 0;

            var fadeIn = new Storyboard();
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, container);
            Storyboard.SetTargetProperty(animation, "Opacity");
            fadeIn.Children.Add(animation);
            fadeIn.Begin();
        }
    }

    private void CopyLogEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem &&
            menuItem.DataContext is LogEntry entry)
        {
            ViewModel.CopySelectedLogCommand.Execute(entry);
        }
    }

    private void RemoveMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ActionMappingItem item)
            ViewModel.RemoveActionMapping(item);
    }

    private void TestMapping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ActionMappingItem item)
            _ = ViewModel.TestMappingCommand.ExecuteAsync(item);
    }

    private void TestCommandBox_TextChanged(object sender, string text)
    {
        ViewModel.TestCommandText = text;
    }

    private void CloseProfileEditor_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsProfileEditorOpen = false;
    }
}
