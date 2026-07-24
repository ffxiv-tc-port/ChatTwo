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
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

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

    public void AddToMuteList(ulong accountId, ulong contentId, string name, short worldId)
    {
        AgentMutelist.Instance()->Add(accountId, contentId, name, worldId);
    }

    public void AddToTermsList(SeString content)
    {
        AgentTermFilter.Instance()->OpenNewFilterWindow(content.EncodeWithNullTerminator());
    }

    private void ListCommand(string name, ushort world, string commandName)
    {
        var worldRow = Sheets.WorldSheet.GetRow(world);
        ChatBox.SendMessage($"/{commandName} add {name}@{worldRow.Name.ToString()}");
    }

    private static T* GetAddon<T>(string name) where T : unmanaged
    {
        var addon = RaptureAtkModule.Instance()->RaptureAtkUnitManager.GetAddonByName(name);
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
        var atkStage = AtkStage.Instance();
        var agent = AgentItemDetail.Instance();
        var addon = GetAddon<AtkUnitBase>("ItemDetail");

        // atkStage ain't gonna be null or we have bigger problems
        if (agent == null || addon == null)
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

        var agent = AgentItemDetail.Instance();
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
        var lfg = AgentLookingForGroup.Instance();
        if (lfg->IsAgentActive())
        {
            var addonId = lfg->GetAddonId();
            var atkModule = RaptureAtkModule.Instance();
            var atkModuleVtbl = (void**) atkModule->AtkModule.VirtualTable;
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

    public static bool IsMentor()
    {
        return PlayerState.Instance()->IsMentor();
    }

    public static InfoProxyCommonList.CharacterData[] GetFriends()
    {
        return InfoProxyFriendList.Instance()->CharDataSpan.ToArray();
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

        AgentQuestJournal.Instance()->OpenForQuest(questId, 1);
    }

    public static void OpenPartyFinder(uint id)
    {
        AgentLookingForGroup.Instance()->OpenListing(id);
    }

    public static void OpenAchievement(uint id)
    {
        AgentAchievement.Instance()->OpenById(id);
    }

    public static bool IsInInstance()
    {
        return Plugin.Condition[ConditionFlag.BoundByDuty56];
    }

    public static bool TryOpenAdventurerPlate(ulong playerId)
    {
        try
        {
            AgentCharaCard.Instance()->OpenCharaCard(playerId);
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
        var agent = AgentChatLog.Instance();
        // case 3
        var value = new AtkValue { Type = ValueType.Int, Int = 3, };
        var result = 0;
        var vf0 = *(delegate* unmanaged<AgentChatLog*, int*, AtkValue*, ulong, ulong, int*>*) agent->VirtualTable;
        vf0(agent, &result, &value, 0, 0);
    }

}
