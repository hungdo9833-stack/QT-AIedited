using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class DialogueMapBuilderTests
{
    [Fact]
    public void Speaker_named_before_the_segment_boundary_attributes_the_first_quote_and_kinship_finds_the_listener()
    {
        const string text = "林哲抬起头，眼神复杂地看着自己的母亲：\n“刚才你不是还拿规则说事吗？”\n王秀兰脸色发白。";
        var (chapter, _) = Family(text);
        int boundary = text.IndexOf('“');
        var segment = new TranslationSegment { Id = Guid.NewGuid(), Order = 2, StartUtf16 = boundary, SourceText = text[boundary..] };

        string map = DialogueMapBuilder.Build(chapter, segment);

        Assert.Contains("“刚才你不是还拿规则说事吗？”: Lâm Triết → Vương Tú Lan (self=con; address=mẹ)", map);
    }

    [Fact]
    public void Vocative_name_gives_the_listener_and_its_rule()
    {
        const string text = "王秀兰冷声道：“林哲，你过来。”";
        var (chapter, segment) = Family(text);
        Assert.Contains("Vương Tú Lan → Lâm Triết (self=mẹ; address=con)", DialogueMapBuilder.Build(chapter, segment));
    }

    [Fact]
    public void Pronoun_or_unattributed_speakers_are_left_out()
    {
        var (chapter, segment) = Family("她指着林哲，手指都在哆嗦：“你丢尽了脸！”\n“荒唐！”");
        Assert.Equal("", DialogueMapBuilder.Build(chapter, segment));
    }

    [Fact]
    public void Quotes_outside_the_segment_are_not_listed()
    {
        const string text = "王秀兰冷声道：“林哲，你过来。”\n林哲说道：“妈。”";
        var (chapter, _) = Family(text);
        int second = text.IndexOf("林哲说道", StringComparison.Ordinal);
        var first = new TranslationSegment { Id = Guid.NewGuid(), Order = 1, StartUtf16 = 0, SourceText = text[..second] };
        string map = DialogueMapBuilder.Build(chapter, first);
        Assert.Contains("Vương Tú Lan → Lâm Triết", map);
        Assert.DoesNotContain("Lâm Triết → Vương Tú Lan", map);
    }

    private static (ChapterDocument Chapter, TranslationSegment Segment) Family(string text)
    {
        var mother = new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan" };
        var son = new CharacterProfile { ChineseName = "林哲", VietnameseName = "Lâm Triết" };
        son.Relationships.Add(new CharacterRelationship { TargetCharacterId = mother.Id, ListenerCharacterId = mother.Id,
            Relationship = "con - mẹ", SelfReference = "con", AddresseeReference = "mẹ", Enabled = true });
        mother.Relationships.Add(new CharacterRelationship { TargetCharacterId = son.Id, ListenerCharacterId = son.Id,
            Relationship = "mẹ - con", SelfReference = "mẹ", AddresseeReference = "con", Enabled = true });
        var chapter = new ChapterDocument { Id = Guid.NewGuid(), ChapterNumber = 4, OriginalText = text,
            CharacterProject = new CharacterProject { Profiles = [mother, son] } };
        return (chapter, new TranslationSegment { Id = Guid.NewGuid(), Order = 1, StartUtf16 = 0, SourceText = text });
    }
}
