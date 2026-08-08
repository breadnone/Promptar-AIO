using System.Windows;
using System.Windows.Media;

namespace MyAiGen;

internal static class AppTheme
{
    internal static T F<T>(T o) where T : Freezable { o.Freeze(); return o; }

    public static readonly Brush Bg = F(new SolidColorBrush(Color.FromRgb(20, 20, 24)));
    public static readonly Brush Surface = F(new SolidColorBrush(Color.FromRgb(30, 30, 36)));
    public static readonly Brush SurfaceAlt = F(new SolidColorBrush(Color.FromRgb(38, 38, 44)));
    public static readonly Brush SurfaceHover = F(new SolidColorBrush(Color.FromRgb(48, 48, 55)));
    public static readonly Brush CardBg = F(new SolidColorBrush(Color.FromRgb(26, 26, 32)));
    public static readonly Brush InputBg = F(new SolidColorBrush(Color.FromRgb(18, 18, 22)));
    public static readonly Brush InputBgAlt = F(new SolidColorBrush(Color.FromRgb(22, 22, 28)));
    public static readonly Brush ButtonBg = F(new SolidColorBrush(Color.FromRgb(30, 30, 38)));
    public static readonly Brush Accent = F(new SolidColorBrush(Color.FromRgb(100, 140, 255)));
    public static readonly Brush Highlight = F(new SolidColorBrush(Color.FromRgb(60, 80, 160)));
    public static readonly Brush Error = F(new SolidColorBrush(Color.FromRgb(180, 45, 45)));
    public static readonly Brush BorderAlt = F(new SolidColorBrush(Color.FromRgb(60, 60, 70)));
    public static readonly Brush BorderDim = F(new SolidColorBrush(Color.FromRgb(50, 50, 58)));
    public static readonly Brush BorderTertiary = F(new SolidColorBrush(Color.FromRgb(50, 50, 55)));
    public static readonly Brush ThumbBg = F(new SolidColorBrush(Color.FromRgb(55, 55, 65)));
    public static readonly Brush ThumbHover = F(new SolidColorBrush(Color.FromRgb(75, 75, 90)));
    public static readonly Brush ThumbPressed = F(new SolidColorBrush(Color.FromRgb(95, 95, 115)));

    public static readonly Brush DimBorder = F(new SolidColorBrush(Color.FromArgb(60, 100, 100, 100)));
    public static readonly Brush BubbleBorder = F(new SolidColorBrush(Color.FromArgb(180, 100, 200, 255)));
}
