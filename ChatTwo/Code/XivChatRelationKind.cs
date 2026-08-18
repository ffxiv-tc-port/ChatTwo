namespace ChatTwo.Code;

/// <summary>
/// TC note: this enum is expected to come from Dalamud (a newer <c>Dalamud.Game.Chat.IChatMessage</c>
/// abstraction that categorizes a message sender/target's relationship to the local player), but it
/// does not exist in the Dalamud this fork actually builds against. Defined locally with the same
/// members (inferred from ChatSource.cs's bit-shift usage) so ChatCode/ChatSource keep compiling.
/// </summary>
/// <remarks>
/// <para>Re-verified 2026-08-19 against <c>D:/ffxiv-tc-port/Dalamud</c> (the pinned tree this repo
/// is built with): zero hits for <c>XivChatRelationKind</c> and for <c>interface IChatMessage</c>
/// across the whole repo, and zero occurrences of either name in the built
/// <c>bin/Release/Dalamud.dll</c>. The query was calibrated first on <c>XivChatType</c>, which does
/// hit in both source and the DLL - so the zeroes are real absence, not a broken search.</para>
///
/// <para>Two claims in the original version of this note were wrong and have been corrected:</para>
/// <list type="number">
///   <item>It cited a grep under <c>D:\Dalamud\Dalamud</c>. <b>That path does not exist on this
///   machine</b>, and it is not the tree this repo builds against either - the pinned one is
///   <c>D:/ffxiv-tc-port/Dalamud</c>. The conclusion happened to be right, but the evidence behind
///   it was not. (The further claims about "TC's bundled Dalamud" and "the newest global XIVLauncher
///   dev build" are likewise unverifiable from here and have been dropped rather than repeated.)</item>
///   <item>It called the fallback "the old-API <b>5-arg</b> ChatMessage hook". It is not 5-arg and it
///   is not the <c>ChatMessage</c> event. MessageManager subscribes to
///   <c>IChatGui.ChatMessageUnhandled</c> (MessageManager.cs:97), whose delegate at this API level is
///   <c>OnMessageUnhandledDelegate(XivChatType type, int timestamp, SeString sender, SeString message)</c>
///   - <b>4 args</b>. The 5-arg shape is <c>OnMessageDelegate</c> behind the separate
///   <c>ChatMessage</c> event (it adds <c>ref bool isHandled</c>), which this plugin does not use.</item>
/// </list>
///
/// <para>What still holds: that delegate carries no relation information at all, so
/// <c>MessageManager.ChatMessage</c> reports <see cref="LocalPlayer"/> for both Source and Target,
/// and the "chat source" filtering feature this enum backs degrades to a no-op rather than crashing.
/// Deriving real relation info would mean reimplementing party/alliance/hostility lookups ourselves.
/// <b>Not revived here</b> - that is a feature decision, not a note correction.</para>
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
