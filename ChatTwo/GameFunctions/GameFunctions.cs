using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Memory;
using Dalamud.Utility;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace ChatTwo.GameFunctions;

public unsafe class GameFunctions : IDisposable
{
    #region Hooks
    // TC note: this signature resolves to the wrong address on TC. It doesn't
    // just get called with an occasional null placeholderText (which a null
    // check alone can't fully guard against - the crashes also arrived as a
    // small near-null garbage pointer, which C# `== null` doesn't catch) - the
    // detour was observed firing from completely unrelated native call sites
    // (OnAddonSetup, OnAddonRefresh, OnAddonFinalize, even raw Framework
    // update), which only happens when a hook is attached to the wrong
    // function entirely. Disabled below; see ListCommand() for the
    // placeholder-free replacement.
    // [Signature("E8 ?? ?? ?? ?? 48 85 C0 0F 84 ?? ?? ?? ?? 48 8B D0 49 8D 4F", DetourName = nameof(ResolveTextCommandPlaceholderDetour))]
    // private Hook<ResolveTextCommandPlaceholderDelegate>? ResolveTextCommandPlaceholderHook = null!;
    // private delegate nint ResolveTextCommandPlaceholderDelegate(nint a1, byte* placeholderText, byte a3, byte a4);
    #endregion

    private Plugin Plugin { get; }
    public KeybindManager KeybindManager { get; }
    public Chat Chat { get; }

    public GameFunctions(Plugin plugin)
    {
        Plugin = plugin;
        KeybindManager = new KeybindManager(plugin);
        Chat = new Chat(Plugin);

        Plugin.GameInteropProvider.InitializeFromAttributes(this);
    }

    public void Dispose()
    {
        Chat.Dispose();
        KeybindManager.Dispose();
    }

    public void SendFriendRequest(string name, ushort world)
    {
        ListCommand(name, world, "friendlist");
    }

    public void AddToBlacklist(string name, ushort world)
    {
        ListCommand(name, world, "blist");
    }

    // Guards throughout this file use Chat.ResolveOrNull, which covers BOTH failure modes an
    // Instance() can have. The [Agent] / [InfoProxy] generator templates contain no throw
    // (`agentModule == null ? null : ...`), which is why an earlier pass classified them as
    // return-null-only, but the chain underneath them holds three throwing stubs:
    // Framework.Instance() ([StaticAddress(isPointer: true)]), framework->GetUIModule()
    // ([MemberFunction]) and GetAgentByInternalId / GetInfoProxyById ([MemberFunction]). The full
    // derivation lives next to ResolveOrNull in Chat.cs.
    //
    // The null half is the one that matters most here: dereferencing a null Instance() is an
    // AccessViolationException, a corrupted-state exception that try/catch cannot intercept in .NET
    // Core, so it takes the game down no matter which thread or path reached it. There are no live
    // detours in this file (the only [Signature] hook is commented out at the top), so an escaping
    // throw would merely be caught by Dalamud - except in Dispose and in the per-frame path, both
    // called out where they occur.
    public void AddToMuteList(ulong accountId, ulong contentId, string name, short worldId)
    {
        var agent = Chat.ResolveOrNull<AgentMutelist>(&AgentMutelist.Instance, "GameFunctions/mutelist", "Could not resolve AgentMutelist; the player was not added to the mute list");
        if (agent == null)
            return;

        agent->Add(accountId, contentId, name, worldId);
    }

    public void AddToTermsList(SeString content)
    {
        var agent = Chat.ResolveOrNull<AgentTermFilter>(&AgentTermFilter.Instance, "GameFunctions/termFilter", "Could not resolve AgentTermFilter; the term filter window was not opened");
        if (agent == null)
            return;

        agent->OpenNewFilterWindow(content.EncodeWithNullTerminator());
    }

    private void ListCommand(string name, ushort world, string commandName)
    {
        var worldRow = Sheets.WorldSheet.GetRow(world);
        ChatBox.SendMessage($"/{commandName} add {name}@{worldRow.Name.ToString()}");
    }

