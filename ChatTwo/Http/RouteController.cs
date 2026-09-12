using System.Collections.Concurrent;
using System.Web;
using ChatTwo.Http.MessageProtocol;
using ChatTwo.Util;
using Lumina.Data.Files;
using Newtonsoft.Json;
using WatsonWebserver.Core;
using ErrorEventArgs = Newtonsoft.Json.Serialization.ErrorEventArgs;
using HttpMethod = WatsonWebserver.Core.HttpMethod;

namespace ChatTwo.Http;

public class RouteController
{
    private readonly HostContext HostContext;

    private readonly string AuthTemplate;
    private readonly string ChatBoxTemplate;

    private readonly ConcurrentDictionary<string, long> RateLimit = [];

    private readonly JsonSerializerSettings JsonSettings = new()
    {
        Error = delegate(object? _, ErrorEventArgs args) { args.ErrorContext.Handled = true; }
    };

    public RouteController(HostContext hostContext)
    {
        HostContext = hostContext;

        AuthTemplate = File.ReadAllText(Path.Combine(HostContext.StaticDir, "index.html"));
        ChatBoxTemplate = File.ReadAllText(Path.Combine(HostContext.StaticDir, "chat.html"));

        // Pre Auth
        HostContext.Host.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/", AuthRoute, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Static.Add(HttpMethod.POST, "/auth", AuthenticateClient, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/files/gfdata.gfd", GetGfdData, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/files/fonticon_ps5.tex", GetTexData, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/files/FFXIV_Lodestone_SSF.ttf", GetLodestoneFont, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Static.Add(HttpMethod.GET, "/favicon.ico", GetFavicon, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Parameter.Add(HttpMethod.GET, "/emote/{name}", GetEmote, ExceptionRoute);

        // Post Auth
        HostContext.Host.Routes.PostAuthentication.Static.Add(HttpMethod.GET, "/chat", ChatBoxRoute, ExceptionRoute);
        HostContext.Host.Routes.PostAuthentication.Static.Add(HttpMethod.POST, "/send", ReceiveMessage, ExceptionRoute);
        HostContext.Host.Routes.PostAuthentication.Static.Add(HttpMethod.POST, "/channel", ReceiveChannelSwitch, ExceptionRoute);
        HostContext.Host.Routes.PostAuthentication.Static.Add(HttpMethod.POST, "/tab", ReceiveTabSwitch, ExceptionRoute);

        // Ship all other static files dynamically
        HostContext.Host.Routes.PreAuthentication.Content.Add("/_app/", true, ExceptionRoute);
        HostContext.Host.Routes.PreAuthentication.Content.Add("/static/", true, ExceptionRoute);

        // Server-Sent Events Route
        HostContext.Host.Routes.PostAuthentication.Static.Add(HttpMethod.POST, "/sse", NewSSEConnection, ExceptionRoute);
    }

    private async Task ExceptionRoute(HttpContextBase ctx, Exception _)
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.Send("Internal Server Error, please try again");
    }

    private async Task AuthRoute(HttpContextBase ctx)
    {
        if (Plugin.Config.AuthStore.Count > 0)
        {
            var cookies = WebserverUtil.GetCookieData(ctx.Request.Headers.Get("Cookie") ?? "");
            if (cookies.TryGetValue("ChatTwo-token", out var value) && Plugin.Config.AuthStore.Contains(value))
            {
                await Redirect(ctx, "/chat");
                return;
            }
        }

        await ctx.Response.Send(AuthTemplate);
    }

    public void Dispose()
    {

    }

    #region FileHandlerRoutes
    private async Task GetTexData(HttpContextBase ctx)
    {
        var data = Plugin.DataManager.GetFile<TexFile>("common/font/fonticon_ps5.tex")!.Data;
        await ctx.Response.Send(data);
    }

    private async Task GetGfdData(HttpContextBase ctx)
    {
        var data = Plugin.DataManager.GetFile("common/font/gfdata.gfd")!.Data;
        await ctx.Response.Send(data);
    }

