using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NexusGuard.Core;

/// <summary>true → Visible, false → Collapsed.</summary>
public sealed class BoolToVis : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility.Visible;
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class BoolToVisInverse : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is not Visibility.Visible;
}

public sealed class NotBool : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Texto vazio ou nulo → Collapsed.</summary>
public sealed class TextToVis : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Coleccao vazia ou contagem zero → Visible (para mensagens de "nada aqui").</summary>
public sealed class EmptyToVis : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var empty = value switch
        {
            null => true,
            int i => i == 0,
            System.Collections.ICollection col => col.Count == 0,
            _ => false
        };

        return empty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class CountToVis : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var any = value switch
        {
            int i => i > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => value is not null
        };

        return any ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Percentagem → cor: verde ate 60, ambar ate 85, vermelho acima.</summary>
public sealed class LoadToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var v = value switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => 0d
        };

        var color = v switch
        {
            >= 85 => Color.FromRgb(0xF8, 0x71, 0x71),
            >= 60 => Color.FromRgb(0xFB, 0xBF, 0x24),
            _ => Color.FromRgb(0x34, 0xD3, 0x99)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>true → verde, false → vermelho (estado de protecoes).</summary>
public sealed class StateToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
            : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StateToText : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? "Ativo" : "Inativo";

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Converte uma porcentagem (0-100) na largura proporcional de uma barra.</summary>
public sealed class PercentToWidth : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var pct = value switch
        {
            double d => d,
            int i => i,
            _ => 0d
        };

        var max = p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 100d;
        return Math.Clamp(pct, 0, 100) / 100d * max;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Nível de registro → cor do rótulo.</summary>
public sealed class LogLevelToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var color = value switch
        {
            LogLevel.Ok => Color.FromRgb(0x34, 0xD3, 0x99),
            LogLevel.Warn => Color.FromRgb(0xFB, 0xBF, 0x24),
            LogLevel.Error => Color.FromRgb(0xF8, 0x71, 0x71),
            _ => Color.FromRgb(0x6A, 0x74, 0x88)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Nível de risco de um alvo de limpeza -> cor da etiqueta.</summary>
public sealed class RiskToBrush : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var color = value switch
        {
            Modules.CleanRisk.Moderate => Color.FromRgb(0xFB, 0xBF, 0x24),
            Modules.CleanRisk.Advanced => Color.FromRgb(0xF8, 0x71, 0x71),
            _ => Color.FromRgb(0x34, 0xD3, 0x99)
        };

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
