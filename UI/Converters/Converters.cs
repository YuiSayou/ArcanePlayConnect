using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ArcanePlayConnect.Core.Models;

namespace ArcanePlayConnect.UI.Converters;

public class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is LogLevel level)
        {
            return level switch
            {
                LogLevel.Info => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 255)),       // Neon blue
                LogLevel.Warning => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 200, 0)),    // Yellow
                LogLevel.Error => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 50, 80)),      // Neon red/pink
                LogLevel.Event => new SolidColorBrush(ColorHelper.FromArgb(255, 180, 0, 255)),      // Neon purple
                _ => new SolidColorBrush(Colors.White)
            };
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class LogLevelToTagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is LogLevel level)
        {
            return level switch
            {
                LogLevel.Info => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Event => "EVENT",
                _ => "LOG"
            };
        }
        return "LOG";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class LogCategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is LogCategory cat)
        {
            return cat switch
            {
                LogCategory.Chat    => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 255)),       // Neon blue
                LogCategory.Follow  => new SolidColorBrush(ColorHelper.FromArgb(255, 180, 0, 255)),    // Neon purple
                LogCategory.Gift    => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 50, 120)),      // Neon red/pink
                LogCategory.Like    => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 80, 80)),   // red/coral
                LogCategory.Join    => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 230, 180)),   // Teal/cyan
                LogCategory.Share   => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 149, 0)),   // Orange
                LogCategory.Subscribe => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 215, 0)), // Gold
                LogCategory.Webhook => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 255, 136)),      // Neon green
                LogCategory.System  => new SolidColorBrush(ColorHelper.FromArgb(255, 136, 136, 170)),      // Grey
                _ => new SolidColorBrush(Colors.White)
            };
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class LogCategoryToTagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is LogCategory cat)
        {
            return cat switch
            {
                LogCategory.Chat    => "CHAT",
                LogCategory.Follow  => "FOLLOW",
                LogCategory.Gift    => "GIFT",
                LogCategory.Like    => "LIKE",
                LogCategory.Join    => "JOIN",
                LogCategory.Share   => "SHARE",
                LogCategory.Subscribe => "SUB",
                LogCategory.Webhook => "HOOK",
                LogCategory.System  => "SYS",
                _ => "LOG"
            };
        }
        return "LOG";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b && b)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(255, 0, 255, 136)); // Neon green
        }
        return new SolidColorBrush(ColorHelper.FromArgb(255, 255, 50, 80));     // Neon red
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool bv && bv;
        if (parameter is string s && s == "Invert")
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return false;
    }
}

public class ActionTypeToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is ActionTriggerType t)
        {
            return t switch
            {
                ActionTriggerType.Gift   => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 50, 120)),   // NeonPink
                ActionTriggerType.Follow => new SolidColorBrush(ColorHelper.FromArgb(255, 180, 0, 255)), // NeonPurple
                ActionTriggerType.Chat   => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 200, 255)),   // NeonBlue
                ActionTriggerType.Like   => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 80, 80)),    // red/coral
                ActionTriggerType.Join   => new SolidColorBrush(ColorHelper.FromArgb(255, 0, 230, 180)),    // Teal/cyan
                ActionTriggerType.Share  => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 149, 0)),    // Orange
                ActionTriggerType.Subscribe => new SolidColorBrush(ColorHelper.FromArgb(255, 255, 215, 0)), // Gold
                _ => new SolidColorBrush(Colors.White)
            };
        }
        return new SolidColorBrush(Colors.White);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
