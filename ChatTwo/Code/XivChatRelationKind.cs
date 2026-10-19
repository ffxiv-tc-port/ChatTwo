namespace ChatTwo.Code;

/// <summary>
/// TC note: this enum is expected to come from Dalamud (a newer <c>Dalamud.Game.Chat.IChatMessage</c>
/// abstraction that categorizes a message sender/target's relationship to the local player), but it
/// does not exist in the Dalamud this fork actually builds against. Defined locally with the same
/// members (inferred from ChatSource.cs's bit-shift usage) so ChatCode/ChatSource keep compiling.
/// </summary>
/// <remarks>
/// <para>MessageManager subscribes to <c>IChatGui.ChatMessageUnhandled</c>, whose delegate at this API level
/// (<c>XivChatType type, int timestamp, SeString sender, SeString message</c>) carries no relation
/// information at all, so <c>MessageManager.ChatMessage</c> reports <see cref="LocalPlayer"/> for both
/// Source and Target, and the "chat source" filtering this enum backs degrades to a no-op. Deriving real
/// relation info would mean reimplementing party/alliance/hostility lookups - deliberately not done here.</para>
/// </remarks>
public enum XivChatRelationKind : byte
{
    LocalPlayer = 0,
    PartyMember = 1,
    AllianceMember = 2,
    OtherPlayer = 3,
    EngagedEnemy = 4,
    UnengagedEnemy = 5,
    FriendlyNpc = 6,
    PetOrCompanion = 7,
    PetOrCompanionParty = 8,
    PetOrCompanionAlliance = 9,
    PetOrCompanionOther = 10,
}