    // This one gets the full treatment (try around the call as well as around the resolve) because
    // it has two callers where a merely-caught exception is still damaging:
    //   - Plugin.Dispose() -> SetChatInteractable(true). A throw there aborts the rest of Dispose,
    //     leaving the hooks in Chat/MessageManager attached to a plugin that is being unloaded.
    //     Teardown is also exactly when RaptureAtkModule.Instance() starts returning null.
    //   - Plugin.FrameworkUpdate() -> IsAddonInteractable, five addons every frame while HideChat is
    //     on. The throttled Error keeps a permanent signature break from flooding the log.
    // RaptureAtkModule.Instance() is the chained kind (`uiModule == null ? null :
    // uiModule->GetRaptureAtkModule()`) and GetAddonByName is [MemberFunction], so both halves are
    // live. Degradation: reported as "no such addon", which every caller already handles - the
    // vanilla chat log is simply left in whatever visibility state it currently has.
    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var atkModule = Chat.ResolveOrNull<RaptureAtkModule>(&RaptureAtkModule.Instance, "GameFunctions/atkModule", "Could not resolve RaptureAtkModule; addon lookups are unavailable");
        if (atkModule == null)
            return null;

        AtkUnitBase* addon;
        try
        {
            addon = atkModule->RaptureAtkUnitManager.GetAddonByName(name);
        }
        catch (Exception ex)
        {
            Chat.LogErrorThrottled("GameFunctions/getAddonByName", ex, $"Could not look up the addon '{name}'");
            return null;
        }

