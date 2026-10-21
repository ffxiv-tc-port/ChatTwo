using System.Numerics;
using ChatTwo.Code;
using ChatTwo.GameFunctions.Types;
using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

using Action = System.Action;
using DalamudPartyFinderPayload = Dalamud.Game.Text.SeStringHandling.Payloads.PartyFinderPayload;
using ChatTwoPartyFinderPayload = ChatTwo.Util.PartyFinderPayload;

namespace ChatTwo.Ui.Handler;

public sealed class PayloadHandler
{
    private readonly string PopupId = "chat2-context-popup";

    private InputHandler InputHandler { get; }
    private (Chunk, Payload?)? Popup { get; set; }

    public bool HandleTooltips;
    public uint HoveredItem;
    public uint HoverCounter;
    public uint LastHoverCounter;

    private const uint PopupSfx = 1;

    // Draw thread only. Sheet text of the tooltip or popup on screen, turned into chunks once and reused every frame
    // while it shows the same payload. Keyed by payload object and by site: one item payload has a different name
    // in its tooltip and in its popup.
    private object? TextCacheOwner;
    private int TextCacheSite;
    private List<Chunk>? CachedName;
    private List<Chunk>? CachedDescription;

    private const int SiteStatusHover = 1;
    private const int SiteItemHover = 2;
    private const int SiteEventItemHover = 3;
    private const int SiteItemPopup = 4;
    private const int SiteEventItemPopup = 5;

    private void UseTextCache(object owner, int site)
    {
        if (ReferenceEquals(owner, TextCacheOwner) && site == TextCacheSite)
            return;

        TextCacheOwner = owner;
        TextCacheSite = site;
        CachedName = null;
        CachedDescription = null;
    }

    private static List<Chunk> SheetChunks(SeString text) => ChunkUtil.ToChunks(text, ChunkSource.None, null).ToList();

    public PayloadHandler(InputHandler inputHandler)
    {
        InputHandler = inputHandler;
        PopupId += inputHandler.InputHandlerId;
    }

    public void Draw()
    {
        DrawPopups();

        if (HandleTooltips && ++HoverCounter - LastHoverCounter > 1)
        {
            GameFunctions.GameFunctions.CloseItemTooltip();
            HoveredItem = 0;
            HoverCounter = LastHoverCounter = 0;
            HandleTooltips = false;
        }
    }

    private void DrawPopups()
    {
        if (Popup == null)
            return;

        var (chunk, payload) = Popup.Value;

        using var popup = ImRaii.Popup(PopupId);
        if (!popup.Success)
        {
            Popup = null;
            return;
        }

        using var id = ImRaii.PushId(PopupId);
        var drawn = false;
        switch (payload)
        {
            case PlayerPayload player:
                DrawPlayerPopup(chunk, player);
                drawn = true;
                break;
            case ItemPayload item:
                DrawItemPopup(item);
                drawn = true;
                break;
            case UriPayload uri:
                DrawUriPopup(uri);
                drawn = true;
                break;
            case StatusPayload status:
                DrawStatusPopup(status);
                drawn = true;
                break;
        }

        ContextFooter(drawn, chunk);
        Integrations(chunk, payload);
    }

    private void Integrations(Chunk chunk, Payload? payload)
    {
        var registered = InputHandler.Plugin.Ipc.Registered;
        if (registered.Count == 0)
            return;

        ImGui.Separator();

        var contentId = chunk.Message?.ContentId ?? 0;
        var sender = chunk.Message?.Sender.Select(c => c.Link).FirstOrDefault(p => p is PlayerPayload) as PlayerPayload;

        using var menu = ImRaii.Menu(Language.Context_Integrations);
        if (!menu.Success)
            return;

        var cursor = ImGui.GetCursorPos();
        foreach (var id in registered)
        {
            try
            {
                InputHandler.Plugin.Ipc.Invoke(id, sender, contentId, payload, chunk.Message?.SenderSource, chunk.Message?.ContentSource);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error executing integration");
            }
        }

        if (cursor == ImGui.GetCursorPos())
        {
            using var pushedColor = ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.Text("No integrations available");
        }
    }

