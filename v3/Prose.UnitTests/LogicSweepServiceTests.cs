using Prose.Core.Services.Audit;

namespace Prose.UnitTests;

/// <summary>
/// Tests for LogicSweepService's deterministic, LLM-free helper. The six audit dimensions
/// themselves (causality, knowledge states, timeline, plant/payoff, orphan references, outline
/// agreement) are each a single LLM call and aren't practically unit-testable, but
/// <c>ParseFindingsArray</c> — the ONE parser all six dimensions share for their untrusted LLM
/// JSON output — is pure logic worth covering directly. Made <c>internal</c> (was <c>private</c>);
/// <c>InternalsVisibleTo</c> already covers this project. (Prose truncation is now the shared
/// <c>AuditProseUtils.ClampProse</c> — see <c>AuditProseUtilsTests.cs</c>.)
/// </summary>
[TestFixture]
public class LogicSweepServiceTests
{
    private static readonly IReadOnlyList<AuditBeat> Beats =
    [
        new AuditBeat(Guid.Parse("00000000-0000-0000-0000-000000000001"), 1, "Beat one text."),
        new AuditBeat(Guid.Parse("00000000-0000-0000-0000-000000000002"), 2, "Beat two text."),
        new AuditBeat(Guid.Parse("00000000-0000-0000-0000-000000000003"), 3, "Beat three text."),
    ];

    // ── WithholdSubtextSections ───────────────────────────────────────────────

    [Test]
    public void WithholdSubtextSections_RemovesNeverStatedSectionBodyUntilNextPeerHeading()
    {
        var outline = string.Join("\n",
            "## 1b. Facts {#a}",
            "Kyle arrived in GLMZ in 2215.",
            "## 1c. THE TRUE NATURE OF THE ENTITY (AUTHOR RULING — never stated on page, but legible from it) {#b}",
            "The entity is Kyle himself.",
            "### 1c-i. sub-point",
            "Still doctrine.",
            "## 2. Mrs. Chen {#c}",
            "Sacred ground.");
        var kept = LogicSweepService.WithholdSubtextSections(outline);

        Assert.That(kept, Does.Contain("Kyle arrived in GLMZ in 2215."));
        Assert.That(kept, Does.Contain("Sacred ground."));
        Assert.That(kept, Does.Not.Contain("The entity is Kyle himself."));
        Assert.That(kept, Does.Not.Contain("Still doctrine."));
        Assert.That(kept, Does.Contain("## 1c. THE TRUE NATURE"), "heading stub stays so the model knows the section exists");
        Assert.That(kept, Does.Contain("withheld from this dimension"));
    }

    [Test]
    public void WithholdSubtextSections_LeavesOrdinaryOutlineUntouched()
    {
        var outline = "## 1. Arc\nA fact.\n## 2. Cast\nAnother fact.\n";
        Assert.That(LogicSweepService.WithholdSubtextSections(outline).Trim(), Is.EqualTo(outline.Trim()));
    }

    // ── ParseFindingsArray ─────────────────────────────────────────────────────

    [Test]
    public void ParseFindingsArray_ValidArray_ParsesAllFields()
    {
        var raw = """
            [{"beat_number":2,"severity":"blocker","evidence":"contradicts beat 1","fix":"reconcile the two"}]
            """;
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);

