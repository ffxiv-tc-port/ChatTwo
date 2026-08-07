using System.Text;
using ChatTwo.Resources;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ChatTwo.GameFunctions;

public unsafe class ChatBox
{
    // This one sits on a natively-reachable path, which makes it the most dangerous of the family:
    // Chat.ReplyInSelectedChatModeDetour -> Chat.SetChannelWithExtraChat -> (ExtraChat linkshell
    // branch) -> here. Inside a detour, a null dereference is an AccessViolationException that
    // terminates the process, and a managed exception escaping back into native code terminates it
    // too - so both halves have to be handled, and neither substitutes for the other.
    //
    // UIModule.Instance() is the hand-written chained kind: it returns null on its own
    // (`framework == null ? null : framework->GetUIModule()`) while the throw comes from
    // Framework.Instance(), a [StaticAddress] stub, one level down. Chat.GetUIModuleOrNull covers
    // both; see the comment on Chat.ResolveOrNull.
    //
    // Degradation: the message is not sent and an Error is logged (throttled to one per 30s, shared
    // with Chat's guards). For the ExtraChat path above that means the channel prefix command never
    // reaches ExtraChat, so the channel override is not applied and the user's next message goes to
    // whatever channel vanilla currently has - the same outcome as Chat.SetChannel's guards.
    public static void SendMessageUnsafe(byte[] message)
    {
        // Resolved before allocating so the failure path has nothing to free. Both are side-effect
        // free, so the reordering is safe.
        var uiModule = Chat.GetUIModuleOrNull("ChatBox/uiModule", "Could not resolve UIModule; the chat message was not sent");
        if (uiModule == null)
            return;

        // Utf8String.FromSequence reaches IMemorySpace.GetDefaultSpace(), which is [MemberFunction]
        // and throws InvalidOperationException when its signature does not resolve. That throw was
        // previously free to escape into the detour above, i.e. terminate the process. The null
        // check afterwards is defence in depth: IMemorySpace.Create<T>() genuinely returns null on
        // allocation failure, but FromSequence then dereferences it before returning (NullTerminate
        // never yields an empty array, so the `str != null` branch is always taken), meaning the
        // access violation happens inside ClientStructs and never reaches us. Cheap to keep, and it
        // stops being dead the moment that upstream code grows a null check.
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

    private static string SanitiseText(string text)
    {
        var uText = Utf8String.FromString(text);

        uText->SanitizeString((AllowedEntities) 0x27F);
        var sanitised = uText->ToString();
        uText->Dtor(true);

        return sanitised;
    }
}