    private void ContextFooter(bool didCustomContext, Chunk chunk)
    {
        ImRaii.IEndObject? menu = default;
        if (didCustomContext)
        {
            ImGui.Separator();

            // Only place these menu items in a submenu if we've already drawn
            // custom context menu items based on the payload.
            //
            // It makes it much more convenient in the majority of cases to
            // copy the message content without having to open a submenu.
            menu = ImRaii.Menu(Plugin.PluginName);
            if (!menu.Success)
                return;
        }

        // ScreenshotMode changed, so we inform the webinterface about the new message format
        if (ImGui.Checkbox(Language.Context_ScreenshotMode, ref PlayerUtil.ScreenshotMode))
            InputHandler.Plugin.ServerCore.SendBulkMessageList();

        if (ImGui.Selectable(Language.Context_HideChat))
            InputHandler.MainWindow.CurrentHideState = HideState.User;

        if (chunk.Message is { } message)
        {
            if (ImGui.Selectable(Language.Context_Copy))
            {
                ImGui.SetClipboardText(StringifyMessage(message, true));
                WrapperUtil.AddNotification(Language.Context_CopySuccess, NotificationType.Info);
            }

            // Only show a separate "Copy content" option if the message has
            // Sender chunks, so it doesn't show for system messages.
            if (message.Sender.Count > 0 && ImGui.Selectable(Language.Context_CopyContent))
            {
                ImGui.SetClipboardText(StringifyMessage(message));
                WrapperUtil.AddNotification(Language.Context_CopyContentSuccess, NotificationType.Info);
            }

            using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int) ImGuiCol.TextDisabled]))
                ImGui.TextUnformatted(message.Code.Type.DisplayName());

            DrawChannelControls(message);
        }

        // TC note: unlike the old ImRaii.MenuDisposable struct (default-safe no-op Dispose),
        // ImRaii.IEndObject is an interface, so `default` is a real null reference - guard the
        // call since `menu` is left at its default value when didCustomContext is false.
        menu?.Dispose();
    }

    /// <summary>
    /// Per-tab channel controls. The one-click item turns off the channel this message arrived
    /// on; the submenu exists so a channel can be turned back on without opening the settings
    /// window and hunting through the channel tree.
    /// </summary>
    private void DrawChannelControls(Message message)
    {
        var tab = InputHandler.MainWindow.ContextTab;
        if (tab == null)
            return;

        var type = message.Code.Type;
        var name = type.DisplayName();
        var tellTab = tab.Channel == InputChannel.Tell && tab.TellTarget.IsSet();

        // SelectedChannels is bypassed entirely for tell-target tabs (Tab.Matches), and for GM
        // messages and ExtraChat linkshells (Message.Matches). Removing an entry in any of those
        // cases would report success and change nothing, so grey the item out and say why.
        string? blocked = null;
        if (tellTab)
            blocked = Language.Options_Tabs_TellTabChannelSelection;
        else if (type.IsGm())
            blocked = Language.Context_ChannelAlwaysShown;
        else if (message.ExtraChatChannel != Guid.Empty)
            blocked = Language.Context_ChannelExtraChat;
        else if (!tab.SelectedChannels.ContainsKey(type))
            blocked = string.Format(Language.Context_ChannelAlreadyHidden, name);

        ImGui.Separator();

        using (ImRaii.Disabled(blocked != null))
        {
            if (ImGui.Selectable(string.Format(Language.Context_ChannelDisable, name)) && blocked == null)
            {
                tab.SelectedChannels.Remove(type);

                // The settings window holds a snapshot of the tab list taken when it opened and
                // copies it back wholesale on Save, so this edit has to land in that snapshot too
                // or saving settings would silently undo it. See SettingsWindow.MirrorTabEdit.
                InputHandler.Plugin.SettingsWindow.MirrorTabEdit(tab.Identifier, mirror => mirror.SelectedChannels.Remove(type));

                InputHandler.Plugin.SaveConfig();

                // Also drop what is already on screen, otherwise it looks like nothing happened
                // until the hidden messages scroll off the top.
                tab.Messages.RemoveWhere(m => m.Code.Type == type);

                WrapperUtil.AddNotification(string.Format(Language.Context_ChannelDisabledNotice, name), NotificationType.Info, false);
            }
        }

        if (blocked != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked);

        if (tellTab)
        {
            // Wrapping a menu in BeginDisabled is ambiguous (the popup would inherit the disabled
            // item flags), so show a plain greyed row carrying the reason instead of a dead
            // submenu the user can hover but never open.
            using (ImRaii.Disabled(true))
                ImGui.Selectable(Language.Context_ChannelMenu);

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Language.Options_Tabs_TellTabChannelSelection);

            return;
        }

        DrawChannelToggles(tab);
    }

    private void DrawChannelToggles(Tab tab)
    {
        using var menu = ImRaii.Menu(Language.Context_ChannelMenu);
        if (!menu.Success)
            return;

        // Reuse the settings window's grouping (ChatTypeExt.SortOrder) instead of enumerating the
        // ChatType enum: it is the same user-facing channels, in the same order, under the same
        // four headers, so this list and the settings list cannot drift apart. Enumerating the
        // enum would produce 89 entries including GM and ExtraChat types that this cannot affect.
        foreach (var (header, types) in ChatTypeExt.SortOrder)
        {
            var selectable = types.Where(channel => !channel.IsGm()).ToArray();
            if (selectable.Length == 0)
                continue;

            var enabledCount = selectable.Count(tab.SelectedChannels.ContainsKey);

            using var pushedId = ImRaii.PushId(header);

            // ### keeps the submenu's ImGui id stable while the counter in its label changes -
            // without it, ticking a box would change the id and close the menu being worked in.
            using var group = ImRaii.Menu($"{header} ({enabledCount}/{selectable.Length})###group");
            if (!group.Success)
                continue;

            foreach (var channel in selectable)
            {
                // A checkbox rather than a MenuItem: MenuItem closes the whole popup stack when
                // clicked, which would mean one right-click per channel.
                var enabled = tab.SelectedChannels.ContainsKey(channel);
                if (!ImGui.Checkbox($"{channel.DisplayName()}##chan-{(ushort) channel}", ref enabled))
                    continue;

                if (enabled)
                {
                    tab.SelectedChannels[channel] = (ChatSourceExt.All, ChatSourceExt.All);

                    // Back-fill the messages this tab had been filtering out. Deferred so a burst
                    // of ticks doesn't queue one SQLite pass per click.
                    InputHandler.Plugin.DeferredFilterFrames = 30;
                }
                else
                {
                    tab.SelectedChannels.Remove(channel);
                    tab.Messages.RemoveWhere(message => message.Code.Type == channel);
                }

                // Same reason as the one-click disable above: keep the settings window's snapshot
                // in step so pressing Save there doesn't revert this toggle.
                var nowEnabled = enabled;
                InputHandler.Plugin.SettingsWindow.MirrorTabEdit(tab.Identifier, mirror =>
                {
                    if (nowEnabled)
                        mirror.SelectedChannels[channel] = (ChatSourceExt.All, ChatSourceExt.All);
                    else
                        mirror.SelectedChannels.Remove(channel);
                });

                InputHandler.Plugin.SaveConfig();
            }
        }
    }

    private static string StringifyMessage(Message? message, bool withSender = false)
    {
        if (message == null)
            return string.Empty;

        var chunks = withSender ? message.Sender.Concat(message.Content) : message.Content;
        return chunks.Where(chunk => chunk is TextChunk)
            .Cast<TextChunk>()
            .Select(text => text.Content)
            .Aggregate(string.Concat);
    }

    public unsafe void Click(Chunk chunk, Payload? payload, ImGuiMouseButton button)
    {
        if (Plugin.Config.PlaySounds)
            UIGlobals.PlaySoundEffect(PopupSfx);

        switch (button)
        {
            case ImGuiMouseButton.Left:
                LeftClickPayload(chunk, payload);
                break;
            case ImGuiMouseButton.Right:
                RightClickPayload(chunk, payload);
                break;
        }
    }

    public void Hover(Payload payload)
    {
        var hoverSize = 350f * ImGuiHelpers.GlobalScale;

        switch (payload)
        {
            case StatusPayload status:
                DoHover(() => HoverStatus(status), hoverSize);
                break;
            case ItemPayload item:
                if (Plugin.Config.NativeItemTooltips)
                {
                    if (!HandleTooltips || HoveredItem != item.RawItemId)
                    {
                        HandleTooltips = true;
                        HoveredItem = item.RawItemId;
                        HoverCounter = LastHoverCounter = 0;

                        GameFunctions.GameFunctions.OpenItemTooltip(item.RawItemId, item.Kind);
                    }
                    else
                    {
                        LastHoverCounter = HoverCounter;
                    }

                    return;
                }

                DoHover(() => HoverItem(item), hoverSize);
                break;
            case UriPayload uri:
                DoHover(() => HoverUri(uri), hoverSize);
                break;
        }
    }

    private void DoHover(Action inside, float width)
    {
        ImGui.SetNextWindowSize(new Vector2(width, -1f));

        using (ImRaii.Tooltip())
        using (ImRaii.TextWrapPos(0.0f))
        using (ImRaii.PushColor(ImGuiCol.Text, InputHandler.Plugin.DefaultText))
            inside();
    }

    private static void InlineIcon(IDalamudTextureWrap icon)
    {
        var cursor = ImGui.GetCursorPos();
        const int maxIconSize = 32;
        // Keep the icons aspect ratio while also shrinking it down so its at most 32px wide/tall
        var iconRatio = icon.Size.X / icon.Size.Y;
        var x = Math.Min(maxIconSize, (int) (maxIconSize * iconRatio));
        var y = Math.Min(maxIconSize, (int) (maxIconSize / iconRatio));
        var size = ImGuiHelpers.ScaledVector2(x, y);

        ImGui.Image(icon.Handle, size);
        ImGui.SameLine();
        ImGui.SetCursorPos(cursor + new Vector2(size.X + 4, size.Y - ImGui.GetTextLineHeightWithSpacing()));
    }

    private void HoverStatus(StatusPayload status)
    {
        if (Plugin.TextureProvider.GetFromGameIcon(status.Status.Value.Icon).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        UseTextCache(status, SiteStatusHover);
        InputHandler.ChunkHandler.DrawChunks(CachedName ??= SheetChunks(StatusName(status)));
        ImGui.Separator();

        InputHandler.ChunkHandler.DrawChunks(CachedDescription ??= SheetChunks(status.Status.Value.Description.ToDalamudString()));
    }

    private static SeString StatusName(StatusPayload status)
    {
        var builder = new SeStringBuilder();
        var nameValue = status.Status.Value.Name.ToString();
        switch (status.Status.Value.StatusCategory)
        {
            case 1:
                builder.AddUiForeground($"{SeIconChar.Buff.ToIconString()}{nameValue}", 517);
                break;
            case 2:
                builder.AddUiForeground($"{SeIconChar.Debuff.ToIconString()}{nameValue}", 518);
                break;
            default:
                builder.AddUiForeground(nameValue, 1);
                break;
        }

        return builder.BuiltString;
    }

    private void HoverItem(ItemPayload item)
    {
        if (item.Kind == ItemKind.EventItem)
        {
            HoverEventItem(item);
            return;
        }

        if (!item.Item.TryGetValue(out Item resolvedItem))
            return;

        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(resolvedItem.Icon, item.IsHQ)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        UseTextCache(item, SiteItemHover);
        InputHandler.ChunkHandler.DrawChunks(CachedName ??= SheetChunks(resolvedItem.Name.ToDalamudString()));
        ImGui.Separator();

        InputHandler.ChunkHandler.DrawChunks(CachedDescription ??= SheetChunks(resolvedItem.Description.ToDalamudString()));
    }

    private void HoverEventItem(ItemPayload payload)
    {
        if (!Sheets.EventItemSheet.TryGetRow(payload.RawItemId, out var itemRow))
            return;

        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(itemRow.Icon)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        UseTextCache(payload, SiteEventItemHover);
        InputHandler.ChunkHandler.DrawChunks(CachedName ??= SheetChunks(Sheets.EventItemName(itemRow).ToDalamudString()));
        ImGui.Separator();

        if (!Sheets.EventItemHelpSheet.TryGetRow(payload.RawItemId, out var itemHelpRow))
            return;

        InputHandler.ChunkHandler.DrawChunks(CachedDescription ??= SheetChunks(itemHelpRow.Description.ToDalamudString()));
    }

    private void HoverUri(UriPayload uri)
    {
        ImGui.TextUnformatted(string.Format(Language.Context_URLDomain, uri.Uri.Authority));
        ImGuiUtil.WarningText(Language.Context_URLWarning);
    }

    private void LeftClickPayload(Chunk chunk, Payload? payload)
    {
        switch (payload)
        {
            case MapLinkPayload map:
                Plugin.GameGui.OpenMapWithMapLink(map);
                break;
            case QuestPayload quest:
                GameFunctions.GameFunctions.OpenQuestLog(quest.Quest);
                break;
            case DalamudLinkPayload link:
                ClickLinkPayload(chunk, payload, link);
                break;
            case DalamudPartyFinderPayload pf:
                if (pf.LinkType == DalamudPartyFinderPayload.PartyFinderLinkType.PartyFinderNotification)
                    GameFunctions.GameFunctions.OpenPartyFinder();
                else
                    GameFunctions.GameFunctions.OpenPartyFinder(pf.ListingId);
                break;
            case ChatTwoPartyFinderPayload pf:
                GameFunctions.GameFunctions.OpenPartyFinder(pf.Id);
                break;
            case AchievementPayload achievement:
                GameFunctions.GameFunctions.OpenAchievement(achievement.Id);
                break;
            case RawPayload raw:
                if (Equals(raw, ChunkUtil.PeriodicRecruitmentLink))
                    GameFunctions.GameFunctions.OpenPartyFinder();
                break;
            case UriPayload uri:
                WrapperUtil.TryOpenUri(uri.Uri);
                break;
            default:
                RightClickPayload(chunk, payload);
                break;
        }
    }

    private void ClickLinkPayload(Chunk chunk, Payload payload, DalamudLinkPayload link)
    {
        if (chunk.GetSeString() is not { } source)
            return;

        var start = source.Payloads.IndexOf(payload);
        var end = source.Payloads.IndexOf(RawPayload.LinkTerminator, start == -1 ? 0 : start);
        if (start == -1 || end == -1)
            return;

        var payloads = source.Payloads.Skip(start).Take(end - start + 1).ToList();
        if (!Plugin.ChatGui.RegisteredLinkHandlers.TryGetValue((link.Plugin, link.CommandId), out var value))
        {
            Plugin.Log.Warning("Could not find DalamudLinkHandlers");
            return;
        }

        try
        {
            // Running XivCommon SendChat instantly, without RunOnTick, leads to a game freeze, for whatever reason
            Plugin.Framework.RunOnTick(() => value.Invoke(link.CommandId, new SeString(payloads)));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error executing DalamudLinkPayload handler");
        }
    }

    private void RightClickPayload(Chunk chunk, Payload? payload)
    {
        Popup = (chunk, payload);
        ImGui.OpenPopup(PopupId);
    }

    private void DrawItemPopup(ItemPayload payload)
    {
        if (payload.Kind == ItemKind.EventItem)
        {
            DrawEventItemPopup(payload);
            return;
        }

        if (!Sheets.ItemSheet.TryGetRow(payload.ItemId, out var itemRow))
            return;

        var hq = payload.Kind == ItemKind.Hq;
        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(itemRow.Icon, hq)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        UseTextCache(payload, SiteItemPopup);
        InputHandler.ChunkHandler.DrawChunks(CachedName ??= SheetChunks(ItemPopupName(itemRow, payload)), false);
        ImGui.Separator();

        var realItemId = payload.RawItemId;
        if (itemRow.EquipSlotCategory.RowId != 0)
        {
            if (ImGui.Selectable(Language.Context_TryOn))
                GameFunctions.Context.TryOn(realItemId, 0);

            if (ImGui.Selectable(Language.Context_ItemComparison))
                GameFunctions.Context.OpenItemComparison(realItemId);
        }

        if (itemRow.ItemSearchCategory.Value.Category == 3)
            if (ImGui.Selectable(Language.Context_SearchRecipes))
                GameFunctions.Context.SearchForRecipesUsingItem(payload.ItemId);

        if (ImGui.Selectable(Language.Context_SearchForItem))
            GameFunctions.Context.SearchForItem(realItemId);

        if (ImGui.Selectable(Language.Context_Link))
            GameFunctions.Context.LinkItem(realItemId);

        if (ImGui.Selectable(Language.Context_CopyItemName))
            ImGui.SetClipboardText(ItemPopupName(itemRow, payload).TextValue);
    }

    private static SeString ItemPopupName(Item itemRow, ItemPayload payload)
    {
        var name = itemRow.Name.ToDalamudString();
        // hq symbol
        if (payload.Kind == ItemKind.Hq)
            name.Payloads.Add(new TextPayload(" "));
        else if (payload.Kind == ItemKind.Collectible)
            name.Payloads.Add(new TextPayload(" "));

        return name;
    }

    private void DrawEventItemPopup(ItemPayload payload)
    {
        if (payload.Kind != ItemKind.EventItem)
            return;

        if (!Sheets.EventItemSheet.HasRow(payload.ItemId))
            return;

        var item = Sheets.EventItemSheet.GetRow(payload.ItemId);
        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Icon)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        UseTextCache(payload, SiteEventItemPopup);
        InputHandler.ChunkHandler.DrawChunks(CachedName ??= SheetChunks(Sheets.EventItemName(item).ToDalamudString()), false);
        ImGui.Separator();

        var realItemId = payload.RawItemId;
        if (ImGui.Selectable(Language.Context_Link))
            GameFunctions.Context.LinkItem(realItemId);

        if (ImGui.Selectable(Language.Context_CopyItemName))
            ImGui.SetClipboardText(Sheets.EventItemName(item).ToString());
    }

    private void DrawPlayerPopup(Chunk chunk, PlayerPayload player)
    {
        // Possible that GMs return a null payload
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (player == null)
            return;

        var world = player.World;
        if (chunk.Message?.Code.Type == ChatType.FreeCompanyLoginLogout)
            if (Plugin.PlayerState.HomeWorld.IsValid)
                world = Plugin.PlayerState.HomeWorld;

        // Naming the world (header, /tell prefill) needs no native call, so TC worlds are let through
        // here; of the block of actions further down they only get the two text-command entries.
        var worldIsNameable = Sheets.IsPublicOrTaiwan(world.Value);
        var name = new List<Chunk> { new TextChunk(ChunkSource.None, null, player.PlayerName) };
        if (worldIsNameable)
        {
            name.AddRange([
                new IconChunk(ChunkSource.None, null, BitmapFontIcon.CrossWorld),
                new TextChunk(ChunkSource.None, null, world.Value.Name.ToString())
            ]);
        }

        InputHandler.ChunkHandler.DrawChunks(name, false);
        ImGui.Separator();

        var validContentId = chunk.Message?.ContentId is not (null or 0);
        if (ImGui.Selectable(Language.Context_SendTell))
        {
            // Eureka, Bozja and Occult need special handling as tells work different
            if (!Sheets.IsInForay())
            {
                InputHandler.ChatInput = $"/tell {player.PlayerName}";
                if (worldIsNameable)
                    InputHandler.ChatInput += $"@{world.Value.Name}";

                InputHandler.ChatInput += " ";
            }
            else if (validContentId)
            {
                InputHandler.Plugin.Functions.Chat.SetEurekaTellChannel(player.PlayerName, world.Value.Name.ToString(), (ushort) world.RowId, 0, chunk.Message!.ContentId, 0, false);
            }

            InputHandler.Activate = true;
        }

        var scope = PlayerMenuRouting.Scope(world.Value.IsPublic, world.RowId);
        if (scope == PlayerMenuScope.Full)
        {
            var party = Plugin.PartyList;
            var leader = party[(int) party.PartyLeaderIndex]?.ContentId;
            // TC note: IPartyMember.ContentId is a plain `long` at true API13 (later API
            // versions widened it to ulong) - cast to compare against Plugin.PlayerState's ulong.
            var isLeader = party.Length == 0 || Plugin.PlayerState.ContentId == (ulong?) leader;
            var member = party.FirstOrDefault(member => member.Name.TextValue == player.PlayerName && member.World.RowId == world.RowId);
            var isInParty = member != null;
            var inInstance = GameFunctions.GameFunctions.IsInInstance();
            var inPartyInstance = Sheets.TerritorySheet.GetRow(Plugin.ClientState.TerritoryType).TerritoryIntendedUse.RowId is 41 or 47 or 48 or 52 or 53 or 61;
            if (isLeader)
            {
                if (!isInParty)
                {
                    if (inInstance && inPartyInstance)
                    {
                        if (validContentId && ImGui.Selectable(Language.Context_InviteToParty))
                            GameFunctions.Party.InviteInInstance(chunk.Message!.ContentId);
                    }
                    else if (!inInstance)
                    {
                        using var menu = ImRaii.Menu(Language.Context_InviteToParty);
                        if (menu.Success)
                        {
                            if (ImGui.Selectable(Language.Context_InviteToParty_SameWorld))
                                GameFunctions.Party.InviteSameWorld(player.PlayerName, (ushort)world.RowId, chunk.Message?.ContentId ?? 0);

                            if (validContentId && ImGui.Selectable(Language.Context_InviteToParty_DifferentWorld))
                                GameFunctions.Party.InviteOtherWorld(chunk.Message!.ContentId, (ushort)world.RowId);
                        }
                    }
                }

                if (isInParty && member != null && (!inInstance || (inInstance && inPartyInstance)))
                {
                    if (ImGui.Selectable(Language.Context_Promote))
                        GameFunctions.Party.Promote(player.PlayerName, (ulong) member.ContentId);

                    if (ImGui.Selectable(Language.Context_KickFromParty))
                        GameFunctions.Party.Kick(player.PlayerName, (ulong) member.ContentId);
                }
            }

            var isFriend = GameFunctions.GameFunctions.GetFriends().Any(friend => friend.NameString == player.PlayerName && friend.HomeWorld == world.RowId);
            if (!isFriend && ImGui.Selectable(Language.Context_SendFriendRequest))
                InputHandler.Plugin.Functions.SendFriendRequest(player.PlayerName, (ushort) world.RowId);

            using (var menuBlockFunctions = ImRaii.Menu(Language.Context_BlockFunctions))
            {
                if (menuBlockFunctions.Success)
                {
                    if (ImGui.Selectable(Language.Context_AddToBlacklist))
                        InputHandler.Plugin.Functions.AddToBlacklist(player.PlayerName, (ushort)world.RowId);

                    if (chunk.Message != null)
                    {
                        var message = chunk.Message;

                        if (message.AccountId != 0 && ImGui.Selectable(Language.Context_AddToMuteList))
                            InputHandler.Plugin.Functions.AddToMuteList(message.AccountId, message.ContentId, player.PlayerName, (short) world.RowId);

                        if (ImGui.Selectable(Language.Context_AddToTermsFilter))
                            InputHandler.Plugin.Functions.AddToTermsList(message.ContentSource);
                    }
                }
            }

            if (GameFunctions.GameFunctions.IsMentor() && ImGui.Selectable(Language.Context_InviteToNoviceNetwork))
                GameFunctions.Context.InviteToNoviceNetwork(player.PlayerName, (ushort) world.RowId);
        }
        else if (scope == PlayerMenuScope.TextCommandsOnly)
        {
            // Deliberately no "already a friend" lookup here: the friend list read has never run on the
            // TC client, and the game answers a duplicate request on its own.
            if (ImGui.Selectable(Language.Context_SendFriendRequest))
                InputHandler.Plugin.Functions.SendFriendRequest(player.PlayerName, (ushort) world.RowId);

            using var menuBlockFunctions = ImRaii.Menu(Language.Context_BlockFunctions);
            if (menuBlockFunctions.Success && ImGui.Selectable(Language.Context_AddToBlacklist))
                InputHandler.Plugin.Functions.AddToBlacklist(player.PlayerName, (ushort) world.RowId);
        }

        var inputChannel = chunk.Message?.Code.Type.ToInputChannel();
        if (inputChannel != null && ImGui.Selectable(Language.Context_ReplyInSelectedChatMode))
        {
            InputHandler.Plugin.Functions.Chat.SetChannelWithExtraChat(inputChannel.Value);
            InputHandler.Activate = true;
        }

        if (ImGui.Selectable(Language.Context_Target) && FindCharacterForPayload(player) is { } obj)
            Plugin.TargetManager.Target = obj;

        if (validContentId && ImGui.Selectable(Language.Context_AdventurerPlate))
            if (!GameFunctions.GameFunctions.TryOpenAdventurerPlate(chunk.Message!.ContentId))
                WrapperUtil.AddNotification(Language.Context_AdventurerPlateError, NotificationType.Warning);
    }

    // The returned wrapper is only good for the caller's immediate use: the object table rewrites
    // these wrappers in place, so it must never be kept across frames.
    private IPlayerCharacter? FindCharacterForPayload(PlayerPayload payload)
    {
        IPlayerCharacter? nameOnly = null;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is not IPlayerCharacter character)
                continue;

            if (character.Name.TextValue != payload.PlayerName)
                continue;

            switch (PlayerMenuRouting.MatchNamedTarget(payload.World.Value.IsPublic, payload.World.RowId, character.HomeWorld.RowId))
            {
                case TargetMatch.Take:
                    return character;
                case TargetMatch.Fallback:
                    nameOnly ??= character;
                    break;
            }
        }

        return nameOnly;
    }

    private void DrawUriPopup(UriPayload uri)
    {
        ImGui.TextUnformatted(string.Format(Language.Context_URLDomain, uri.Uri.Authority));
        ImGuiUtil.WarningText(Language.Context_URLWarning, false);
        ImGui.Separator();

        if (ImGui.Selectable(Language.Context_OpenInBrowser))
            WrapperUtil.TryOpenUri(uri.Uri);

        if (ImGui.Selectable(Language.Context_CopyLink))
        {
            ImGui.SetClipboardText(uri.Uri.ToString());
            WrapperUtil.AddNotification(Language.Context_CopyLinkNotification, NotificationType.Info);
        }
    }

    private void DrawStatusPopup(StatusPayload status)
    {
        if (Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(status.Status.Value.Icon)).GetWrapOrDefault() is { } icon)
            InlineIcon(icon);

        var builder = new SeStringBuilder();
        var nameValue = status.Status.Value.Name.ToString();
        switch (status.Status.Value.StatusCategory)
        {
            case 1:
                builder.AddUiForeground($"{SeIconChar.Buff.ToIconString()}{nameValue}", 517);
                break;
            case 2:
                builder.AddUiForeground($"{SeIconChar.Debuff.ToIconString()}{nameValue}", 518);
                break;
            default:
                builder.AddUiForeground(nameValue, 1);
                break;
        }

        InputHandler.ChunkHandler.DrawChunks(ChunkUtil.ToChunks(builder.BuiltString, ChunkSource.None, null).ToList(), false);
        ImGui.Separator();

        if (ImGui.Selectable(Language.Context_Link))
        {
            GameFunctions.Context.LinkStatus(status.Status.RowId);
            InputHandler.ChatInput += " <status>";
        }
    }
}
