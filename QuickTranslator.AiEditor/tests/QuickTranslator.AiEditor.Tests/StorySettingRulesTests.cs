using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class StorySettingRulesTests
{
    [Fact]
    public void Unspecified_setting_adds_nothing()
    {
        var project = new CharacterProject();
        Assert.Equal("", StorySettingRules.PromptBlock(project));
        Assert.Empty(StorySettingRules.CheckDialogue(project, "“Ngươi điên rồi!”"));
    }

    [Fact]
    public void Modern_prompt_bans_archaic_terms_except_those_a_project_rule_uses()
    {
        var project = Modern();
        Assert.Contains("ngươi", StorySettingRules.PromptBlock(project));
        Assert.Contains(", ta,", StorySettingRules.PromptBlock(project));

        var father = new CharacterProfile { VietnameseName = "Lâm Kiến Quốc" };
        var son = new CharacterProfile { VietnameseName = "Lâm Triết" };
        father.Relationships.Add(new CharacterRelationship { TargetCharacterId = son.Id, ListenerCharacterId = son.Id,
            Relationship = "bố - con", SelfReference = "ta", AddresseeReference = "con", Enabled = true });
        project.Profiles.AddRange([father, son]);
        Assert.DoesNotContain(", ta,", StorySettingRules.PromptBlock(project));
        Assert.Empty(StorySettingRules.CheckDialogue(project, "“Ta không đồng ý.”"));
    }

    [Fact]
    public void Modern_check_flags_archaic_dialogue_only()
    {
        var issues = StorySettingRules.CheckDialogue(Modern(),
            "Ngươi là kẻ ngốc, bà nghĩ. “Tô Vũ! Ngươi thật vô liêm sỉ!” “Chúng ta đi thôi, người ta đang nhìn.” “Anh ta nói gì?”");
        var issue = Assert.Single(issues);
        Assert.Equal(IssueCode.ArchaicAddressing, issue.Code);
        Assert.Contains("“Ngươi”", issue.Message);
    }

    [Fact]
    public void Modern_check_flags_standalone_ta_and_reports_each_term_once()
    {
        var issues = StorySettingRules.CheckDialogue(Modern(), "“Ta nói rồi, ngươi đi đi.” “Ngươi nghe chưa?”");
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void Historical_setting_never_warns()
    {
        var project = new CharacterProject { Setting = StorySetting.Historical };
        Assert.Contains("historical", StorySettingRules.PromptBlock(project));
        Assert.Empty(StorySettingRules.CheckDialogue(project, "“Ngươi là ai?”"));
    }

    private static CharacterProject Modern() => new() { Setting = StorySetting.Modern };
}
