using ChatTwo.Http.MessageProtocol;
using ChatTwo.Ui.Handler;
using ChatTwo.Util;
using Dalamud.Plugin.Services;

namespace ChatTwo.Http;

public class ServerCore : IAsyncDisposable
{
    public static readonly HttpClient HttpClient = new();

    public readonly Plugin Plugin;
    public readonly SendHandler SendHandler;
    private readonly HostContext HostContext;

    public ServerCore(Plugin plugin)
    {
        Plugin = plugin;

        SendHandler = new SendHandler(plugin);
        HostContext = new HostContext(this);

        Plugin.Framework.Update += FrameworkUpdate;
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        Plugin.Framework.Update -= FrameworkUpdate;
        await HostContext.DisposeAsync();
    }

    private void FrameworkUpdate(IFramework _)
    {
        // Every frame: an index loop, because Index() allocates an iterator and a boxed list enumerator per call.
        var tabs = Plugin.Config.Tabs;
        for (var idx = 0; idx < tabs.Count; idx++)
        {
            var tab = tabs[idx];
            if (tab.Unread == tab.LastSendUnread)
                continue;

            tab.LastSendUnread = tab.Unread;
            foreach (var eventServer in HostContext.EventConnections)
                eventServer.OutboundQueue.Enqueue(new ChatTabUnreadStateEvent(new ChatTabUnreadState(idx, tab.Unread)));
        }
    }

    #region SSE Helper
    public async Task PrepareNewClient(SSEConnection sse)
    {
        // 🔴🔴 新的 SSE 連線是從 HTTP 伺服器的執行緒進來的（RouteController.NewSSEConnection），
        //    而這一支底下兩段都會轉派給 framework 執行緒，並且會讀原生記憶體（通訊貝名稱、截圖模式下的 LocalPlayer）。
        //    卸載期 RunOnFrameworkThread／無延遲的 RunOnTick 會就地在 HTTP 執行緒上跑它們 ＝ 攔不到的 AccessViolation。
        // 🔑 卸載期直接不準備這個客戶端：網頁端只會看到一條沒有初始資料的連線，而遊戲下一刻就要關了。
        // 📌 SendNewLogin() 那條路徑是從 framework 執行緒進來的，閘門不會觸發。
        if (FrameworkUnloadGuard.ShouldSkip("網頁介面初始化新的 SSE 連線"))
            return;

        // This takes long, so keep it outside the next frame
        var messages = await HostContext.Processing.GetAllMessages();

        // Using the bulk message event to clear everything on the client side that may still exist
        await Plugin.Framework.RunOnTick(() =>
        {
            sse.OutboundQueue.Enqueue(new BulkMessagesEvent(messages));

            sse.OutboundQueue.Enqueue(new SwitchChannelEvent(HostContext.Processing.GetCurrentChannel()));
            sse.OutboundQueue.Enqueue(new ChannelListEvent(HostContext.Processing.GetValidChannels()));

            sse.OutboundQueue.Enqueue(new ChatTabSwitchedEvent(HostContext.Processing.GetCurrentTab()));
            sse.OutboundQueue.Enqueue(new ChatTabListEvent(HostContext.Processing.GetAllTabs()));
        });
    }

    public void SendNewMessage(Message message)
    {
        if (!HostContext.IsActive)
            return;

        // 🔴🔴 這一支是從 MessageManager.ProcessPendingMessages 進來的，那是一條自己的背景執行緒，
        //    不是 framework 執行緒。無延遲的 RunOnTick 在卸載期會就地在那條執行緒上執行委派，而
        //    ReadMessageContent → ProcessChunk 在「截圖模式」開著時會讀 Plugin.PlayerState（ObjectTable.LocalPlayer）
        //    ＝ 從別的執行緒對隨時可能被換掉的原生指標解參考，是下面的 catch 攔不到的 AccessViolation。
        // 🔑 卸載期跳過這一則 SSE 推播：客戶端頂多少收到最後一兩行聊天，而它們馬上也會斷線。
        if (FrameworkUnloadGuard.ShouldSkip("網頁介面推播新聊天訊息"))
            return;

        try
        {
            Plugin.Framework.RunOnTick(() =>
            {
                var bundledResponse = new NewMessageEvent(HostContext.Processing.ReadMessageContent(message));
                foreach (var eventServer in HostContext.EventConnections)
                    eventServer.OutboundQueue.Enqueue(bundledResponse);
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Sending message over SSE failed.");
        }
    }

    public void SendBulkMessageList()
    {
        if (!HostContext.IsActive)
            return;

        try
        {
            Plugin.Framework.RunOnTick(() =>
            {
                foreach (var eventServer in HostContext.EventConnections)
                    eventServer.OutboundQueue.Enqueue(new BulkMessagesEvent(new Messages(HostContext.Processing.ReadMessageList().Result)));
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Sending channel switch over SSE failed.");
        }
    }

    public void SendChannelSwitch(Chunk[] channelName)
    {
        if (!HostContext.IsActive)
            return;

        try
        {
            Plugin.Framework.RunOnTick(() =>
            {
                var bundledResponse = new SwitchChannelEvent(new SwitchChannel(HostContext.Processing.ReadChannelName(channelName)));
                foreach (var eventServer in HostContext.EventConnections)
                    eventServer.OutboundQueue.Enqueue(bundledResponse);
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Sending channel switch over SSE failed.");
        }
    }

    public void SendChannelList()
    {
        if (!HostContext.IsActive)
            return;

        try
        {
            Plugin.Framework.RunOnTick(() =>
            {
                var bundledResponse = new ChannelListEvent(HostContext.Processing.GetValidChannels());
                foreach (var eventServer in HostContext.EventConnections)
                    eventServer.OutboundQueue.Enqueue(bundledResponse);
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Sending channel switch over SSE failed.");
        }
    }

    public void SendNewLogin()
    {
        if (!HostContext.IsActive)
            return;

        try
        {
            Plugin.Framework.RunOnTick(async () =>
            {
                foreach (var eventServer in HostContext.EventConnections)
                    await HostContext.Core.PrepareNewClient(eventServer);
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Preparing all clients after login failed.");
        }
    }
    #endregion

    public void InvalidateSessions()
    {
        if (!HostContext.IsActive)
            return;

        Plugin.Config.AuthStore.Clear();
        Plugin.SaveConfig();
    }

    public bool IsActive()
    {
        return HostContext is { IsActive: true, Host.IsListening: true };
    }

    public bool IsStopping()
    {
        return HostContext is { IsActive: false, IsStopping: true };
    }


    public bool Start()
    {
        return HostContext.Start();
    }

    public void Run()
    {
        HostContext.Run();
    }

    public async ValueTask<bool> Stop()
    {
        return await HostContext.Stop();
    }
}