    private async Task GetLodestoneFont(HttpContextBase ctx)
    {
        var data = HostContext.Core.Plugin.FontManager.GameSymFont;
        await ctx.Response.Send(data);
    }

    private async Task GetFavicon(HttpContextBase ctx)
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.Send();
    }

    private async Task GetEmote(HttpContextBase ctx)
    {
        var name = ctx.Request.Url.Parameters["name"] ?? "";
        if (name == "" || !EmoteCache.Exists(name))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.Send("Malformed emote name.");
            return;
        }


        var emote = EmoteCache.GetEmote(name);
        if (emote is null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.Send("Emote not valid.");
            return;
        }

        // Wait for the emote to be loaded a maximum of 5 times
        var timeout = 5;
        while (!emote.IsLoaded && timeout > 0)
        {
            timeout--;
            await Task.Delay(25);
        }

        ctx.Response.Headers.Add("Cache-Control", "max-age=86400");
        await ctx.Response.Send(emote.RawData);
    }
    #endregion

    #region PreAuthRoutes
    private async Task<bool> AuthenticateClient(HttpContextBase ctx)
    {
        var currentTick = Environment.TickCount64;
        if (RateLimit.TryGetValue(ctx.Request.Source.IpAddress, out var timestamp) && timestamp > currentTick)
        {
            _ = ctx.Request.DataAsString; // Temp fix for Watson.Lite bug #155
            return await Redirect(ctx, "/", ("message", "Rate limit active (10s)"));
        }

        // The next request will be rate limited for 10s
        RateLimit[ctx.Request.Source.IpAddress] = currentTick + 10_000;

        var authcode = HttpUtility.ParseQueryString(ctx.Request.DataAsString ?? "").Get("authcode");
        if (authcode == null || authcode != Plugin.Config.WebinterfacePassword)
            return await Redirect(ctx, "/", ("message", "Authentication failed"));

        var token = WebinterfaceUtil.GenerateSimpleToken();
        Plugin.Config.AuthStore.Add(token);

        ctx.Response.Headers.Add("Set-Cookie", $"ChatTwo-token={token}");
        return await Redirect(ctx, "/chat");
    }
    #endregion

    #region PostAuthRoutes
    private async Task ChatBoxRoute(HttpContextBase ctx)
    {
        await ctx.Response.Send(ChatBoxTemplate);
    }

    private async Task ReceiveMessage(HttpContextBase ctx)
    {
        if (!await EnforceMediaType(ctx, "application/json"))
            return;

        var content = JsonConvert.DeserializeObject<IncomingMessage>(ctx.Request.DataAsString, JsonSettings);
        if (content.Message.Length is < 2 or > 500)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.Send(JsonConvert.SerializeObject(new ErrorResponse("Invalid message received.")));
            return;
        }

        // 🔴🔴 這個處理常式跑在 HTTP 伺服器的執行緒上（不是 framework 執行緒），而
        //    RunOnFrameworkThread 在 Framework.IsFrameworkUnloading 為真時不排隊、
        //    會就地在這條執行緒上執行委派（本 pin Dalamud/Game/Framework.cs）。
        //    SendWithoutContext 會走進遊戲的聊天送出路徑（原生記憶體＋遊戲函式），
        //    卸載期正是那些結構被拆掉的時候。失敗形式是 AccessViolationException，
        //    那在 .NET Core 是 corrupted-state exception，try/catch 攔不到，
        //    使用者看到的是遊戲直接關掉。
        // 🔑 回 503 而不是假裝成功：訊息確實沒有送出去，網頁端該知道。
        if (FrameworkUnloadGuard.ShouldSkip("網頁介面送出聊天訊息"))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.Send(JsonConvert.SerializeObject(new ErrorResponse("The game is shutting down, the message was not sent.")));
            return;
        }

        await Plugin.Framework.RunOnFrameworkThread(() => { HostContext.Core.SendHandler.SendWithoutContext(content.Message); });

        ctx.Response.StatusCode = 201;
        await ctx.Response.Send(JsonConvert.SerializeObject(new OkResponse("Message was send to the channel.")));
    }

    private async Task ReceiveChannelSwitch(HttpContextBase ctx)
    {
        if (!await EnforceMediaType(ctx, "application/json"))
            return;

        var channel = JsonConvert.DeserializeObject<IncomingChannel>(ctx.Request.DataAsString, JsonSettings);
        if (!Enum.IsDefined(channel.Channel))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.Send(JsonConvert.SerializeObject(new ErrorResponse("Invalid channel received.")));
            return;
        }

        // 🔴🔴 同上：SetChannelWithExtraChat 會改遊戲聊天框目前的頻道（原生），
        //    而卸載期的 RunOnFrameworkThread 會就地在 HTTP 執行緒上跑它。
        if (FrameworkUnloadGuard.ShouldSkip("網頁介面切換聊天頻道"))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.Send(JsonConvert.SerializeObject(new ErrorResponse("The game is shutting down, the channel was not switched.")));
            return;
        }

        await Plugin.Framework.RunOnFrameworkThread(() => { HostContext.Core.Plugin.Functions.Chat.SetChannelWithExtraChat(channel.Channel); });

        ctx.Response.StatusCode = 201;
        await ctx.Response.Send(JsonConvert.SerializeObject(new OkResponse("Channel switch was initiated.")));
    }

    private async Task ReceiveTabSwitch(HttpContextBase ctx)
    {
        if (!await EnforceMediaType(ctx, "application/json"))
            return;

        var tab = JsonConvert.DeserializeObject<IncomingTab>(ctx.Request.DataAsString, JsonSettings);
        if (tab.Index < 0 || tab.Index >= Plugin.Config.Tabs.Count)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.Send(JsonConvert.SerializeObject(new ErrorResponse("Invalid tab received.")));
            return;
        }

        await Plugin.Framework.RunOnFrameworkThread(() => { HostContext.Core.Plugin.WantedTab = tab.Index; });

        ctx.Response.StatusCode = 201;
        await ctx.Response.Send(JsonConvert.SerializeObject(new OkResponse("Tab switch was initiated.")));
    }

    private async Task NewSSEConnection(HttpContextBase ctx)
    {
        try
        {
            Plugin.Log.Debug($"Client connected: {ctx.Guid}");

            var sse = new SSEConnection(HostContext.TokenSource.Token);
            await HostContext.Core.PrepareNewClient(sse);
            HostContext.EventConnections.Add(sse);

            await sse.HandleEventLoop(ctx);

            // It should always be done after return
            if (sse.Done)
                HostContext.EventConnections.Remove(sse);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to finish the server event function");
        }
    }
    #endregion

    #region RedirectHelper
    public static async Task<bool> Redirect(HttpContextBase ctx, string location, params (string, string)[] parameter)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (var (key, value) in parameter)
            query.Add(key, value);

        ctx.Response.Headers.Add("Location", $"{location}?{query}");
        ctx.Response.StatusCode = 303;
        return await ctx.Response.Send();
    }
    #endregion

    #region PreChecks

    /// <summary>
    /// Check that the request has the correct media type that the functions expects.
    /// </summary>
    /// <param name="ctx"></param>
    /// <param name="requiredMediaType"></param>
    /// <returns>True if media type is correct, otherwise handled and false</returns>
    private async Task<bool> EnforceMediaType(HttpContextBase ctx, string requiredMediaType)
    {
        if (ctx.Request.ContentType == requiredMediaType)
            return true;

        ctx.Response.StatusCode = 415;
        await ctx.Response.Send(JsonConvert.SerializeObject(new ErrorResponse("Request contains wrong media type.")));
        return false;
    }

    #endregion
}