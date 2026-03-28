using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using ArcanePlayConnect.Core.Models;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class EventLogPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private int _previousLogCount;

    public EventLogPage()
    {
        InitializeComponent();

        _previousLogCount = ViewModel.FilteredLogs.Count;

        ViewModel.FilteredLogs.CollectionChanged += (s, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                _previousLogCount = ViewModel.FilteredLogs.Count;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModel.FilteredLogs.Count > 0)
                        LogListView.ScrollIntoView(ViewModel.FilteredLogs[^1]);
                });
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                _previousLogCount = 0;
            }
        };
    }

    private void LogListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;

        var container = args.ItemContainer;
        if (args.ItemIndex >= _previousLogCount - 1 && _previousLogCount > 0)
        {
            container.Opacity = 0;
            var fadeIn = new Storyboard();
            var anim = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(250)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, container);
            Storyboard.SetTargetProperty(anim, "Opacity");
            fadeIn.Children.Add(anim);
            fadeIn.Begin();
        }
    }

    private void CopyLogEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem menuItem && menuItem.DataContext is LogEntry entry)
            ViewModel.CopySelectedLogCommand.Execute(entry);
    }
}
