using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace ChatTwo.GameFunctions;

// All five entry points are context-menu actions reached only from managed code (PayloadHandler ->
// ImGui.Selectable). Every one of them dereferenced an Instance() with no check whatsoever.
//
// Guards use Chat.ResolveOrNull, which covers both halves: [InfoProxy] and [Agent] getters return
// null while the module is not built (early login, teardown), and they throw
// InvalidOperationException from the three [StaticAddress]/[MemberFunction] stubs in the chain below
// the generator template - Framework.Instance(), framework->GetUIModule(), and
// GetInfoProxyById / GetAgentByInternalId. See the taxonomy next to ResolveOrNull in Chat.cs.
//
// The null half is the reason this is not merely tidiness: dereferencing a null Instance() is an
// AccessViolationException, a corrupted-state exception that try/catch cannot intercept in .NET Core,
// so it kills the process from a purely managed call site.
//
// Degradation for all five: the invite/kick/promote silently does not happen and one throttled Error
// is logged. Deliberately no user-facing notification - these already have a "nothing happened"
// failure mode from the server side (the target declined, moved world, left the instance), so adding
// a popup here would report a client-side condition in a place users read as a server answer.
public static unsafe class Party
{
    public static void InviteSameWorld(string name, ushort world, ulong contentId)
    {
        var proxy = Chat.ResolveOrNull<InfoProxyPartyInvite>(&InfoProxyPartyInvite.Instance, "Party/inviteSameWorld", "Could not resolve InfoProxyPartyInvite; the party invite was not sent");
        if (proxy == null)
            return;

        // this only works if target is on the same world
        fixed (byte* namePtr = name.ToTerminatedBytes()) {
            proxy->InviteToParty(contentId, namePtr, world);
        }
    }

    public static void InviteOtherWorld(ulong contentId, ushort worldId = 0)
    {
        // third param is world, but it requires a specific world
        // if they're not on that world, it will fail
        // pass 0 and it will work on any world EXCEPT for the world the
        // current player is on
        if (contentId == 0)
        {
            WrapperUtil.AddNotification(Language.PartyInvite_NoId, NotificationType.Warning);
            return;
        }

        var proxy = Chat.ResolveOrNull<InfoProxyPartyInvite>(&InfoProxyPartyInvite.Instance, "Party/inviteOtherWorld", "Could not resolve InfoProxyPartyInvite; the cross-world party invite was not sent");
        if (proxy == null)
            return;

        proxy->InviteToPartyContentId(contentId, worldId);
    }

    public static void InviteInInstance(ulong contentId)
    {
        if (contentId == 0)
        {
            WrapperUtil.AddNotification(Language.PartyInvite_NoId, NotificationType.Warning);
            return;
        }

        var proxy = Chat.ResolveOrNull<InfoProxyPartyInvite>(&InfoProxyPartyInvite.Instance, "Party/inviteInInstance", "Could not resolve InfoProxyPartyInvite; the in-instance party invite was not sent");
        if (proxy == null)
            return;

        proxy->InviteToPartyInInstanceByContentId(contentId);
    }

    public static void Kick(string name, ulong contentId)
    {
        var agent = Chat.ResolveOrNull<AgentPartyMember>(&AgentPartyMember.Instance, "Party/kick", "Could not resolve AgentPartyMember; the party member was not kicked");
        if (agent == null)
            return;

        fixed (byte* namePtr = name.ToTerminatedBytes()) {
            agent->Kick(namePtr, 0, contentId);
        }
    }

    public static void Promote(string name, ulong contentId)
    {
        var agent = Chat.ResolveOrNull<AgentPartyMember>(&AgentPartyMember.Instance, "Party/promote", "Could not resolve AgentPartyMember; the party member was not promoted");
        if (agent == null)
            return;

        fixed (byte* namePtr = name.ToTerminatedBytes()) {
            agent->Promote(namePtr, 0, contentId);
        }
    }
}
