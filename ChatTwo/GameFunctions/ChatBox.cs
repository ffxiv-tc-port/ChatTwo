using System.Text;
using ChatTwo.Resources;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo.GameFunctions;

public unsafe class ChatBox
{
    // Natively reachable (Chat.ReplyInSelectedChatModeDetour -> Chat.SetChannelWithExtraChat ->
    // ExtraChat linkshell branch -> here): a null dereference or an escaping managed exception both
    // terminate the process, so both halves are handled via Chat.GetUIModuleOrNull (see
    // Chat.ResolveOrNull). Degradation: the message is not sent and a throttled Error is logged; for
    // the ExtraChat path the channel override is not applied, same outcome as Chat.SetChannel's guards.
    public static void SendMessageUnsafe(byte[] message)
    {
        // Resolved before allocating so the failure path has nothing to free. Both are side-effect
        // free, so the reordering is safe.
        var uiModule = Chat.GetUIModuleOrNull("ChatBox/uiModule", "Could not resolve UIModule; the chat message was not sent");
        if (uiModule == null)
            return;

        // Utf8String.FromSequence reaches IMemorySpace.GetDefaultSpace(), which is [MemberFunction]
        // and throws InvalidOperationException when its signature does not resolve - inside the detour
        // above that would terminate the process. The null check afterwards is defence in depth:
        // FromSequence dereferences the allocation before returning, so it cannot fire today, but it is
        // cheap to keep.
        Utf8String* mes;
        try
        {
            mes = Utf8String.FromSequence(message.NullTerminate());
        }
        catch (Exception ex)
        {
            Chat.LogErrorThrottled("ChatBox/alloc", ex, "Could not allocate the chat message string; the chat message was not sent");
            return;
        }

        if (mes == null)
            return;

        try
        {
            // ProcessChatBoxEntry is [MemberFunction], so it throws on an unresolved signature too.
            uiModule->ProcessChatBoxEntry(mes);
        }
        catch (Exception ex)
        {
            Chat.LogErrorThrottled("ChatBox/processEntry", ex, "ProcessChatBoxEntry failed; the chat message was not sent");
        }
        finally
        {
            mes->Dtor(true);
        }
    }

    public static void SendMessage(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length == 0)
            throw new ArgumentException(Language.ChatBox_Error_Empty, nameof(message));

        if (bytes.Length > 500)
            throw new ArgumentException(Language.ChatBox_Error_Too_Long, nameof(message));

        if (message.Length != SanitiseText(message).Length)
            throw new ArgumentException(Language.ChatBox_Error_Invalid, nameof(message));

        SendMessageUnsafe(bytes);
    }

    // TC: this must not call Utf8String.SanitizeString. That native function is resolved by a
    // byte-pattern signature baked into FFXIVClientStructs, and on the TC client the scan lands on
    // the wrong address - calling it crashes the game (see the note on Chat.IsCharValid).
    // The only consumer compares lengths to reject messages containing characters the game would
    // strip; the managed check is more permissive, and the game rejects those messages on its own.
    private static string SanitiseText(string text)
    {
        var sanitised = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c))
                sanitised.Append(c);
        }

        return sanitised.ToString();
    }
}