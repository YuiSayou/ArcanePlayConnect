using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using ArcanePlayConnect.UI.ViewModels;

namespace ArcanePlayConnect.UI.Views;

public sealed partial class DashboardPage : Page
{
    public MainViewModel ViewModel { get; } = MainViewModel.Instance;

    private int _displayedViewerCount;
    private long _displayedTotalLikes;
    private DispatcherTimer? _viewerTimer;
    private DispatcherTimer? _likesTimer;
    private int _targetViewerCount;
    private long _targetTotalLikes;

    public DashboardPage()
    {
        InitializeComponent();

        _displayedViewerCount = ViewModel.ViewerCount;
        _displayedTotalLikes = ViewModel.TotalLikes;
        ViewerCountText.Text = FormatNumber(_displayedViewerCount);
        LikesCountText.Text = FormatNumber(_displayedTotalLikes);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ViewerCount))
        {
            _targetViewerCount = ViewModel.ViewerCount;
            StartCounterAnimation(ref _viewerTimer, () => _displayedViewerCount, v => _displayedViewerCount = v,
                () => _targetViewerCount, ViewerCountText);
        }
        else if (e.PropertyName == nameof(MainViewModel.TotalLikes))
        {
            _targetTotalLikes = ViewModel.TotalLikes;
            StartLongCounterAnimation(ref _likesTimer, () => _displayedTotalLikes, v => _displayedTotalLikes = v,
                () => _targetTotalLikes, LikesCountText);
        }
    }

    private void StartCounterAnimation(ref DispatcherTimer? timer, Func<int> getCurrent, Action<int> setCurrent,
        Func<int> getTarget, TextBlock textBlock)
    {
        timer?.Stop();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            var current = getCurrent();
            var target = getTarget();
            if (current == target)
            {
                t.Stop();
                return;
            }
            var diff = target - current;
            var step = Math.Max(1, Math.Abs(diff) / 15);
            setCurrent(diff > 0 ? Math.Min(current + step, target) : Math.Max(current - step, target));
            textBlock.Text = FormatNumber(getCurrent());
        };
        timer = t;
        t.Start();
    }

    private void StartLongCounterAnimation(ref DispatcherTimer? timer, Func<long> getCurrent, Action<long> setCurrent,
        Func<long> getTarget, TextBlock textBlock)
    {
        timer?.Stop();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            var current = getCurrent();
            var target = getTarget();
            if (current == target)
            {
                t.Stop();
                return;
            }
            var diff = target - current;
            var step = Math.Max(1L, Math.Abs(diff) / 15);
            setCurrent(diff > 0 ? Math.Min(current + step, target) : Math.Max(current - step, target));
            textBlock.Text = FormatNumber(getCurrent());
        };
        timer = t;
        t.Start();
    }

    private static string FormatNumber(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000.0:F1}M";
        if (value >= 1_000) return $"{value / 1_000.0:F1}K";
        return value.ToString("N0");
    }

    private static string FormatNumber(int value) => FormatNumber((long)value);
}
