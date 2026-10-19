namespace ChatTwo.Util;

/// <summary>
/// Holds the one item that is still waiting for a follow-up call from the game.
/// </summary>
/// <remarks>
/// Not thread safe; use it from one thread. Pure on purpose (no Dalamud, no game state) so the pairing
/// can be checked offline. <see cref="Take"/> empties the slot, so a follow-up is paired at most once.
/// </remarks>
public sealed class AwaitingSlot<T> where T : class
{
    private T? Item;

    public void Arm(T item) => Item = item;

    public void Disarm() => Item = null;

    public T? Take()
    {
        var item = Item;
        Item = null;
        return item;
    }
}
