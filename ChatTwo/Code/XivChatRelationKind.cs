namespace ChatTwo.Code;

/// <summary>
/// TC note: this enum is expected to come from Dalamud (a newer <c>Dalamud.Game.Chat.IChatMessage</c>
/// abstraction that categorizes a message sender/target's relationship to the local player), but it
/// doesn't exist in the verified true-API13 Dalamud reference build used to port this repo forward
/// (checked directly in source - grep for "XivChatRelationKind"/"interface IChatMessage" under
/// D:\Dalamud\Dalamud came up empty), nor in TC's bundled Dalamud, nor even in the newest global
/// XIVLauncher dev build checked on this machine - it's a very recent/bleeding-edge Dalamud addition
/// with no equivalent available anywhere yet. Defined locally with the same members (inferred from
/// ChatSource.cs's bit-shift usage) so ChatCode/ChatSource keep compiling; MessageManager.ChatMessage
/// (the old-API 5-arg ChatMessage hook, which has no relation info at all) always reports
/// <see cref="LocalPlayer"/> for both Source and Target - the "chat source" filtering feature this
/// backs degrades to a no-op rather than crashing, since there's no data to derive real relation info
/// from without reimplementing party/alliance/hostility lookups ourselves.
/// </summary>
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
