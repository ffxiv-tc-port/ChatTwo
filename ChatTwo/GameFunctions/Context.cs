using ChatTwo.Util;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace ChatTwo.GameFunctions;

// Every method in this file is a one-shot context-menu action reached only from managed code
// (PayloadHandler -> ImGui.Selectable), so an escaping exception is caught by Dalamud and costs at
// most the rest of that frame's draw. A null Instance() is a different matter: dereferencing one is
// an AccessViolationException, a corrupted-state exception that try/catch cannot intercept in .NET
// Core at all, so it takes the game down regardless of who called it.
//
// The guards therefore use Chat.ResolveOrNull, which handles BOTH halves at the dominant failure
// site: [Agent] and [InfoProxy] getters return null when the module is not built yet, AND throw
// InvalidOperationException from the three [StaticAddress]/[MemberFunction] stubs in the chain
// underneath the generator template (Framework.Instance, GetUIModule, GetAgentByInternalId /
// GetInfoProxyById). See the taxonomy comment next to ResolveOrNull in Chat.cs for the derivation.
//
// The member functions invoked afterwards are [MemberFunction] and throw on their own unresolved
// signatures; those are deliberately left to propagate to Dalamud, because on this path the
// distinction the user sees is "the menu entry did nothing" either way, and the log entry from a
// real signature break is worth more than a swallowed one.
//
// Degradation for all of them: the action silently does nothing and one throttled Error is logged.
public sealed unsafe class Context
{
    public static void InviteToNoviceNetwork(string name, ushort world)
    {
        var proxy = Chat.ResolveOrNull<InfoProxyNoviceNetwork>(&InfoProxyNoviceNetwork.Instance, "Context/noviceNetwork", "Could not resolve InfoProxyNoviceNetwork; the novice network invite was not sent");
        if (proxy == null)
            return;

        // can specify content id if we have it, but there's no need
        proxy->InviteToNoviceNetwork(0, 0, world, name.ToTerminatedBytes());
    }

    public static void TryOn(uint itemId, byte stainId)
    {
        AgentTryon.TryOn(0xFF, itemId, stainId);
    }

    public static void LinkItem(uint itemId)
    {
        var agent = Chat.ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "Context/linkItem", "Could not resolve AgentChatLog; the item link was not inserted");
        if (agent == null)
            return;

        agent->LinkItem(itemId);
    }

    public static void LinkStatus(uint statusId)
    {
        var agent = Chat.ResolveOrNull<AgentChatLog>(&AgentChatLog.Instance, "Context/linkStatus", "Could not resolve AgentChatLog; the status link was not inserted");
        if (agent == null)
            return;

        agent->ContextStatusId = statusId;
    }

    public static void OpenItemComparison(uint itemId)
    {
        var agent = Chat.ResolveOrNull<AgentItemComp>(&AgentItemComp.Instance, "Context/itemComp", "Could not resolve AgentItemComp; the item comparison was not opened");
        if (agent == null)
            return;

        agent->CompareItem(0x4D, itemId, 0, 0);
    }

    public static void SearchForRecipesUsingItem(uint itemId)
    {
        var agent = Chat.ResolveOrNull<AgentRecipeProductList>(&AgentRecipeProductList.Instance, "Context/recipeProductList", "Could not resolve AgentRecipeProductList; the recipe search was not started");
        if (agent == null)
            return;

        agent->SearchForRecipesUsingItem(itemId);
    }

    public static void SearchForItem(uint itemId)
    {
        // ItemFinderModule.Instance() is the hand-written chained kind rather than an [Agent] getter,
        // but it fails identically: `uiModule == null ? null : uiModule->GetItemFinderModule()`, with
        // the throw coming from Framework.Instance() further down.
        var module = Chat.ResolveOrNull<ItemFinderModule>(&ItemFinderModule.Instance, "Context/itemFinder", "Could not resolve ItemFinderModule; the item search was not started");
        if (module == null)
            return;

        module->SearchForItem(itemId);
    }
}
