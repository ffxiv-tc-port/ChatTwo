using System.Globalization;
using System.Runtime.InteropServices;
using ChatTwo.Util;
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
    // Deliberately not hooked on TC. On an earlier client this signature landed on the wrong function (the
    // detour fired from unrelated native call sites, which no argument check can guard). On 7.25 it
    // resolves to a single target, but whether that target is the right function is unverified - and
    // nothing needs the hook any more: ListCommand() is the placeholder-free replacement.
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
    // Instance() can have (see the comment there). The null half matters most: dereferencing a null
    // Instance() is an uncatchable AccessViolationException no matter which path reached it. There
    // are no live detours in this file, so an escaping throw would merely be caught by Dalamud -
    // except in Dispose and in the per-frame path, both called out where they occur.
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
        ChatBox.SendMessage(PlayerMenuRouting.ListAddCommand(commandName, name, worldRow.Name.ToString()));
    }

    // Try around the call as well as the resolve, because two callers are hurt by a merely-caught
    // exception: Plugin.Dispose() -> SetChatInteractable(true) (a throw aborts the rest of Dispose and
    // leaves hooks attached) and Plugin.FrameworkUpdate() -> IsAddonInteractable (every frame while
    // HideChat is on; the throttled Error keeps a signature break from flooding the log).
    // Degradation: reported as "no such addon", which every caller already handles.
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
        // AtkStage.Instance() is [StaticAddress(isPointer: true)]: it throws when the signature does not
        // resolve (a live risk on TC) AND returns null whenever the game has not constructed the
        // singleton (loading screen, teardown). Both are ordinary states, and the deref at the bottom of
        // this method (atkStage->TooltipManager) would be an AccessViolationException that no try/catch
        // can intercept.
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
        agent->Flag2 = 1;
        agent->Flag3 = 0;

        // This just probably needs to be set
        agent->AddonId = addon->Id;

        // Skips early return
        // TC note: upstream calls this field `TooltipType`; the FFXIVClientStructs pin this fork builds
        // against names it `Flag1` (same FieldOffset 0x14C, same "Flag1 |= 2 allows AddonItemDetail to be
        // shown" semantics) - a field rename, not a TC-specific gap.
        atkStage->TooltipManager.Flag1 |= 2;
        addon->Show(false, 15);
    }

    public static void CloseItemTooltip()
    {
        // hide addon first to prevent the "addon close" sound
        var addon = GetAddon<AtkUnitBase>("ItemDetail");
        if (addon != null)
            addon->Hide(true, false, 0);

        // Same [Agent] getter as OpenItemTooltip above: resolve through ResolveOrNull, then null-check.
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
        // Contract: always ends with the party finder window open - focused if it already is, shown if not.
        // lfg and the RaptureAtkModule below (plus the vtable read off it) all need a null check.
        // IsAgentActive is [VirtualFunction], which never throws but renders as
        // `VirtualTable->IsAgentActive(this)` - so a null receiver faults on the vtable load rather than
        // raising anything catchable.
        var lfg = Chat.ResolveOrNull<AgentLookingForGroup>(&AgentLookingForGroup.Instance, "GameFunctions/lfg", "Could not resolve AgentLookingForGroup; the party finder was not toggled");
        if (lfg == null)
            return;

        if (lfg->IsAgentActive())
        {
            var addonId = lfg->GetAddonId();
            var atkModule = Chat.ResolveOrNull<RaptureAtkModule>(&RaptureAtkModule.Instance, "GameFunctions/lfgAtkModule", "Could not resolve RaptureAtkModule; the party finder window was not brought to front");
            if (atkModule == null)
                return;

            // FocusAddon is [VirtualFunction(27)]; that slot was checked against the TC 7.25 executable
            // (neighbor slots 26 IsAddonReady / 28 ClearFocus exclude an off-by-one). It does not null-check
            // the vtable or the slot itself, so both are checked here.
            var atkModuleVtbl = atkModule->VirtualTable;
            if (atkModuleVtbl == null || atkModuleVtbl->FocusAddon == null)
                return;

            atkModule->FocusAddon(addonId, true);
        }
        else
        {
            // Deliberately no "already requested, so hide instead" branch: upstream read a raw agent offset
            // for it that now lands inside the last viewed listing's comment text, so a long comment turned
            // this click into Hide().
            lfg->Show();
        }
    }

    // PlayerState.Instance() is a plain [StaticAddress] (no isPointer): it can throw but never returns
    // null. The null check below is still live - ResolveOrNull returns null on its catch path.
    // Degradation: reporting "not a mentor" hides the novice-network entries in the context menu and
    // the channel switcher - fail-closed, since the alternative would be offering an action the game
    // would then reject.
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
        if (!quest.IsValid)
        {
            Plugin.ChatGui.PrintError("Quest is wrongly formatted");
            return;
        }

        var splits = quest.Value.Id.ToString().Split("_");
        if (splits.Length != 2)
        {
            Plugin.ChatGui.PrintError("QuestId is wrongly formatted");
            return;
        }

        if (!uint.TryParse(splits[1], NumberStyles.Any, CultureInfo.InvariantCulture,  out var questId))
        {
            Plugin.ChatGui.PrintError("Unable to parse quest id");
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
        // The try only covers half of this: it catches the throw from the [Agent] chain, but
        // AgentCharaCard.Instance() returning null and then being dereferenced by OpenCharaCard is an
        // AccessViolationException the catch cannot see - hence the null check inside. The try stays
        // (OpenCharaCard is [MemberFunction] and throws on an unresolved signature; the Warning-level
        // message is the caller's contract).
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
        // The agent is dereferenced for its vtable, so both are checked: agent->VirtualTable is a plain
        // field read, so it survives a half-torn-down
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