        return addon != null && addon->IsReady ? (T*)addon : null;
    }

    public static void SetAddonInteractable(string name, bool interactable)
    {
        var addon = GetAddon<AtkUnitBase>(name);
        if (addon == null)
            return;
        addon->IsVisible = interactable;
    }

    public static void SetChatInteractable(bool interactable)
    {
        for (var i = 0; i < 4; i++)
            SetAddonInteractable($"ChatLogPanel_{i}", interactable);

        SetAddonInteractable("ChatLog", interactable);
    }

    public static bool IsAddonInteractable(string name)
    {
        var addon = GetAddon<AtkUnitBase>(name);
        return addon != null && addon->IsVisible;
    }

    public static void OpenItemTooltip(uint id, ItemKind itemKind)
    {
        // Upstream's "atkStage ain't gonna be null or we have bigger problems" is the assumption this
        // pass exists to remove. AtkStage.Instance() is [StaticAddress(isPointer: true)], which the
        // generator renders as `if (ppInstance is null) Throw; return *ppInstance;` - so it throws
        // when the signature does not resolve (a live risk on TC, where signatures are the thing that
        // breaks) AND returns null whenever the game has not constructed the singleton, which is true
        // on the loading screen and during teardown. Both are ordinary states, not "bigger problems",
        // and the deref at the bottom of this method (atkStage->TooltipManager) is an
        // AccessViolationException that no try/catch can intercept.
        var atkStage = Chat.ResolveOrNull<AtkStage>(&AtkStage.Instance, "GameFunctions/atkStage", "Could not resolve AtkStage; the item tooltip was not opened");
        var agent = Chat.ResolveOrNull<AgentItemDetail>(&AgentItemDetail.Instance, "GameFunctions/itemDetailOpen", "Could not resolve AgentItemDetail; the item tooltip was not opened");
        var addon = GetAddon<AtkUnitBase>("ItemDetail");

        // Degradation: no tooltip is shown for the hovered item link, which is the same outcome the
        // existing agent == null / addon == null early return already produces.
        if (atkStage == null || agent == null || addon == null)
            return;

        agent->DetailKind = itemKind == ItemKind.EventItem ? DetailKind.KeyItem : DetailKind.Item;
        agent->TypeOrId = id;
        agent->Index = 0;
        agent->Flag1 &= 0xEF;
        agent->ItemId = id;
        // agent->Flag2 = 1;
        // agent->Flag3 = 0;
        // TODO: Revert whenever CS is merged
        *(byte*)((nint)agent + 0x21A) = 1;
        *(byte*)((nint)agent + 0x21E) = 0;

        // This just probably needs to be set
        agent->AddonId = addon->Id;

        // Skips early return
        // TC note: upstream's own source (and its "TODO: Revert whenever CS is merged" comment
        // above) called this field `TooltipType`, but the verified true-API13 FFXIVClientStructs
        // pin (D:\Dalamud) has since renamed it to `Flag1` (same FieldOffset 0x14C, same "Allows
        // AddonItemDetail to be shown with Flag1 |= 2" semantics per its own doc comment) - not a
        // TC-specific gap, just a field rename that landed in this CS version.
        atkStage->TooltipManager.Flag1 |= 2;
        addon->Show(false, 15);
    }

    public static void CloseItemTooltip()
    {
        // hide addon first to prevent the "addon close" sound
        var addon = GetAddon<AtkUnitBase>("ItemDetail");
        if (addon != null)
            addon->Hide(true, false, 0);

        // Already null-checked upstream; the resolve is what was unguarded. Same [Agent] getter as
        // OpenItemTooltip above.
        var agent = Chat.ResolveOrNull<AgentItemDetail>(&AgentItemDetail.Instance, "GameFunctions/itemDetailClose", "Could not resolve AgentItemDetail; the item tooltip was not dismissed");
        if (agent != null)
        {
            var eventData = stackalloc AtkValue[1];
            var atkValues = stackalloc AtkValue[1];
            atkValues->Type = ValueType.Int;
            atkValues->Int = -1;
            agent->ReceiveEvent(eventData, atkValues, 1, 1);
        }
    }

    public static void OpenPartyFinder()
    {
        // this whole method: 6.05: 84433A (FF 97 ?? ?? ?? ?? 41 B4 01)
        // lfg was dereferenced with no check at all (lfg->IsAgentActive()), and so was the
        // RaptureAtkModule below it plus the vtable read off it. IsAgentActive is [VirtualFunction],
        // which never throws but renders as `VirtualTable->IsAgentActive(this)` - so a null receiver
        // faults on the vtable load rather than raising anything catchable.
        var lfg = Chat.ResolveOrNull<AgentLookingForGroup>(&AgentLookingForGroup.Instance, "GameFunctions/lfg", "Could not resolve AgentLookingForGroup; the party finder was not toggled");
        if (lfg == null)
            return;

        if (lfg->IsAgentActive())
        {
            var addonId = lfg->GetAddonId();
            var atkModule = Chat.ResolveOrNull<RaptureAtkModule>(&RaptureAtkModule.Instance, "GameFunctions/lfgAtkModule", "Could not resolve RaptureAtkModule; the party finder window was not brought to front");
            if (atkModule == null)
                return;

            // Hand-rolled vtable dispatch: index 27 is upstream's, kept as-is because nothing offline
            // can confirm or refute it on TC. The pointer checks below are what make a wrong index
            // the only remaining risk instead of one of three.
            var atkModuleVtbl = (void**) atkModule->AtkModule.VirtualTable;
            if (atkModuleVtbl == null || atkModuleVtbl[27] == null)
                return;

            var vf27 = (delegate* unmanaged<RaptureAtkModule*, ulong, ulong, byte>) atkModuleVtbl[27];
            vf27(atkModule, addonId, 1);
        }
        else
        {
            // 6.05: 8443DD
            if (*(uint*) ((nint) lfg + 0x2C20) > 0)
                lfg->Hide();
            else
                lfg->Show();
        }
    }

    // PlayerState.Instance() is the one shape in this file with only ONE failure mode: a plain
    // [StaticAddress] (no isPointer), rendered as `if (pInstance is null) Throw; return pInstance;`,
    // so it can throw but can never return null. The null check below is still not dead code -
    // ResolveOrNull returns null on its catch path, which is precisely how the throw is converted
    // into something this method can act on.
    //
    // Degradation: reporting "not a mentor" hides the novice-network entries in the context menu and
    // the channel switcher. That is the fail-closed direction: the alternative would be offering an
    // action the game would then reject.
    public static bool IsMentor()
    {
        var state = Chat.ResolveOrNull<PlayerState>(&PlayerState.Instance, "GameFunctions/playerState", "Could not resolve PlayerState; treating the player as not a mentor");
        if (state == null)
            return false;

        return state->IsMentor();
    }

    // Degradation: an empty friend list. Both callers only ask `.Any(...)` of it, so the effect is
    // that a friend is not recognised as one - it suppresses a "reply to friend" shortcut rather
    // than granting anything.
    public static InfoProxyCommonList.CharacterData[] GetFriends()
    {
        var proxy = Chat.ResolveOrNull<InfoProxyFriendList>(&InfoProxyFriendList.Instance, "GameFunctions/friendList", "Could not resolve InfoProxyFriendList; treating the friend list as empty");
        if (proxy == null)
            return [];

        return proxy->CharDataSpan.ToArray();
    }

    public static void OpenQuestLog(RowRef<Quest> quest)
    {
        var splits = quest.Value.Id.ToString().Split("_");
        if (splits.Length != 2)
        {
            Plugin.ChatGui.Print("QuestId is wrongly formatted");
            return;
        }

        if (!uint.TryParse(splits[1], NumberStyles.Any, CultureInfo.InvariantCulture,  out var questId))
        {
            Plugin.ChatGui.Print("Unable to parse quest id");
            return;
        }

        var agent = Chat.ResolveOrNull<AgentQuestJournal>(&AgentQuestJournal.Instance, "GameFunctions/questJournal", "Could not resolve AgentQuestJournal; the quest log was not opened");
        if (agent == null)
            return;

        agent->OpenForQuest(questId, 1);
    }

    public static void OpenPartyFinder(uint id)
    {
        var agent = Chat.ResolveOrNull<AgentLookingForGroup>(&AgentLookingForGroup.Instance, "GameFunctions/lfgListing", "Could not resolve AgentLookingForGroup; the party finder listing was not opened");
        if (agent == null)
            return;

        agent->OpenListing(id);
    }

    public static void OpenAchievement(uint id)
    {
        var agent = Chat.ResolveOrNull<AgentAchievement>(&AgentAchievement.Instance, "GameFunctions/achievement", "Could not resolve AgentAchievement; the achievement was not opened");
        if (agent == null)
            return;

        agent->OpenById(id);
    }

    public static bool IsInInstance()
    {
        return Plugin.Condition[ConditionFlag.BoundByDuty56];
    }

    public static bool TryOpenAdventurerPlate(ulong playerId)
    {
        // The existing try only ever covered half of this: it catches the throw from the [Agent]
        // chain, but AgentCharaCard.Instance() returning null and then being dereferenced by
        // OpenCharaCard is an AccessViolationException, which this catch cannot see. The try is kept
        // (OpenCharaCard is [MemberFunction] and throws on its own unresolved signature, and the
        // existing Warning-level message is the caller's contract), with the null check added inside.
        try
        {
            var agent = AgentCharaCard.Instance();
            if (agent == null)
            {
                Plugin.Log.Warning("Unable to open adventurer plate: AgentCharaCard is not available");
                return false;
            }

            agent->OpenCharaCard(playerId);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Unable to open adventurer plate");
            return false;
        }
    }

    public static void ClickNoviceNetworkButton()
    {
        // agent was dereferenced immediately for its vtable with no check. The vtable pointer itself
        // is checked too: agent->VirtualTable is a plain field read, so it survives a half-torn-down
        // agent and hands back null, and the extra `*` below would then fault on address 0.
        var agent = Chat.ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "GameFunctions/noviceButton", "Could not resolve AgentChatLog; the novice network button was not clicked");
        if (agent == null || agent->VirtualTable == null)
            return;

        // case 3
        var value = new AtkValue { Type = ValueType.Int, Int = 3, };
        var result = 0;
        var vf0 = *(delegate* unmanaged<AgentChatLog*, int*, AtkValue*, ulong, ulong, int*>*) agent->VirtualTable;
        if (vf0 == null)
            return;

        vf0(agent, &result, &value, 0, 0);
    }

}
