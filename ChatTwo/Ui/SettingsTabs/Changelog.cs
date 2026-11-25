using ChatTwo.Resources;
using ChatTwo.Util;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;

namespace ChatTwo.Ui.SettingsTabs;

public sealed class Changelog : ISettingsTab
{
    private Configuration Mutable { get; }

    public string Name => Language.Options_Changelog_Tab + "###tabs-changelog";

    public Changelog(Configuration mutable)
    {
        Mutable = mutable;
    }

    public void Draw(bool changed)
    {
        using var wrap = ImRaii.TextWrapPos(0.0f);

        ImGui.TextUnformatted(Language.Options_Warning_NotImplemented);
        // PrintChangelog 沒有讀取端（上游同）；畫複本、不寫回，使用者存的值原樣保留。
        var printChangelog = Mutable.PrintChangelog;
        using (ImRaii.Disabled())
            ImGui.Checkbox(Language.Options_PrintChangelog_Name, ref printChangelog);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGuiUtil.Tooltip(Language.Options_PrintChangelog_NoEffect);
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Language.Options_PrintChangelog_NoEffectTag);
        ImGuiUtil.HelpText(Language.Options_PrintChangelog_NoEffect);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var changelog = Plugin.Interface.Manifest.Changelog;
        if (changelog != null)
        {
            ImGui.TextUnformatted(Language.Options_Changelog_Header);
            ImGui.TextUnformatted($"Version {Plugin.Interface.Manifest.AssemblyVersion.ToString(3)}");
            ImGui.Spacing();
            foreach (var sentence in changelog.Split("\n"))
            {
                if (sentence == string.Empty)
                {
                    ImGui.NewLine();
                    continue;
                }

                var condition = sentence.StartsWith('-') || sentence.StartsWith("  -");
                using var indent = ImRaii.PushIndent(10.0f, true, condition);
                ImGui.TextUnformatted(sentence);
            }
        }
        ImGui.Spacing();
    }
}
