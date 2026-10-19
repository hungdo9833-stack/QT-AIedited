using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AddressingCpuSignalTests
{
    private static CensusSource Source(string text) => new(Guid.NewGuid(), 1, text);

    [Fact]
    public void Name_variants_are_generated_kept_only_when_present_and_never_counted_inside_the_full_name()
    {
        var text = "王秀兰来了。秀兰笑了。小兰点头。兰儿走了。王小姐好。王秀兰又来了。王秀兰说话。";
        var group = NameVariantSurvey.Analyze([Source(text)], ["王秀兰"], minimumOccurrences: 1).Single();
        Assert.Equal("王", group.Surname); Assert.Equal("秀兰", group.GivenName);
        var forms = group.Variants.ToDictionary(v => v.Form, v => v.Occurrences);
        Assert.Equal(1, forms["秀兰"]); // The three 秀兰 inside 王秀兰 are not variant uses.
        Assert.True(forms.ContainsKey("小兰") && forms.ContainsKey("兰儿") && forms.ContainsKey("王小姐"));
        Assert.False(forms.ContainsKey("阿兰")); Assert.False(forms.ContainsKey("兰兰")); // Absent from the source.
    }

    [Fact]
    public void A_form_two_characters_could_own_is_shared_not_assigned_and_full_names_are_never_variants()
    {
        var text = "王秀兰和李秀兰都在。小兰来了。小兰走了。秀兰哭了。秀兰笑了。王兰也在。王兰走了。";
        var groups = NameVariantSurvey.Analyze([Source(text)], ["王秀兰", "李秀兰", "王兰"]).ToDictionary(g => g.FullName);
        Assert.Contains("小兰", groups["王秀兰"].SharedForms); Assert.Contains("秀兰", groups["李秀兰"].SharedForms);
        Assert.DoesNotContain(groups["王秀兰"].Variants, v => v.Form is "小兰" or "秀兰");
        Assert.DoesNotContain(groups.Values.SelectMany(g => g.Variants), v => v.Form == "王兰"); // Another person's full name.
    }

    [Fact]
    public void Narrative_pronoun_follows_the_name_only_outside_quotes_and_before_another_name()
    {
        string text = string.Concat(
            "    王秀兰点点头，她转身走了。\n",            // 她 → 王秀兰
            "    林青看着王秀兰，她笑了。\n",              // nearest name before 她 is 王秀兰; 林青 gets nothing
            "    王秀兰说道：「她不会来的。」\n",           // pronoun inside dialogue is not narration
            "    林青站着。其他人走了。他们都笑了。\n",      // 其他 / 他们 are not 他
            "    林青愣住了。\n    他叹了口气。\n");           // next sentence, same paragraph? no: new paragraph → not counted
        var result = NarrativeReferenceSurvey.Analyze([Source(text)], ["王秀兰", "林青"]).ToDictionary(x => x.Name);
        Assert.Equal(1, result["王秀兰"].She); Assert.Equal(0, result["王秀兰"].He);
        Assert.Equal(0, result["林青"].He); Assert.Equal(0, result["林青"].She);
        Assert.Equal(3, result["林青"].NarrationMentions); // lines 2, 4 and 5
        Assert.Equal("unknown", result["王秀兰"].SourceGender); // Fewer than 5 hits is not a clear signal.
    }

    [Fact]
    public void Kinship_term_in_between_means_the_pronoun_may_be_someone_else_real_corpus_noise()
    {
        string text = string.Concat(Enumerable.Repeat(
            "    柳轻锋看着妈妈，她笑了。\n    柳轻锋叹了口气，他走了。\n    柳轻锋看着姐姐。\n    她哭了。\n", 3));
        var hero = NarrativeReferenceSurvey.Analyze([Source(text)], ["柳轻锋"]).Single();
        Assert.Equal(0, hero.She); Assert.Equal(3, hero.He);
    }

    [Fact]
    public void Next_sentence_in_the_same_paragraph_counts_and_clear_majority_sets_source_gender()
    {
        string text = string.Concat(Enumerable.Repeat("    林青站了起来。他走向门口。\n", 6));
        var hero = NarrativeReferenceSurvey.Analyze([Source(text)], ["林青"]).Single();
        Assert.Equal(6, hero.He); Assert.Equal("male", hero.SourceGender);
        Assert.All(hero.Samples, s => Assert.Equal('他', s.FollowingPronoun));
    }
}
