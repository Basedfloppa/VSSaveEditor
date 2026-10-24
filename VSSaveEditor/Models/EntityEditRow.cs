using System.ComponentModel;
using VSCore;

namespace VSSaveEditor.Models;

/// <summary>
///     An edit row in the entity card: either a named field of the ECX blob, or one component of
///     the position (x/y/z - they sit in the blob without a name, see <see cref="EntityPositionCodec" />).
///     The text stays a string: the user edits it as is, parsing and validation happen on Apply.
/// </summary>
public sealed class EntityEditRow : INotifyPropertyChanged
{
    private string _text = "";

    public required string Label { get; init; }

    /// <summary>The value when the edit was opened: unchanged rows are not rebuilt -
    /// otherwise the round-trip would lose precision (and null would turn into an empty string).</summary>
    public string OriginalText { get; init; } = "";

    /// <summary>The blob field; null means a position row.</summary>
    public ChunkField? Field { get; init; }

    /// <summary>0=x, 1=y, 2=z when <see cref="Field" /> == null.</summary>
    public int PositionComponent { get; init; } = -1;

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
