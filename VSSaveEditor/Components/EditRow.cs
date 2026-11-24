using Avalonia.Controls;
using VSCore;

namespace VSSaveEditor;

/// <summary>
///     One editable row of a data window: the box and the text it had when the row was drawn. Only a box whose
///     text differs is pushed into the model — otherwise every Apply would "edit" every value it can show, and
///     the pending edit would rewrite blobs nobody touched. Both data windows drew this type of their own.
/// </summary>
internal sealed class EditRow
{
    public EditRow(ChunkField? field, TextBox box, string original)
    {
        Field = field;
        Box = box;
        Original = original;
    }

    public EditRow(PbNode node, TextBox box, string original)
    {
        Node = node;
        Box = box;
        Original = original;
    }

    public ChunkField? Field { get; }
    public PbNode? Node { get; }
    public TextBox Box { get; }

    /// <summary>The text the box started with — the reference a keystroke is compared against.</summary>
    public string Original { get; }

    public bool IsChanged => (Box.Text ?? "") != Original;
}