        Assert.That(results, Has.Count.EqualTo(1));
        var v = results[0];
        Assert.That(v.RuleKey, Is.EqualTo("causality"));
        Assert.That(v.Title, Is.EqualTo("Causality chain"));
        Assert.That(v.Severity, Is.EqualTo("BLOCKER")); // upper-cased
        Assert.That(v.Evidence, Does.StartWith("Beat #2:"));
        Assert.That(v.Location, Is.EqualTo(Beats[1].Id.ToString()));
        Assert.That(v.Fix, Is.EqualTo("reconcile the two"));
    }

    [Test]
    public void ParseFindingsArray_EmptyArray_ReturnsEmpty()
    {
        var results = LogicSweepService.ParseFindingsArray("timeline", "Timeline", "[]", Beats);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseFindingsArray_ChatterAroundArray_ExtractsInnerArray()
    {
        var raw = "Here are the findings:\n[{\"beat_number\":1,\"severity\":\"minor\",\"evidence\":\"small nit\",\"fix\":null}]\nDone.";
        var results = LogicSweepService.ParseFindingsArray("timeline", "Timeline", raw, Beats);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Severity, Is.EqualTo("MINOR"));
        Assert.That(results[0].Fix, Is.Null);
    }

    [Test]
    public void ParseFindingsArray_NoBrackets_ReturnsEmpty()
    {
        var results = LogicSweepService.ParseFindingsArray("timeline", "Timeline", "The timeline holds.", Beats);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseFindingsArray_MalformedJson_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.DoesNotThrow(() =>
        {
            var results = LogicSweepService.ParseFindingsArray("timeline", "Timeline", "[{\"severity\": oops}]", Beats);
            Assert.That(results, Is.Empty);
        });
    }

    [Test]
    public void ParseFindingsArray_EmptyEvidence_EntryIsDropped()
    {
        var raw = """[{"beat_number":1,"severity":"blocker","evidence":"","fix":"x"}]""";
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);
        Assert.That(results, Is.Empty, "an entry with no evidence is not a real finding and must not persist");
    }

    [Test]
    public void ParseFindingsArray_UnknownSeverity_DefaultsToModerate()
    {
        var raw = """[{"beat_number":1,"severity":"catastrophic","evidence":"something's wrong","fix":null}]""";
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Severity, Is.EqualTo("MODERATE"));
    }

    [Test]
    public void ParseFindingsArray_NullBeatNumber_LocationIsNull()
    {
        // plant/payoff and outline-agreement findings can be whole-node (beat_number: null)
        var raw = """[{"beat_number":null,"severity":"moderate","evidence":"whole-book issue","fix":null}]""";
        var results = LogicSweepService.ParseFindingsArray("plant_payoff", "Plant/payoff ledger", raw, Beats);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Location, Is.Null);
        Assert.That(results[0].Evidence, Is.EqualTo("whole-book issue")); // no "Beat #" prefix without a number
    }

    [Test]
    public void ParseFindingsArray_BeatNumberNotInBeatsList_LocationIsNullButEvidenceStillTagged()
    {
        // beat_number references a beat that isn't in this audit's beat set (e.g. stale/renumbered)
        var raw = """[{"beat_number":99,"severity":"minor","evidence":"orphaned reference","fix":null}]""";
        var results = LogicSweepService.ParseFindingsArray("orphan_refs", "Orphan references", raw, Beats);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Location, Is.Null);
        Assert.That(results[0].Evidence, Does.StartWith("Beat #99:"));
    }

    [Test]
    public void ParseFindingsArray_MultipleFindings_AllParsed()
    {
        var raw = """
            [
                {"beat_number":1,"severity":"blocker","evidence":"first problem","fix":"fix one"},
                {"beat_number":2,"severity":"moderate","evidence":"second problem","fix":"fix two"},
                {"beat_number":3,"severity":"minor","evidence":"third problem","fix":null}
            ]
            """;
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results.Select(r => r.Severity), Is.EqualTo(new[] { "BLOCKER", "MODERATE", "MINOR" }));
    }

    // ── LogicSweepReport aggregation ───────────────────────────────────────────

    [Test]
    public void LogicSweepReport_CountsAggregateCorrectly()
    {
        var findings = new List<AuditVerdict>
        {
            new("causality", "Causality chain", "BLOCKER", "e1"),
            new("timeline", "Timeline", "MODERATE", "e2"),
            new("timeline", "Timeline", "MINOR", "e3"),
        };
        var report = new LogicSweepReport(Guid.NewGuid(), "test-slug", "Test Book", 10, findings);

        Assert.That(report.BlockerCount, Is.EqualTo(1));
        Assert.That(report.ModerateCount, Is.EqualTo(1));
        Assert.That(report.MinorCount, Is.EqualTo(1));
        Assert.That(report.Clean, Is.False);
    }

    [Test]
    public void LogicSweepReport_NoFindings_IsClean()
    {
        var report = new LogicSweepReport(Guid.NewGuid(), "test-slug", "Test Book", 10, []);
        Assert.That(report.Clean, Is.True);
        Assert.That(report.BlockerCount, Is.EqualTo(0));
    }

    // ── Hallucinated-citation guard (2026-08-14 VIGL session) ──────────────────
    //
    // AuditProseUtils.ClampProse (used by every whole-book LLM audit rule) elides the entire
    // middle of an oversized book's concatenated prose, keeping only the first+last 50k chars.
    // Confirmed on VIGL's real until-dry sweep: the model can still cite a beat number whose
    // actual text fell in that elided middle and fabricate a plausible-sounding quote for it —
    // ParseFindingsArray resolved beat_number against the FULL beat list regardless of what the
    // model was actually shown, so the fabrication got a real BeatId and looked like a genuine
    // finding. QuotedEvidenceAppearsInBeat is the fix: reject a finding whose quoted evidence
    // doesn't appear anywhere in the beat it's attributed to.

    [Test]
    public void ParseFindingsArray_QuotedEvidenceNotInCitedBeat_FindingIsDropped()
    {
        // Beat #2's real text is "Beat two text." — a quote that doesn't exist there at all is
        // exactly the VIGL beat-#5656 bug: a citation for content the model never saw.
        var raw = """[{"beat_number":2,"severity":"blocker","evidence":"Doyle says \"this line does not exist in beat two\" here","fix":null}]""";
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseFindingsArray_QuotedEvidenceMatchesCitedBeat_FindingIsKept()
    {
        var raw = """[{"beat_number":2,"severity":"blocker","evidence":"beat says \"Beat two text.\" with no setup","fix":null}]""";
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);
        Assert.That(results, Has.Count.EqualTo(1));
    }

    [Test]
    public void ParseFindingsArray_UnquotedParaphrasedEvidence_NeverRejected()
    {
        // A finding that paraphrases rather than quotes verbatim has nothing to verify against —
        // must not be false-rejected just because it contains no quotation marks.
        var raw = """[{"beat_number":2,"severity":"blocker","evidence":"this contradicts what beat one established","fix":null}]""";
        var results = LogicSweepService.ParseFindingsArray("causality", "Causality chain", raw, Beats);
        Assert.That(results, Has.Count.EqualTo(1));
    }

    [TestCase("Beat two text.", "beat two text", true)] // whitespace/case-insensitive match
    [TestCase("Beat two text.", "fabricated line never in the beat", false)]
    public void QuotedEvidenceAppearsInBeat_MatchesCaseAndWhitespaceInsensitively(string beatText, string quoted, bool expected)
    {
        var evidence = $"the text reads \"{quoted}\" plainly";
        Assert.That(LogicSweepService.QuotedEvidenceAppearsInBeat(evidence, beatText), Is.EqualTo(expected));
    }

    [Test]
    public void QuotedEvidenceAppearsInBeat_NoQuotesInEvidence_ReturnsTrue()
    {
        Assert.That(LogicSweepService.QuotedEvidenceAppearsInBeat("no quotes here at all", "Beat two text."), Is.True);
    }

    [Test]
    public void QuotedEvidenceAppearsInBeat_SingleQuotedFabrication_IsCaught()
    {
        // 2026-08-14 real VIGL round-5 bug: the model attributed a whole invented scene to
        // beat #14657 using single quotes throughout ('worked her fingers back into the seam',
        // 'let the smaller lens come free of the larger one') — none of which exist in that
        // beat's real text (which is actually about Orim kneeling and saying "Home"). The
        // double-quote-only check let this straight through; single-quote detection must catch it.
        var evidence = "Lyra 'worked her fingers back into the seam' and 'let the smaller lens come free of the larger one' at the crater";
        var realBeatText = "Not far past where the hand had been, Orim stopped. He knelt where the glass gave way. \"Home,\" he said.";
        Assert.That(LogicSweepService.QuotedEvidenceAppearsInBeat(evidence, realBeatText), Is.False);
    }

    [Test]
    public void QuotedEvidenceAppearsInBeat_ShortMisattributedFragments_AreCaught()
    {
        // 2026-08-14 real VIGL round-7 bug: the model misattributed beat #5590's real text
        // ("what it authorized did not exist") to a fabricated "beat #5603" (Doyle's Amnios-drift
        // scene, which never mentions instruments or authorization at all), quoting only short
        // fragments ('authorized', 'did not exist' — 10/14 chars) that the original 15-char
        // floor didn't check. Both must now be caught against beat #5603's real, unrelated text.
        var evidence = "describes Vega reading a property-recovery instrument that 'authorized' something that 'did not exist'";
        var beat5603RealText = "There is a spark, and the spark is aware that it is a spark and not a man, suspended in something warm and gold.";
        Assert.That(LogicSweepService.QuotedEvidenceAppearsInBeat(evidence, beat5603RealText), Is.False);
    }

    [Test]
    public void QuotedEvidenceAppearsInBeat_SingleQuotedRealText_IsAccepted()
    {
        var evidence = "the beat states 'Beat two text' plainly";
        Assert.That(LogicSweepService.QuotedEvidenceAppearsInBeat(evidence, "Beat two text."), Is.True);
    }

    [TestCase("Doyle's shell and Orim's rod are both mentioned in passing")]
    [TestCase("she wasn't expecting that and hadn't planned for it either")]
    public void QuotedEvidenceAppearsInBeat_PossessivesAndContractions_NeverMistakenForQuotes(string evidence)
    {
        // Mid-word apostrophes ("Orim's", "wasn't") must never be treated as a pair of quote
        // marks bounding some accidental substring — that would reject valid findings whose
        // evidence merely happens to contain two possessives/contractions in the same sentence.
        Assert.That(LogicSweepService.QuotedEvidenceAppearsInBeat(evidence, "Beat two text."), Is.True);
    }

    // ── BuildClampedProse ───────────────────────────────────────────────────────

    [Test]
    public void BuildClampedProse_UnderThreshold_ReturnsFullConcatenationUnchanged()
    {
        var result = LogicSweepService.BuildClampedProse(Beats);
        Assert.That(result, Does.Contain("Beat one text."));
        Assert.That(result, Does.Contain("Beat three text."));
        Assert.That(result, Does.Not.Contain("elided"));
    }

    [Test]
    public void BuildClampedProse_OverThreshold_NamesTheElidedBeatRangeAndWarnsAgainstCitingIt()
    {
        // 200 beats of ~1000 chars each (~200k total) forces the clamp; beats roughly in the
        // middle third should land in the elided gap and get named in the placeholder.
        var beats = Enumerable.Range(1, 200)
            .Select(n => new AuditBeat(Guid.NewGuid(), n, new string('x', 900) + $" beat{n}"))
            .ToList();

        var result = LogicSweepService.BuildClampedProse(beats);

        Assert.That(result, Does.Contain("[Beat #1]"), "head should be visible");
        Assert.That(result, Does.Contain("[Beat #200]"), "tail should be visible");
        Assert.That(result, Does.Contain("elided"));
        Assert.That(result, Does.Contain("Do NOT report a finding citing any beat number in that range"));

        // The named elided range must actually bracket beat #100 (the book's middle) — the
        // instruction is useless if it doesn't cover the beats the model is most likely to
        // fabricate a citation for.
        var match = System.Text.RegularExpressions.Regex.Match(result, @"beats #(\d+)-#(\d+)");
        Assert.That(match.Success, Is.True, "elision note should name a beat-number range");
        var lo = int.Parse(match.Groups[1].Value);
        var hi = int.Parse(match.Groups[2].Value);
        Assert.That(lo, Is.LessThan(100));
        Assert.That(hi, Is.GreaterThan(100));
    }

    // ── Self-declared non-findings (2026-08-24) ─────────────────────────────────
    // Models persistently return confirmations and non-verifications as findings, sometimes at
    // BLOCKER severity — a real VIGL round filed a BLOCKER whose evidence concluded "the prose is
    // consistent with the outline's locked kill choreography." Persisting those makes every other
    // finding untrustworthy, which is what made prior reports say "don't run --until-dry."

    [Test]
    public void ParseFindingsArray_ConfirmationReportedAsFinding_IsDropped()
    {
        var raw = """
            [{"beat_number":1,"severity":"blocker","evidence":"This matches the outline's description of the kill sequence exactly. The prose is consistent with the outline's locked kill choreography.","fix":null}]
            """;
        var results = LogicSweepService.ParseFindingsArray("outline_agreement", "Outline agreement", raw, Beats);
        Assert.That(results, Is.Empty, "a confirmation is not a finding, whatever severity the model stamped on it");
    }

    [Test]
    public void ParseFindingsArray_NonVerificationReportedAsFinding_IsDropped()
    {
        var raw = """
            [{"beat_number":null,"severity":"moderate","evidence":"Cannot verify whether beat #4369 contains the tally; those beats were not provided.","fix":"Provide beats from Ch16 to verify."}]
            """;
        var results = LogicSweepService.ParseFindingsArray("outline_agreement", "Outline agreement", raw, Beats);
        Assert.That(results, Is.Empty, "a gap in the model's window is not a defect in the book");
    }

    [Test]
    public void ParseFindingsArray_NoFixNeededInTheFixField_IsDropped()
    {
        var raw = """
            [{"beat_number":1,"severity":"minor","evidence":"Beat one text.","fix":"No fix needed; outline and prose align on the separation."}]
            """;
        var results = LogicSweepService.ParseFindingsArray("outline_agreement", "Outline agreement", raw, Beats);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void ParseFindingsArray_RealFinding_SurvivesTheNonFindingFilter()
    {
        // The filter must not eat genuine defects — this is the failure mode that would matter.
        var raw = """
            [{"beat_number":2,"severity":"blocker","evidence":"Beat two text. states the grace period is eight days, but the sale on day nine is called inside the window.","fix":"Change eight to ten."}]
            """;
        var results = LogicSweepService.ParseFindingsArray("outline_agreement", "Outline agreement", raw, Beats);
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Severity, Is.EqualTo("BLOCKER"));
    }

    [Test]
    public void IsSelfDeclaredNonFinding_MixedVerdict_IsNotTreatedAsAConfirmation()
    {
        // "X is consistent, but Y contradicts it" is a REAL finding and must not be filtered.
        Assert.That(
            LogicSweepService.IsSelfDeclaredNonFinding(
                "Beat 12 is consistent with the timeline, but beat 14 places the same scene a month earlier.",
                "Reconcile beat 14 to the established date."),
            Is.False);
    }

    [Test]
    public void IsSelfDeclaredNonFinding_ConsistencyConclusionWithNoFix_IsFiltered()
    {
        // The shape a real VIGL round produced: an inserted-beat-drift entry that trails off into
        // "...which is consistent with their established professional relationship", proposing
        // nothing. A confirmation wearing a finding's clothes.
        Assert.That(
            LogicSweepService.IsSelfDeclaredNonFinding(
                "The flat, procedural tone suggests a standard acknowledgment rather than a first "
                + "meeting, which is consistent with their established professional relationship.",
                null),
            Is.True);
    }

    [Test]
    public void IsSelfDeclaredNonFinding_ConsistencyMentionWithARealFix_Survives()
    {
        // Same phrase, but the model proposed an actual change — that is a finding, keep it.
        Assert.That(
            LogicSweepService.IsSelfDeclaredNonFinding(
                "Beat 12 is consistent with the timeline, but beat 14 places the same scene a month earlier.",
                "Reconcile beat 14 to the established date."),
            Is.False);
    }

    // ── Chapter attribution in the beat header (2026-08-23) ──────────────────────
    // The outline cites scenes BY CHAPTER, but Beat.Number is not chapter-local, so a
    // prompt labelling prose with only "[Beat #N]" let OutlineAgreementRule compare a beat against
    // a different chapter's description and report a mismatch between two unrelated things. Diagnosed
    // on both BCODA's and VIGL's 2026-08-22 sweeps (beat #3033 is really in Ch30, matching the very
    // Ch30 passage the finding claimed it contradicted) and recommended for fix in both reports.

    [Test]
    public void BuildClampedProse_WhenBeatHasChapterTitle_LabelsTheBeatWithItsChapter()
    {
        var beats = new List<AuditBeat>
        {
            new(Guid.NewGuid(), 3033, "The gray suit sat down.", 100, "Chapter 30 — The Gray Suit"),
        };

        var result = LogicSweepService.BuildClampedProse(beats);

        Assert.That(result, Does.Contain("[Beat #3033 | Chapter 30 — The Gray Suit]"),
            "the model can only match prose to a chapter-keyed outline passage if the header names the chapter");
    }

    [Test]
    public void BuildClampedProse_WhenBeatHasNoChapterTitle_FallsBackToBareBeatHeader()
    {
        // Test fixtures and any caller that can't resolve a chapter must keep the old format
        // rather than emitting an empty "| " and inviting the model to guess a chapter.
        var result = LogicSweepService.BuildClampedProse(
            [new AuditBeat(Guid.NewGuid(), 7, "Text.", 100, "")]);

        Assert.That(result, Does.Contain("[Beat #7]"));
        Assert.That(result, Does.Not.Contain("|"));
    }

    [Test]
    public void BuildClampedProse_OverThreshold_ChapterLabelledBeatsStillResolveVisibility()
    {
        // The head/tail visibility probe matches on the rendered header, so a chapter-labelled
        // book must not report every beat as elided just because the header got longer.
        var beats = Enumerable.Range(1, 200)
            .Select(n => new AuditBeat(
                Guid.NewGuid(), n, new string('x', 900) + $" beat{n}", n,
                $"Chapter {(n / 8) + 1} — Some Title"))
            .ToList();

        var result = LogicSweepService.BuildClampedProse(beats);

        Assert.That(result, Does.Contain("[Beat #1 | Chapter 1 — Some Title]"), "head should be visible");
        Assert.That(result, Does.Contain("[Beat #200 | Chapter 26 — Some Title]"), "tail should be visible");

        var match = System.Text.RegularExpressions.Regex.Match(result, @"beats #(\d+)-#(\d+)");
        Assert.That(match.Success, Is.True, "elision note should still name a real beat-number range");
        Assert.That(int.Parse(match.Groups[1].Value), Is.LessThan(100));
        Assert.That(int.Parse(match.Groups[2].Value), Is.GreaterThan(100));
    }
}
