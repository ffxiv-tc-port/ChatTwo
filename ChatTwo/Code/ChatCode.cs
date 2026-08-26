using Dalamud.Game.Text;

namespace ChatTwo.Code;

public class ChatCode
{
    public ChatType Type { get; }
    public XivChatRelationKind Source { get; }
    public XivChatRelationKind Target { get; }

    /// <summary>
    /// The log kind lives in the low 7 bits of the game's chat code; bits 7-10 and 11-14 carry the
    /// target's and the sender's relationship to the local player. Upstream builds against a Dalamud
    /// that hands those three out already separated, so it takes <paramref name="type"/> as a bare
    /// log kind. At API13 IChatGui.ChatMessageUnhandled still forwards the game's argument verbatim -
    /// Dalamud does no masking of its own - so on this branch <paramref name="type"/> arrives as the
    /// whole packed code (e.g. 4922, not 58) and has to be masked here. Without the mask every
    /// message whose sender or target is not the local player got a Type that matches no entry in a
    /// tab's SelectedChannels, so it was dropped on arrival - but the same rows read back out of the
    /// database were narrowed to a byte on the way in, which is why they reappeared after a refill.
    /// </summary>
    /// <remarks>
    /// Masking in the constructor rather than at the call site is deliberate: it also covers the
    /// database read path (see the byte overload below). Rows written before this fix hold the packed
    /// code in their ChatType column, and masking a value that was already truncated to a byte yields
    /// the same log kind as masking the full code, so those rows decode correctly on load without a
    /// migration. A value that is already a bare log kind is unaffected - every log kind is under 128.
    /// <para>
    /// Source and Target are deliberately left as the caller passed them. MessageManager still reports
    /// LocalPlayer for both (see XivChatRelationKind), so the per-source filtering stays the documented
    /// no-op it has always been on this branch; decoding the two nibbles here would start hiding
    /// messages for anyone whose SelectedChannels entries are narrower than the full mask, and would
    /// disagree with the zeroes already stored in every existing database row.
    /// </para>
    /// </remarks>
    public ChatCode(XivChatType type, XivChatRelationKind source, XivChatRelationKind target)
    {
        Type = (ChatType)((ushort)type & 0x7F);
        Source = source;
        Target = target;
    }

    public ChatCode(byte type, byte source, byte target)
        : this((XivChatType)type, (XivChatRelationKind)source, (XivChatRelationKind)target) {}

    public bool IsBattle()
    {
        switch (Type)
        {
            // Error isn't a battle message, but it can be just as spammy if you
            // use macros with unavailable actions.
            case ChatType.Error:
            case ChatType.Damage:
            case ChatType.Miss:
            case ChatType.Action:
            case ChatType.Item:
            case ChatType.Healing:
            case ChatType.GainBuff:
            case ChatType.LoseBuff:
            case ChatType.GainDebuff:
            case ChatType.LoseDebuff:
            case ChatType.BattleSystem:
                return true;
            default:
                return false;
        }
    }

    public bool IsCraftOrGather()
    {
        switch (Type)
        {
            case ChatType.Crafting:
            case ChatType.Gathering:
            case ChatType.GatheringSystem:
                return true;
            default:
                return false;
        }
    }

    public bool IsPlayerMessage()
    {
        switch (Type)
        {
            case ChatType.Say:
            case ChatType.Shout:
            case ChatType.TellOutgoing:
            case ChatType.TellIncoming:
            case ChatType.Party:
            case ChatType.CrossParty:
            case ChatType.Linkshell1:
            case ChatType.Linkshell2:
            case ChatType.Linkshell3:
            case ChatType.Linkshell4:
            case ChatType.Linkshell5:
            case ChatType.Linkshell6:
            case ChatType.Linkshell7:
            case ChatType.Linkshell8:
            case ChatType.CrossLinkshell1:
            case ChatType.CrossLinkshell2:
            case ChatType.CrossLinkshell3:
            case ChatType.CrossLinkshell4:
            case ChatType.CrossLinkshell5:
            case ChatType.CrossLinkshell6:
            case ChatType.CrossLinkshell7:
            case ChatType.CrossLinkshell8:
            case ChatType.FreeCompany:
            case ChatType.NoviceNetwork:
            case ChatType.Yell:
            case ChatType.ExtraChatLinkshell1:
            case ChatType.ExtraChatLinkshell2:
            case ChatType.ExtraChatLinkshell3:
            case ChatType.ExtraChatLinkshell4:
            case ChatType.ExtraChatLinkshell5:
            case ChatType.ExtraChatLinkshell6:
            case ChatType.ExtraChatLinkshell7:
            case ChatType.ExtraChatLinkshell8:
                return true;
            default:
                return false;
        }
    }

    public int ToSortCodeV2()
    {
        return (byte)Type << 16 | (byte)Source << 8 | (byte)Target;
    }

    public override bool Equals(object? obj)
    {
        if (obj == null)
            return false;

        if (obj is not ChatCode code)
            return false;

        return GetHashCode() == code.GetHashCode();
    }

    public override int GetHashCode()
    {
        return (byte)Type << 16 | (byte)Source << 8 | (byte)Target;
    }
}
