using System.Text;
using ChatTwo.Code;
using ChatTwo.GameFunctions;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;

namespace ChatTwo.Ui.Handler;

public class SendHandler
{
    private readonly Plugin Plugin;

    public List<string> InputBacklog = [];
    public int InputBacklogIdx = -1;

    private string Message = string.Empty;
    private bool TellSpecialUnused;

    public SendHandler(Plugin plugin)
    {
        Plugin = plugin;
    }

    public void SendWithoutContext(string message)
    {
        Message = message;
        SendChatBox(Plugin.CurrentTab, ref Message, ref TellSpecialUnused);
    }

    public void SendChatBox(Tab activeTab, ref string chatInput, ref bool tellSpecial)
    {
        if (!string.IsNullOrWhiteSpace(chatInput))
        {
            var trimmed = chatInput.Trim();
            AddBacklog(trimmed);
            InputBacklogIdx = -1;

            if (HasTranslationCommand(trimmed))
            {
                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                return;
            }

            if (tellSpecial)
            {
                var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                AutoTranslate.ReplaceWithPayload(ref tellBytes);

                Plugin.Functions.Chat.SendTellUsingCommandInner(tellBytes);
                tellSpecial = false;

                activeTab.CurrentChannel.ResetTempChannel();
                chatInput = string.Empty;
                return;
            }

            if (!trimmed.StartsWith('/'))
            {
                var target = activeTab.TellTarget.IsSet() ? activeTab.TellTarget : activeTab.CurrentChannel.TempTellTarget ?? activeTab.CurrentChannel.TellTarget;
                if (target != null)
                {
                    // ContentId 0 is a case where we can't directly send messages, so we send a /tell formatted message and let the game handle it
                    if (target.ContentId == 0)
                    {
                        SendTellAsTextCommand(target.Name, target.ToWorldString(), trimmed);

                        activeTab.CurrentChannel.ResetTempChannel();
                        chatInput = string.Empty;
                        return;
                    }

                    var reason = target.Reason;
                    var worldName = target.ToWorldString();
                    var worldIsPublic = Sheets.WorldSheet.TryGetRow(target.World, out var world) && world.IsPublic;
                    switch (TellRouting.Decide(worldIsPublic, target.World, target.Name, worldName))
                    {
                        case TellRoute.Native:
                        {
                            if (reason == TellReason.Reply && GameFunctions.GameFunctions.GetFriends().Any(friend => friend.ContentId == target.ContentId))
                                reason = TellReason.Friend;

                            var tellBytes = Encoding.UTF8.GetBytes(trimmed);
                            AutoTranslate.ReplaceWithPayload(ref tellBytes);

                            Plugin.Functions.Chat.SendTell(reason, target.ContentId, target.Name, (ushort) world.RowId, tellBytes, trimmed);
                            break;
                        }
                        case TellRoute.TextCommand:
                            Plugin.Log.Information($"Tell target on world {target.World} carries a content ID but the world is not flagged public; sending it as a /tell text command");
                            SendTellAsTextCommand(target.Name, worldName, trimmed);
                            break;
                        default:
                            // Never guess at a recipient: say so and send nothing. The text stays in the input history.
                            Plugin.Log.Warning($"Tell not sent: target world {target.World} cannot be addressed (name empty: {string.IsNullOrWhiteSpace(target.Name)}, world name empty: {string.IsNullOrWhiteSpace(worldName)})");
                            Plugin.ChatGui.PrintError(Language.Chat_SendTell_Error);
                            break;
                    }

                    activeTab.CurrentChannel.ResetTempChannel();
                    chatInput = string.Empty;
                    return;
                }

                if (activeTab.CurrentChannel.UseTempChannel)
                    trimmed = $"{activeTab.CurrentChannel.TempChannel.Prefix()} {trimmed}";
                else
                    trimmed = $"{activeTab.CurrentChannel.Channel.Prefix()} {trimmed}";
            }

            var bytes = Encoding.UTF8.GetBytes(trimmed);
            AutoTranslate.ReplaceWithPayload(ref bytes);

            ChatBox.SendMessageUnsafe(bytes);
        }

        activeTab.CurrentChannel.ResetTempChannel();
        chatInput = string.Empty;
    }

    private static void SendTellAsTextCommand(string name, string worldName, string text)
    {
        var tellBytes = Encoding.UTF8.GetBytes(TellRouting.TextCommand(name, worldName, text));
        AutoTranslate.ReplaceWithPayload(ref tellBytes);

        ChatBox.SendMessageUnsafe(tellBytes);
    }

    private bool HasTranslationCommand(string trimmed)
    {
        var messageBytes = Encoding.UTF8.GetBytes(trimmed);
        if (AutoTranslate.StartsWithCommand(ref messageBytes))
        {
            ChatBox.SendMessageUnsafe(messageBytes);
            return true;
        }

        return false;
    }

    public void AddBacklog(string message)
    {
        for (var i = 0; i < InputBacklog.Count; i++)
        {
            if (InputBacklog[i] != message)
                continue;

            InputBacklog.RemoveAt(i);
            break;
        }

        InputBacklog.Add(message);
    }
}