using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>
/// "Hoàn thành" and a person known only by a title (陆老夫人, a recurring grandmother): a minimal profile is created only on
/// strong CPU-verified evidence, with a Vietnamese name from the glossary or a verified answer; otherwise the pair is held.
/// Mock proposals on two differently styled novels (urban romance with “”, xianxia with 「」); nothing calls a provider.
/// </summary>
public sealed class AddressingTitleProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-title-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed record Novel(string Title, string Person, string Partner, string PartnerVi, string PersonVi, int[] Chapters);
    private static readonly Novel Urban = new("隐婚100分", "陆老夫人", "宁夕", "Ninh Tịch", "Lục lão phu nhân", [17, 18, 19]);
    private static readonly Novel Xianxia = new("test cpu", "秦老太爷", "柳轻锋", "Liễu Khinh Phong", "Tần lão thái gia", [3, 9, 9]);

    public static TheoryData<string> Novels => new() { Urban.Title, Xianxia.Title };
    private static Novel Pick(string title) => title == Urban.Title ? Urban : Xianxia;

    private async Task<(ProjectSession Session, AddressingReviewService Service)> Project(Novel n, Action<ProjectResources>? edit = null)
    {
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), n.Title);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        resources.Characters.Profiles.Add(new CharacterProfile { ChineseName = n.Partner, VietnameseName = n.PartnerVi, NormalizationEnabled = false });
        edit?.Invoke(resources);
        await new ProjectResourceStore(session).SaveAsync(resources);
        return (session, new AddressingReviewService(session));
    }

    private static AddressingProposal Pair(string speaker, string listener, string self, string address, int from, int to) =>
        new(Guid.NewGuid(), "req", Guid.NewGuid().ToString("N"), AddressingTaskKind.Pair, speaker, listener, "Consistent", "Quan hệ", "unknown",
            [new(from, to, self, address, [], [], "", "", ["S1"])], [], "Lời thoại trực tiếp.", true, []);

    private static AddressingProposal Character(string name, string vi, string gender = "female") =>
        new(Guid.NewGuid(), "req", Guid.NewGuid().ToString("N"), AddressingTaskKind.Character, name, null, "Consistent", "", gender,
            [new(1, 0, "", "", [], [], "bà", "", ["N1"])], [new(name, vi)], "Lời kể.", true, []);

    /// <summary>Verified scene turns where the title person talks with the partner, one per chapter listed.</summary>
    private static AddressingProposal Scene(Novel n, params int[] chapters) =>
        new(Guid.NewGuid(), "req", Guid.NewGuid().ToString("N"), AddressingTaskKind.Scene, "", null, "Scene", "", "", [], [], "", true, [])
        {
            Turns = chapters.Select((c, i) => new AddressingTurn("Q" + (i + 1), c, 100 * (i + 1), "h" + i, i % 2 == 0 ? n.Person : n.Partner, i % 2 == 0 ? n.Partner : n.Person, [], [], [])).ToArray()
        };

    private static async Task Ledger(ProjectSession s, Action<AddressingLedgerState> edit) => await new AddressingLedgerStore(s).UpdateAsync(edit);
    private static Task<ProjectResources> Load(ProjectSession s) => new ProjectResourceStore(s).LoadAsync();

    private static void Evidence(AddressingLedgerState s, Novel n, params int[] chapters)
    {
        s.Proposals.Add(Scene(n, chapters));
        s.Proposals.Add(Pair(n.Person, n.Partner, "ta", "con", 17, 19));
        s.Proposals.Add(Pair(n.Partner, n.Person, "cháu", "bà", 17, 19));
    }

    [Theory, MemberData(nameof(Novels))]
    public async Task Strong_evidence_and_a_glossary_name_create_a_minimal_profile_and_both_pair_rules_and_undo_restores(string title)
    {
        var n = Pick(title);
        Assert.True(ChineseMentionDiscovery.IsTitleForm(n.Person));
        var (session, service) = await Project(n, r => r.Terms.Add(new(n.Person, n.PersonVi)));
        await Ledger(session, s => Evidence(s, n, n.Chapters));
        var before = await Load(session);

        var preview = await service.PreviewCompletionAsync();
        Assert.Equal(2, preview.Applied.Count); Assert.Empty(preview.Held);
        Assert.Equal(before.Revision, (await Load(session)).Revision);

        var done = await service.CompleteAsync(before.Revision);
        Assert.Empty(done.Held);
        var person = done.Resources.Characters.Profiles.Single(p => p.ChineseName == n.Person);
        var partner = done.Resources.Characters.Profiles.Single(p => p.ChineseName == n.Partner);
        Assert.Equal(n.PersonVi, person.VietnameseName); Assert.Equal(CharacterGender.Unknown, person.Gender);
        Assert.Equal("", person.PrimaryNarrativePronoun); Assert.False(person.NarrativePronounLocked); Assert.False(person.NormalizationEnabled);
        Assert.False(AddressingAiSurvey.HasHan(person.VietnameseName));
        Assert.Contains(person.Relationships, r => r.EffectiveListenerCharacterId == partner.Id && r.SelfReference == "ta" && r.AddresseeReference == "con" && r.Locked);
        Assert.Contains(partner.Relationships, r => r.EffectiveListenerCharacterId == person.Id && r.SelfReference == "cháu");
        Assert.Contains(done.Applied, a => a.Detail.Contains("Tạo hồ sơ danh xưng " + n.Person));
        Assert.True(done.Resources.Characters.ConsistencyLockEnabled);

        var undone = await service.UndoCompletionAsync();
        Assert.DoesNotContain(undone.Characters.Profiles, p => p.ChineseName == n.Person);
        Assert.Contains(undone.Terms, t => t.Chinese == n.Person && t.Vietnamese == n.PersonVi); // The user's glossary entry survives the undo.
        Assert.Empty(undone.Characters.Profiles.Single(p => p.ChineseName == n.Partner).Relationships);
        Assert.Equal(2, (await service.CompleteAsync(undone.Revision)).Applied.Count); // Pending again.
    }

    [Theory, MemberData(nameof(Novels))]
    public async Task The_vietnamese_name_may_come_from_a_verified_character_answer_with_its_gender(string title)
    {
        var n = Pick(title);
        var (session, service) = await Project(n);
        await Ledger(session, s => { Evidence(s, n, n.Chapters); s.Proposals.Add(Character(n.Person, n.PersonVi)); });
        var done = await service.CompleteAsync((await Load(session)).Revision);
        var person = done.Resources.Characters.Profiles.Single(p => p.ChineseName == n.Person);
        Assert.Equal(n.PersonVi, person.VietnameseName); Assert.Equal(CharacterGender.Female, person.Gender);
        Assert.Equal(2, person.Relationships.Count + done.Resources.Characters.Profiles.Single(p => p.ChineseName == n.Partner).Relationships.Count);
        Assert.Equal(3, done.Applied.Count); Assert.Empty(done.Held);
    }

    [Theory, MemberData(nameof(Novels))]
    public async Task Weak_evidence_keeps_the_pair_held_with_the_reason_and_writes_no_profile(string title)
    {
        var n = Pick(title);
        // Three turns, all in one chapter; and then only two turns in two chapters.
        foreach (int[] chapters in new[] { new[] { 17, 17, 17 }, new[] { 17, 18 } })
        {
            var (p, svc) = await Project(n, r => r.Terms.Add(new(n.Person, n.PersonVi)));
            await Ledger(p, s => Evidence(s, n, chapters));
            var done = await svc.CompleteAsync((await Load(p)).Revision);
            Assert.DoesNotContain(done.Resources.Characters.Profiles, x => x.ChineseName == n.Person);
            Assert.Equal(2, done.Held.Count(h => h.Kind == AddressingTaskKind.Pair && h.Detail.Contains("Chưa tự tạo hồ sơ danh xưng " + n.Person) && h.Detail.Contains("lượt thoại")));
            Assert.Empty(done.Applied);
        }
    }

    [Theory, MemberData(nameof(Novels))]
    public async Task No_vietnamese_name_a_han_name_a_duplicate_name_or_a_claimed_form_all_hold(string title)
    {
        var n = Pick(title);
        var cases = new (string Why, Action<ProjectResources> Resources, Action<AddressingLedgerState> Ledger)[]
        {
            ("tên tiếng Việt", _ => { }, _ => { }),
            ("chữ Hán", r => r.Terms.Add(new(n.Person, "Lục 老夫人")), _ => { }),
            ("trùng tên Việt", r => { r.Terms.Add(new(n.Person, n.PartnerVi)); }, _ => { }),
            ("cách gọi của người khác", r => r.Terms.Add(new(n.Person, n.PersonVi)),
                s => s.Proposals.Add(Character(n.Partner, n.PartnerVi) with { NameForms = [new(n.Partner, n.PartnerVi), new(n.Person, n.PersonVi)] })),
            ("nhiều nhân vật", r => r.Terms.Add(new(n.Person, n.PersonVi)), s =>
            {
                foreach (string other in new[] { "甲乙", "丙丁" })
                    s.Proposals.Add(Character(other, "Người " + other.Length) with { NameForms = [new(other, "Người"), new(n.Person, n.PersonVi)] });
            }),
        };
        foreach (var (why, resources, ledger) in cases)
        {
            var (session, service) = await Project(n, resources);
            await Ledger(session, s => { Evidence(s, n, n.Chapters); ledger(s); });
            var done = await service.CompleteAsync((await Load(session)).Revision);
            Assert.DoesNotContain(done.Resources.Characters.Profiles, p => p.ChineseName == n.Person);
            Assert.Contains(done.Held, h => h.Kind == AddressingTaskKind.Pair && h.Detail.Contains("Chưa tự tạo hồ sơ danh xưng") && h.Detail.Contains(why));
            Assert.All(done.Resources.Characters.Profiles, p => Assert.DoesNotContain(p.Relationships, r => r.EffectiveListenerCharacterId == Guid.Empty));
        }
    }

    [Theory, MemberData(nameof(Novels))]
    public async Task Existing_profiles_and_user_decisions_are_never_overwritten_or_bypassed(string title)
    {
        var n = Pick(title);
        // The user already has a locked profile for the person, with their own rule for one direction.
        var (session, service) = await Project(n, r =>
        {
            var mine = new CharacterProfile { ChineseName = n.Person, VietnameseName = "Bà tôi đặt", PrimaryNarrativePronoun = "bà", NarrativePronounLocked = true, NormalizationEnabled = false };
            var partner = r.Characters.Profiles.Single(p => p.ChineseName == n.Partner);
            mine.Relationships.Add(new() { TargetCharacterId = partner.Id, ListenerCharacterId = partner.Id, SpeakerCharacterId = mine.Id, Relationship = "Của tôi", SelfReference = "bà", AddresseeReference = "cháu", Enabled = true, Locked = true });
            r.Characters.Profiles.Add(mine);
        });
        await Ledger(session, s => Evidence(s, n, n.Chapters));
        var done = await service.CompleteAsync((await Load(session)).Revision);
        var person = done.Resources.Characters.Profiles.Single(p => p.ChineseName == n.Person);
        Assert.Equal("Bà tôi đặt", person.VietnameseName); Assert.Equal("bà", Assert.Single(person.Relationships).SelfReference);
        Assert.Contains(done.Held, h => h.Subject == n.Person && h.Detail.Contains("không ghi đè"));
        Assert.Single(done.Applied); // The other direction (to the existing profile) is written.
        Assert.DoesNotContain(done.Applied, a => a.Detail.Contains("Tạo hồ sơ danh xưng"));

        // A rejected pair stays rejected even where the profile could have been created.
        var (session2, service2) = await Project(n, r => r.Terms.Add(new(n.Person, n.PersonVi)));
        AddressingProposal rejected = null!;
        await Ledger(session2, s => { Evidence(s, n, n.Chapters); rejected = s.Proposals.Last(); });
        await service2.RejectAsync(rejected.Id);
        var done2 = await service2.CompleteAsync((await Load(session2)).Revision);
        Assert.Contains(done2.Held, h => h.Detail == "bạn đã bác bỏ");
        Assert.Single(done2.Applied); // Only the other direction; its profile is created for it.
    }

    [Fact]
    public void Planning_asks_about_a_title_only_person_who_speaks_in_verified_lines()
    {
        const string text = "    “奶奶，您吃饭了吗？”\n    “吃过了，夕夕。”\n    陆老夫人看着宁夕，笑了笑。\n    陆老夫人点点头。\n    陆老夫人又说道：“夕夕，过来。”\n";
        var sources = new[] { new CensusSource(Guid.NewGuid(), 17, text), new CensusSource(Guid.NewGuid(), 18, text) };
        string[] names = ["宁夕"];
        string reply = "吃过了，夕夕。", ask = "奶奶，您吃饭了吗？";
        var turns = sources.SelectMany(s => new[]
        {
            new AddressingTurn("Q1", s.ChapterNumber, text.IndexOf(ask, StringComparison.Ordinal), AddressingAiSurvey.QuoteHash(ask), "宁夕", "陆老夫人", [], [], []),
            new AddressingTurn("Q2", s.ChapterNumber, text.IndexOf(reply, StringComparison.Ordinal), AddressingAiSurvey.QuoteHash(reply), "陆老夫人", "宁夕", [], [], [])
        }).ToArray();
        var tasks = AddressingAiSurvey.PlanTasks(sources, names, new(MinimumPairLines: 2), sceneTurns: turns);
        Assert.Contains(tasks, t => t.Kind == AddressingTaskKind.Pair && (t.Subject == "陆老夫人" || t.Listener == "陆老夫人"));
        var person = tasks.Single(t => t.Kind == AddressingTaskKind.Character && t.Subject == "陆老夫人");
        Assert.True(person.Samples.Count > 0);
        Assert.DoesNotContain(AddressingAiSurvey.PlanTasks(sources, names, new(MinimumPairLines: 2)), t => t.Kind == AddressingTaskKind.Character && t.Subject == "陆老夫人");
    }
}
