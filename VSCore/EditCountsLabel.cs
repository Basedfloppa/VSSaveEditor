namespace VSCore;

/// <summary>
///     The pending-edit label, in the words the windows use ("1 value(s) changed, 1 item(s) deleted, …").
///     Two counts records drew it: a count of zero is left out, and no count at all is "nothing to apply".
/// </summary>
public static class EditCountsLabel
{
    public static string Of(params (int Count, string What)[] parts)
    {
        var said = parts.Where(p => p.Count > 0).Select(p => $"{p.Count} {p.What}").ToList();
        return said.Count == 0 ? "nothing to apply" : string.Join(", ", said);
    }

    /// <summary>
    ///     The live line of a data window: values still sitting in text boxes ("to write", not yet in the model)
    ///     plus everything the model already knows. One source per kind, so a typed value is never counted twice:
    ///     a box is only pushed into the model on apply/redraw, after which it stops being "typed".
    /// </summary>
    public static (string Text, bool Any) Live(int typedValues, params (int Count, string What)[] modelParts)
    {
        var parts = typedValues > 0 ? new (int Count, string What)[] { (typedValues, "value(s) to write") } : [];
        var all = parts.Concat(modelParts).ToArray();
        return (Of(all), all.Any(p => p.Count > 0));
    }
}
