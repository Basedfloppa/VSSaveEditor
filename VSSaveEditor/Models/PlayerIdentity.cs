namespace VSSaveEditor.Models;

/// <summary>
///     A player as the dialogs have to name one: the save keeps the name and the uid in different places
///     (the uid is in `playerdata`, the name inside the character), while a mod key carries only the uid.
/// </summary>
public sealed record PlayerIdentity(long Id, string Uid, string? Name)
{
    public string Label => Name is { Length: > 0 } name ? $"{name} ({Uid})" : Uid;
}
