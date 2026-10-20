using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>
/// "Hoàn thành": every verified proposal is written in one click (the product is an automatic translator), nothing locked
/// or already decided is overwritten, the newest answer wins, and the whole completion can be undone. Mock proposals only.
/// </summary>
public sealed class AddressingCompletionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-complete-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static Task<ProjectResources> Load(ProjectSession s) => new ProjectResourceStore(s).LoadAsync();
    private static Task<ProjectResources> Save(ProjectSession s, ProjectResources r) => new ProjectResourceStore(s).SaveAsync(r);
    private static Task<AddressingLedgerState> Edit(ProjectSession s, Action<AddressingLedgerState> edit) => new AddressingLedgerStore(s).UpdateAsync(edit);
    private static int Index(AddressingLedgerState s, AddressingTaskKind kind, string subject) => s.Proposals.FindIndex(p => p.Kind == kind && p.Subject == subject);

    [Fact]
    public async Task One_click_writes_every_character_alias_narrative_pronoun_and_both_pair_directions_and_turns_translation_on()
    {
        var (session, service, _) = await AddressingReviewTests.Seed(_root);
        var preview = await service.PreviewCompletionAsync();
        Assert.Equal(4, preview.Applied.Count); Assert.Empty(preview.Held);
        Assert.Equal(0, (await Load(session)).Revision); // A preview writes nothing.

        var done = await service.CompleteAsync((await Load(session)).Revision);
        Assert.Equal(4, done.Applied.Count); Assert.NotNull(done.ApplyId);
        var heroine = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
        var hero = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "林青");
        Assert.Equal("Vương Tú Lan", heroine.VietnameseName); Assert.Equal(CharacterGender.Female, heroine.Gender);
        Assert.Equal("nàng", heroine.PrimaryNarrativePronoun); Assert.True(heroine.NarrativePronounLocked);
        Assert.Contains("小兰", heroine.Aliases); Assert.Contains(done.Resources.Terms, t => t.Chinese == "小兰" && t.Vietnamese == "Tiểu Lan");
        Assert.Equal("hắn", hero.PrimaryNarrativePronoun);
        Assert.Contains(heroine.Relationships, r => r.EffectiveListenerCharacterId == hero.Id && r.SelfReference == "chị" && r.AddresseeReference == "em" && r.Locked);
        Assert.Contains(hero.Relationships, r => r.EffectiveListenerCharacterId == heroine.Id && r.SelfReference == "em" && r.AddresseeReference == "chị");
        Assert.True(done.Resources.Characters.ConsistencyLockEnabled); // "Hoàn thành" = translate with these rules.
        var block = new CharacterConsistencyRuleSelector().Select(done.Resources.Characters, "王秀兰看着林青说道：「吃饭了。」", 1).PromptBlock;
        Assert.Contains("self = chị; address = em", block); Assert.Contains("narrative pronoun = nàng", block);

        var ledger = await service.LoadAsync();
        Assert.All(done.Applied, a => Assert.Equal(AddressingDecision.Accepted, ledger.LatestReview(a.ProposalId)!.Decision));
        var again = await service.CompleteAsync(done.Resources.Revision);
        Assert.Empty(again.Applied); Assert.Null(again.ApplyId); Assert.Equal(done.Resources.Revision, (await Load(session)).Revision); // Idempotent.
    }

    [Fact]
    public async Task Undo_restores_the_previous_project_exactly_and_makes_the_proposals_pending_again()
    {
        var (session, service, _) = await AddressingReviewTests.Seed(_root);
        var before = await Load(session);
        Assert.False(await service.CanUndoCompletionAsync());
        var done = await service.CompleteAsync(before.Revision);
        Assert.True(await service.CanUndoCompletionAsync());
        var undone = await service.UndoCompletionAsync();
        Assert.Empty(undone.Characters.Profiles); Assert.False(undone.Characters.ConsistencyLockEnabled); Assert.Equal(before.Terms.Count, undone.Terms.Count);
        var ledger = await service.LoadAsync();
        Assert.All(done.Applied, a => Assert.Equal(AddressingDecision.Reverted, ledger.LatestReview(a.ProposalId)!.Decision));
        Assert.False(await service.CanUndoCompletionAsync());
        Assert.Equal(4, (await service.CompleteAsync(undone.Revision)).Applied.Count); // Pending again: "Hoàn thành" works once more.
    }

    [Fact]
    public async Task Undo_after_later_edits_asks_before_discarding_them()
    {
        var (session, service, _) = await AddressingReviewTests.Seed(_root);
        var done = await service.CompleteAsync((await Load(session)).Revision);
        var edited = done.Resources.Clone(); edited.Context = "Ghi chú người dùng thêm sau.";
        await Save(session, edited);
        await Assert.ThrowsAsync<AddressingUndoConflictException>(() => service.UndoCompletionAsync());
        Assert.Equal("Ghi chú người dùng thêm sau.", (await Load(session)).Context); // Nothing changed.
        var undone = await service.UndoCompletionAsync(discardLaterEdits: true);
        Assert.Empty(undone.Characters.Profiles); Assert.Equal("", undone.Context);
    }

    [Fact]
    public async Task Locked_profiles_existing_rules_and_the_glossary_are_never_overwritten()
    {
        var (session, service, _) = await AddressingReviewTests.Seed(_root);
        var resources = await Load(session);
        var mine = new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", Gender = CharacterGender.Female, PrimaryNarrativePronoun = "cô", NarrativePronounLocked = true, NormalizationEnabled = false };
        var hero = new CharacterProfile { ChineseName = "林青", VietnameseName = "Lâm Thanh", NormalizationEnabled = false };
        hero.Relationships.Add(new() { TargetCharacterId = mine.Id, ListenerCharacterId = mine.Id, SpeakerCharacterId = hero.Id, Relationship = "em", SelfReference = "tôi", AddresseeReference = "chị", Enabled = true });
        resources.Characters.Profiles.AddRange([mine, hero]);
        resources.Terms.Add(new("小兰", "Lan nhi")); // The user's glossary rendering.
        var saved = await Save(session, resources);

        var done = await service.CompleteAsync(saved.Revision);
        var heroine = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
        Assert.Equal("cô", heroine.PrimaryNarrativePronoun); // Locked by the user: untouched.
        Assert.Contains(done.Held, h => h.Kind == AddressingTaskKind.Character && h.Subject == "王秀兰" && h.Detail.Contains("đã khóa"));
        var heroRules = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "林青").Relationships;
        Assert.Equal("tôi", Assert.Single(heroRules).SelfReference); // The user's rule stays.
        Assert.Contains(done.Held, h => h.Kind == AddressingTaskKind.Pair && h.Subject == "林青" && h.Detail.Contains("không ghi đè"));
        Assert.Contains(heroine.Relationships, r => r.SelfReference == "chị"); // The other direction had no rule: written.
        Assert.Equal("Lan nhi", done.Resources.Terms.Single(t => t.Chinese == "小兰").Vietnamese);
        Assert.Equal("hắn", done.Resources.Characters.Profiles.Single(p => p.ChineseName == "林青").PrimaryNarrativePronoun); // Empty field: filled.
    }

    [Fact]
    public async Task The_users_glossary_rendering_wins_over_the_ai_and_does_not_hold_the_character()
    {
        var (session, service, _) = await AddressingReviewTests.Seed(_root);
        var resources = await Load(session);
        resources.Terms.Add(new("小兰", "Lan nhi")); resources.Terms.Add(new("王秀兰", "Vương Tú Lan (bản của tôi)"));
        var done = await service.CompleteAsync((await Save(session, resources)).Revision);
        var heroine = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
        Assert.Equal("Vương Tú Lan (bản của tôi)", heroine.VietnameseName); Assert.Contains("小兰", heroine.Aliases);
        Assert.Equal("Lan nhi", done.Resources.Terms.Single(t => t.Chinese == "小兰").Vietnamese);
        Assert.DoesNotContain(done.Held, h => h.Subject == "王秀兰" && h.Kind == AddressingTaskKind.Character);
    }

    [Fact]
    public async Task The_newest_answer_wins_and_human_decisions_are_kept()
    {
        var (session, service, ledger) = await AddressingReviewTests.Seed(_root);
        var sisterPair = ledger.Proposals[Index(ledger, AddressingTaskKind.Pair, "王秀兰")];
        // A later run answered NotThisPair for the same direction: the older valid answer must not be applied.
        await Edit(session, s => s.Proposals.Add(sisterPair with { Id = Guid.NewGuid(), Status = "NotThisPair", EvidenceValid = false, Phases = [] }));
        await service.RejectAsync(ledger.Proposals[Index(ledger, AddressingTaskKind.Character, "林青")].Id);

        var done = await service.CompleteAsync((await Load(session)).Revision);
        Assert.Contains(done.Held, h => h.Subject == "王秀兰" && h.Listener == "林青" && h.Detail == "AI: NotThisPair");
        Assert.Contains(done.Held, h => h.Kind == AddressingTaskKind.Character && h.Subject == "林青" && h.Detail == "bạn đã bác bỏ");
        Assert.DoesNotContain(done.Resources.Characters.Profiles, p => p.ChineseName == "林青");
        Assert.Contains(done.Held, h => h.Subject == "林青" && h.Listener == "王秀兰" && h.Detail.Contains("Chưa có hồ sơ 林青")); // Needs both profiles.
        Assert.Single(done.Applied); // Only 王秀兰's character.
    }

    [Fact]
    public async Task Ambiguous_name_forms_and_chinese_in_old_proposals_are_held_without_blocking_the_rest()
    {
        var (session, service, ledger) = await AddressingReviewTests.Seed(_root);
        await Edit(session, s =>
        {
            foreach (string name in new[] { "王秀兰", "林青" })
            {
                int i = Index(s, AddressingTaskKind.Character, name);
                s.Proposals[i] = s.Proposals[i] with { NameForms = [.. s.Proposals[i].NameForms, new("小王", "Tiểu Vương")] }; // Claimed by both.
            }
            int pair = Index(s, AddressingTaskKind.Pair, "林青"); // Verified before the Chinese-in-Vietnamese rule existed.
            s.Proposals[pair] = s.Proposals[pair] with { Phases = [s.Proposals[pair].Phases[0] with { AddressVi = "王先生" }] };
        });
        var done = await service.CompleteAsync((await Load(session)).Revision);
        Assert.DoesNotContain(done.Resources.Characters.Profiles, p => p.Aliases.Contains("小王"));
        Assert.Equal(2, done.Held.Count(h => h.Detail.Contains("小王") && h.Detail.Contains("nhiều nhân vật")));
        Assert.Contains(done.Held, h => h.Subject == "林青" && h.Listener == "王秀兰" && h.Detail.Contains("chữ Hán"));
        Assert.Equal(3, done.Applied.Count); // Both characters and the other pair still written.
    }

    [Fact]
    public async Task A_title_form_is_never_an_automatic_alias_even_when_one_character_claims_it()
    {
        // Real-corpus case: 宁小姐 (Miss Ning) was proposed for 宁雪落 only, but it names her sister 宁夕 as well.
        var (session, service, _) = await AddressingReviewTests.Seed(_root);
        await Edit(session, s =>
        {
            int i = Index(s, AddressingTaskKind.Character, "王秀兰");
            s.Proposals[i] = s.Proposals[i] with { NameForms = [.. s.Proposals[i].NameForms, new("王小姐", "Vương tiểu thư")] };
        });
        var done = await service.CompleteAsync((await Load(session)).Revision);
        var heroine = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
        Assert.DoesNotContain("王小姐", heroine.Aliases); Assert.Contains("小兰", heroine.Aliases);
        Assert.Contains(done.Held, h => h.Detail.Contains("王小姐") && h.Detail.Contains("danh xưng"));
    }
}
