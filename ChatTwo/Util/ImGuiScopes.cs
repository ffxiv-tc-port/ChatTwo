using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Util;

// Allocation-free stand-ins for ImRaii on paths that run every frame (ImRaii allocates an object per push and boxes
// every end object). Use them with `using`, which disposes a struct without boxing. Not recorded in ImRaii's own
// bookkeeping (DefaultStyle / DefaultColors / Enabled), which ChatTwo never calls.

public readonly struct StyleVarScope : IDisposable
{
    // Decided once when pushing, so Dispose pops exactly what was pushed whatever happens in between.
    private readonly bool Pushed;

    public StyleVarScope(ImGuiStyleVar idx, Vector2 value, bool condition = true)
    {
        if (condition)
            ImGui.PushStyleVar(idx, value);
        Pushed = condition;
    }

    public void Dispose()
    {
        if (Pushed)
            ImGui.PopStyleVar();
    }
}

public readonly struct StyleColorScope : IDisposable
{
    // Decided once when pushing, so Dispose pops exactly what was pushed whatever happens in between.
    private readonly bool Pushed;

    public StyleColorScope(ImGuiCol idx, Vector4 color, bool condition = true)
    {
        if (condition)
            ImGui.PushStyleColor(idx, color);
        Pushed = condition;
    }

    public StyleColorScope(ImGuiCol idx, uint color, bool condition = true)
    {
        if (condition)
            ImGui.PushStyleColor(idx, color);
        Pushed = condition;
    }

    public void Dispose()
    {
        if (Pushed)
            ImGui.PopStyleColor();
    }
}

// What a Begin* call opened, closed on Dispose under the same rule as the ImRaii object of the same name: a child
// window always, everything else only when Begin returned true, a disabled block only when one was begun.
public readonly struct EndScope : IDisposable
{
    private enum Closer : byte
    {
        None,
        TabBar,
        TabItem,
        Table,
        Popup,
        Child,
        Disabled,
    }

    private readonly Closer Close;

    // What Begin returned.
    public readonly bool Success;

    private EndScope(Closer close, bool success)
    {
        Close = close;
        Success = success;
    }

    private static EndScope IfOpen(Closer close, bool open) => new(open ? close : Closer.None, open);

    public static EndScope TabBar(ImU8String label) => IfOpen(Closer.TabBar, ImGui.BeginTabBar(label));

    public static EndScope TabItem(ImU8String label, ImGuiTabItemFlags flags) => IfOpen(Closer.TabItem, ImGui.BeginTabItem(label, flags));

    public static EndScope Table(ImU8String id, int columns, ImGuiTableFlags flags) => IfOpen(Closer.Table, ImGui.BeginTable(id, columns, flags));

    public static EndScope Popup(ImU8String id) => IfOpen(Closer.Popup, ImGui.BeginPopup(id));

    public static EndScope ContextPopupItem(ImU8String id) => IfOpen(Closer.Popup, ImGui.BeginPopupContextItem(id));

    public static EndScope Child(ImU8String id, Vector2 size) => new(Closer.Child, ImGui.BeginChild(id, size));

    public static EndScope Disabled(bool disabled)
    {
        if (disabled)
            ImGui.BeginDisabled();
        return IfOpen(Closer.Disabled, disabled);
    }

    public void Dispose()
    {
        switch (Close)
        {
            case Closer.TabBar: ImGui.EndTabBar(); break;
            case Closer.TabItem: ImGui.EndTabItem(); break;
            case Closer.Table: ImGui.EndTable(); break;
            case Closer.Popup: ImGui.EndPopup(); break;
            case Closer.Child: ImGui.EndChild(); break;
            case Closer.Disabled: ImGui.EndDisabled(); break;
        }
    }
}
