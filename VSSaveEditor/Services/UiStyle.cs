using Avalonia;
using Avalonia.Media;

namespace VSSaveEditor;

/// <summary>
///     Fonts and sizes that code sets on controls by hand. The source of the values is the App.axaml
///     resources (`MonoFontFamily`, `MonoFontSize`): XAML takes them statically, code reads them here once, so
///     "Consolas, monospace", 10/11 px and the edit column widths do not spread across files (P1-9).
/// </summary>
internal static class UiStyle
{
    public const double FieldLabelWidth = 200; // field label in the card editor
    public const double FieldBoxWidth = 220; // value box - the same for entity, BlockEntity and player
    public const double CardPadding = 6;
    public const double CardMarginY = 1;

    private static FontFamily? _mono;
    private static double? _monoSize;

    public static FontFamily Mono =>
        _mono ??= Res<FontFamily>("MonoFontFamily") ?? new FontFamily("Consolas, monospace");

    public static double MonoFontSize => _monoSize ??= ResDouble("MonoFontSize", 11);

    private static T? Res<T>(string key) where T : class
    {
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is T t
            ? t
            : null;
    }

    private static double ResDouble(string key, double fallback)
    {
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) &&
               v is double d && d > 0
            ? d
            : fallback;
    }
}