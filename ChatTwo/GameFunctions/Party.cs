using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace ChatTwo.GameFunctions;

// All five entry points are context-menu actions reached only from managed code (PayloadHandler ->
// ImGui.Selectable). Guards use Chat.ResolveOrNull (see the comment there): dereferencing a null
// Instance() is an uncatchable AccessViolationException even from a purely managed call site.
// Degradation: the invite/kick/promote silently does not happen and one throttled Error is logged.
// Deliberately no user-facing notification - users read a popup here as a server answer.
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
