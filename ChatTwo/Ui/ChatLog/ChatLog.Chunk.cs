namespace ChatTwo.Ui.ChatLog;

public partial class ChatLog
{
    public void DrawChannelName(Tab activeTab, bool sendChannelSwitch = false)
    {
        var currentChannel = ReadChannelName(activeTab);
        if (sendChannelSwitch && !SameLabel(currentChannel, PreviousChannel))
        {
            PreviousChannel = currentChannel;
            Plugin.ServerCore.SendChannelSwitch(currentChannel);
        }

        InputHandler.ChunkHandler.DrawChunks(currentChannel);
    }

    // Several labels are rebuilt every frame, so chunks are compared by what the web page is sent, not by reference;
    // otherwise those labels would send a channel switch every frame.
    private static bool SameLabel(Chunk[] a, Chunk[] b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a.Length != b.Length)
            return false;

        for (var i = 0; i < a.Length; i++)
        {
            if (ReferenceEquals(a[i], b[i]))
                continue;

            var same = (a[i], b[i]) switch
            {
                (TextChunk x, TextChunk y) => x.Content == y.Content && x.FallbackColor == y.FallbackColor && x.Foreground == y.Foreground
                                              && x.Glow == y.Glow && x.Italic == y.Italic && ReferenceEquals(x.Link, y.Link),
                (IconChunk x, IconChunk y) => x.Icon == y.Icon && ReferenceEquals(x.Link, y.Link),
                _ => false,
            };

            if (!same)
                return false;
        }

        return true;
    }
}