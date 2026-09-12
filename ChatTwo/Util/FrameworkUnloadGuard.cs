namespace ChatTwo.Util;

/// <summary>
/// 「Dalamud 正在卸載」的閘門。轉派到 framework 執行緒之前先問這一支。
/// </summary>
/// <remarks>
/// 🔴🔴 為什麼需要這一層：<c>Framework.RunOnFrameworkThread</c> 與<b>沒有延遲的</b>
/// <c>RunOnTick</c> 在 <c>Framework.IsFrameworkUnloading</c> 為真時<b>不是排隊，而是就地在
/// 呼叫端的執行緒上執行</b>委派（本 pin <c>Dalamud/Game/Framework.cs</c>：
/// <c>RunOnFrameworkThread</c> 判的是 <c>IsInFrameworkUpdateThread || IsFrameworkUnloading</c>；
/// <c>RunOnTick</c> 在卸載期且沒有 delay／delayTicks 時直接轉呼叫它）。
/// 也就是說「丟回 framework 執行緒再碰原生資料」這層保護，在關遊戲／停用外掛那一瞬間會
/// 整個失效 —— 委派會直接在網頁介面的 HTTP 執行緒或訊息處理執行緒上解參考遊戲的原生結構。
/// 失敗形式是 <c>AccessViolationException</c>，而那在 .NET Core 是 corrupted-state exception，
/// <c>try</c>/<c>catch</c> 完全攔不到，使用者看到的是遊戲直接關掉。
/// <br/><br/>
/// 📌 <b>已經在 framework 執行緒上時一律回 false</b>（那本來就是安全的執行緒），
/// 所以非卸載期的行為與改動前逐字相同。
/// <br/><br/>
/// ⚠️ <b>帶延遲的</b> <c>RunOnTick(..., delay/delayTicks)</c> 不需要這道閘門：它在卸載期回的是
/// <c>Task.FromCanceled</c>，委派根本不會執行。
/// </remarks>
public static class FrameworkUnloadGuard
{
    /// <summary>同一則訊息的重印間隔（毫秒）。</summary>
    private const long LogIntervalMs = 10000;

    /// <summary>節流表上限，避免鍵意外發散時無限成長。</summary>
    private const int MaxTrackedKeys = 32;

    private static readonly Dictionary<string, long> LogTimes = new();

    /// <summary>
    /// 現在是不是「Dalamud 卸載期，而且我不在 framework 執行緒上」。
    /// 為真時呼叫端應該直接放棄這次要轉派給 framework 執行緒的工作。
    /// </summary>
    /// <param name="what">出現在診斷訊息裡的動作名稱，同時是節流的鍵。</param>
    public static bool ShouldSkip(string what)
    {
        if (!Plugin.Framework.IsFrameworkUnloading || Plugin.Framework.IsInFrameworkUpdateThread)
            return false;

        if (ShouldLog(what))
            Plugin.Log.Information($"Dalamud 正在卸載，已跳過「{what}」。卸載期的 RunOnFrameworkThread 會就地在呼叫端的執行緒上執行，保護不了原生記憶體存取；此時這個動作失效可以接受，遊戲崩潰不行。");

        return true;
    }

    /// <summary>
    /// 自帶的節流：首次必放行，之後每 <see cref="LogIntervalMs"/> 毫秒放行一次。
    /// 🔴 鎖內只碰字典 —— 不寫 log、不做 I/O、不呼叫任何別的東西。
    /// </summary>
    private static bool ShouldLog(string key)
    {
        var now = Environment.TickCount64;
        lock (LogTimes)
        {
            if (LogTimes.TryGetValue(key, out var last) && now - last < LogIntervalMs)
                return false;
            if (LogTimes.Count >= MaxTrackedKeys && !LogTimes.ContainsKey(key))
                LogTimes.Clear();
            LogTimes[key] = now;
            return true;
        }
    }
}
