using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Prose.Core.Data;
using Prose.Core.Data.Entities;
using Prose.Core.Services;

namespace Prose.Core.Services.Audit;

/// <summary>
/// Codifies docs/LOGIC.md's six-dimension sweep (SS-A44) as six independent
/// <see cref="ILlmAuditRule"/>s on the shared <see cref="AuditRunner"/> — causality chain,
/// knowledge states, timeline, plant/payoff (two-way), orphan references, outline agreement.
///
/// <b>Honest scope note:</b> this is a single LLM call per dimension over the WHOLE node's
/// prose (truncated for an oversized book, like BookAuditService's ClampProse). The
/// <c>/logic-sweep</c> Claude Code skill — what actually ran on VIGL's 321-beat sweep this
/// session — splits a big book across several range-scoped subagents that each read their
/// slice closely, then a mechanical quote-verification pass, then triage, then a separate
/// fix pass, then re-verification. That's real thoroughness a single prompt over a clamped
/// 100k-character corpus cannot match on a long book. Use THIS service for a small-to-medium
/// book or as an automatable coarse gate (CI, a scheduled check, "has anything gotten
/// obviously worse since last time") — reach for the skill when you actually need the
/// thorough version.
///
/// Findings persist through the standard delete-then-recreate lifecycle
/// (AuditRunner.RunAsync), same as every other rule on this abstraction — a re-run
/// automatically clears findings for a dimension that's gone clean.
/// </summary>
public class LogicSweepService(
    AuditRunner auditRunner,
    PlantPayoffService plantPayoffs,
    IDbContextFactory<ProseDbContext> dbFactory,
    FindingsService findingsSvc)
{
    /// <summary>Consecutive clean (zero-finding) rounds required before a book is considered
    /// converged. 2, not 1 — a single clean round could just be the sample variance any one
    /// LLM-judgment sweep carries; two in a row is real signal that nothing further is
    /// surfacing, not that this particular read happened not to notice anything.</summary>
    public const int DefaultRequiredDryRounds = 2;

    /// <summary>Safety cap on total rounds before "not converging" is escalated as its own
    /// finding instead of looping forever. Matches the project's own "if you can't name the
    /// failure, leave the beat alone" doctrine — repeated fixing that keeps finding NEW things
    /// without ever reaching two clean rounds in a row is itself a signal (the underlying
    /// section likely needs a structural rewrite, not another fix pass), not something to hide
    /// behind an unbounded loop.</summary>
    public const int DefaultMaxTotalRounds = 8;

    public async Task<LogicSweepReport> RunAsync(Guid nodeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // IgnoreQueryFilters(): explicit nodeId, not an ambient scope (same bug class found and
        // fixed in BookArchiveService.ArchiveAsync/WalkAsync, 2026-08-17).
        var node = await db.Nodes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new InvalidOperationException($"Node {nodeId} not found.");

        // Recurses past any nested Collection (2026-08-09 fix) — this is the canonical Logic
        // Sweep QA methodology (docs/LOGIC.md); a shallow lookup here means a mid-book split
        // chapter's beats are silently excluded from the causality/knowledge-state/timeline audit.
        var nodeIds = await NodeWorkbenchService.GetLeafDescendantIdsAsync(db, nodeId, ct);

        var beatRowsUnordered = await db.BeatNodes.AsNoTracking().Include(bn => bn.Beat)
            .Where(bn => nodeIds.Contains(bn.NodeId) && true && bn.Beat != null
                      && bn.Beat!.Text != null && bn.Beat.Text != "")
            .Select(bn => new { bn.Beat!.Id, bn.Beat.Number, bn.Beat.Text, bn.SortKey, bn.NodeId })
            .ToListAsync(ct);

        if (beatRowsUnordered.Count == 0)
            return new LogicSweepReport(nodeId, node.Slug, node.Title, 0, []);

        // BeatNodes.SortKey is fractional WITHIN a chapter (e.g. 50, 100, 150…) — every chapter's
        // own beats restart near the same values, so ordering the WHOLE book's beats by raw
        // SortKey alone (as this used to) interleaves chapters instead of reading chapter 1 through
        // to the end before chapter 2: a chapter-1 beat at SortKey 100 and a chapter-5 beat also at
        // SortKey 100 sort as ties, scrambling the causality/knowledge-state/timeline order the
        // whole sweep depends on. nodeIds is already in true reading order (GetLeafDescendantIdsAsync
        // is depth-first, SortKey-ordered per level — see its own remarks), exactly like
        // RunNarrowAsync's sibling comment already flags for its own cross-chapter beat set. Order by
        // each beat's chapter position first, then its own SortKey within that chapter.
        var chapterOrder = nodeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var beatRows = beatRowsUnordered
            .OrderBy(b => chapterOrder.TryGetValue(b.NodeId, out var idx) ? idx : int.MaxValue)
            .ThenBy(b => b.SortKey)
            .ToList();

        // Chapter titles for the leaf nodes these beats hang off. The outline cites scenes by
        // CHAPTER while Beat.Number is not chapter-local, so without this the model can't tell
        // which chapter it's reading and OutlineAgreementRule compares a beat to the wrong chapter's
        // lock — see AuditBeat.ChapterTitle for the two sweeps that diagnosed this.
        var chapterTitles = await db.Nodes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .Select(n => new { n.Id, n.Title })
            .ToDictionaryAsync(x => x.Id, x => x.Title ?? "", ct);

        // Strip inline entity-GUID tags (corpus-trust-recovery Phase 1a) before this text ever
        // reaches an LLM prompt or QuotedEvidenceAppearsInBeat's literal substring match — a tag
        // sitting inside a cited quote span would otherwise break that Contains check and turn a
        // true, correctly-cited finding into a false negative.
        var beats = beatRows.Select(b => new AuditBeat(
            b.Id, b.Number, BeatMarkup.StripEntityTags(b.Text), b.SortKey,
            chapterTitles.TryGetValue(b.NodeId, out var chTitle) ? chTitle : "")).ToList();

        // A few distinctive disabled-beat snippets so OrphanReferencesRule can spot a live beat
        // still referencing something a cut beat established — an approximation of the skill's
        // "grep every disabled beat's distinctive phrase" step, not a full replacement for it.
        // Strip tags before truncating (must materialize raw Text first — BeatMarkup.StripEntityTags
        // can't translate into the SQL Substring EF would otherwise generate for the old inline
        // truncation).
        // Same chapter-local SortKey tie problem as beatRows above: order by chapter position
        // first so the Take(40) sample spreads across the whole book instead of an arbitrary
        // tie-broken cluster from whichever chapters happen to share low SortKey values.
        var disabledSnippetsRows = await db.BeatNodes.AsNoTracking().Include(bn => bn.Beat)
            .Where(bn => nodeIds.Contains(bn.NodeId) && !true && bn.Beat != null && bn.Beat!.Text != null)
            .Select(bn => new { bn.NodeId, bn.SortKey, Text = bn.Beat!.Text })
            .ToListAsync(ct);
        var disabledSnippets = disabledSnippetsRows
            .OrderBy(b => chapterOrder.TryGetValue(b.NodeId, out var idx) ? idx : int.MaxValue)
            .ThenBy(b => b.SortKey)
            .Take(40)
            .Select(b => BeatMarkup.StripEntityTags(b.Text))
            .Select(t => t.Length > 200 ? t[..200] : t)
            .ToList();

        var plants = await plantPayoffs.GetByNodeAsync(nodeId, ct);

        var extra = new Dictionary<string, object?>
        {
            ["outline"]             = node.NodeOutline,
            ["plants"]            = plants,
            ["disabledSnippets"]  = disabledSnippets,
        };
        var ctx = new AuditContext(nodeId, node.UniverseId, BuildClampedProse(beats), beats, extra);

        IReadOnlyList<IAuditRule> rules =
        [
            new CausalityChainRule(),
            new KnowledgeStatesRule(),
            new TimelineRule(),
            new PlantPayoffRule(),
            new OrphanReferencesRule(),
            new OutlineAgreementRule(),
            new InsertedBeatDriftRule(),
        ];

        var verdicts = await auditRunner.RunAsync(
            "LOGICSWEEP", $"node:{node.Slug}", FindingCategory.Contradiction, rules, ctx, ct: ct);

        return new LogicSweepReport(nodeId, node.Slug, node.Title, beats.Count, verdicts);
    }

    /// <summary>
    /// Blast-radius mini re-check (2026-08-14) — the same five whole-book-agnostic dimensions as
    /// <see cref="RunAsync"/> (causality, knowledge states, timeline, plant/payoff, orphan refs,
    /// outline agreement), but scoped to a caller-supplied beat-ID subset instead of the whole
    /// book's prose. Exists so a fix pass (an --edit-beat, or the /logic-sweep skill's own Step 5)
    /// can verify its own side effects against its immediate neighbors in the SAME turn, instead
    /// of waiting for the next independent full-book sweep round to catch a regression it
    /// introduced — VIGL hit this repeatedly this session (the Ocipheus mis-fix, the Ch18 "two
    /// places at once" over-correction), each surviving multiple sweep rounds before being caught.
    /// Pair with <see cref="BlastRadiusService.GetBlastRadiusBeatIdsAsync"/> for the beat-ID set.
    ///
    /// <see cref="InsertedBeatDriftRule"/> is deliberately excluded — its "late-inserted beat"
    /// signal is computed from the WHOLE book's BeatNodes.SortKey grid (see FindInserted's
    /// remarks), which a cross-chapter blast-radius subset doesn't carry meaningfully.
    ///
    /// Findings are filed under a scope key distinct from the full sweep's ("beat:{anchorBeatId}:
    /// blast" vs. "node:{slug}") so this narrow check's delete-then-recreate lifecycle never
    /// collides with or purges the full-book sweep's own findings.
    /// </summary>
    public async Task<LogicSweepReport> RunNarrowAsync(
        Guid nodeId, IReadOnlyList<Guid> beatIds, Guid anchorBeatId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // IgnoreQueryFilters(): explicit nodeId, not an ambient scope (same bug class found and
        // fixed in BookArchiveService.ArchiveAsync/WalkAsync, 2026-08-17).
        var node = await db.Nodes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new InvalidOperationException($"Node {nodeId} not found.");

        if (beatIds.Count == 0)
            return new LogicSweepReport(nodeId, node.Slug, node.Title, 0, []);

        var beatRows = await db.Beats.AsNoTracking()
            .Where(b => beatIds.Contains(b.Id) && b.Text != null && b.Text != "")
            .Select(b => new { b.Id, b.Number, b.Text })
            .ToListAsync(ct);
        if (beatRows.Count == 0)
            return new LogicSweepReport(nodeId, node.Slug, node.Title, 0, []);

        // Chapter titles for this blast-radius set. A blast radius is deliberately CROSS-chapter,
        // so chapter attribution matters more here than in the full sweep, not less — see
        // AuditBeat.ChapterTitle. A beat linked to several nodes takes its lowest-SortKey link,
        // matching the reading-order position the full sweep would give it.
        var chapterTitles = await db.BeatNodes.AsNoTracking()
            .Where(bn => beatIds.Contains(bn.BeatId))
            .OrderBy(bn => bn.SortKey)
            .Join(db.Nodes.IgnoreQueryFilters(), bn => bn.NodeId, n => n.Id,
                (bn, n) => new { bn.BeatId, n.Title })
            .ToListAsync(ct);
        var chapterTitleByBeat = chapterTitles
            .GroupBy(x => x.BeatId)
            .ToDictionary(g => g.Key, g => g.First().Title ?? "");

        // Ordered by Number (a stable, book-wide reading-order proxy) — SortKey isn't meaningful
        // across a cross-chapter blast-radius set the way it is within a single chapter.
        // Strip inline entity-GUID tags — same reason as the sibling query above.
        var beats = beatRows.OrderBy(b => b.Number)
            .Select((b, i) => new AuditBeat(
                b.Id, b.Number, BeatMarkup.StripEntityTags(b.Text), i,
                chapterTitleByBeat.TryGetValue(b.Id, out var chTitle) ? chTitle : "")).ToList();

        var plants = await plantPayoffs.GetByNodeAsync(nodeId, ct);
        var extra = new Dictionary<string, object?>
        {
            ["outline"]            = node.NodeOutline,
            ["plants"]           = plants,
            ["disabledSnippets"] = new List<string>(),
        };
        var ctx = new AuditContext(nodeId, node.UniverseId, BuildClampedProse(beats), beats, extra);

        IReadOnlyList<IAuditRule> rules =
        [
            new CausalityChainRule(),
            new KnowledgeStatesRule(),
            new TimelineRule(),
            new PlantPayoffRule(),
            new OrphanReferencesRule(),
            new OutlineAgreementRule(),
        ];

        var scopeKey = $"beat:{anchorBeatId:N}:blast";
        var verdicts = await auditRunner.RunAsync(
            "LOGICSWEEP-BLAST", scopeKey, FindingCategory.Contradiction, rules, ctx, ct: ct);

        return new LogicSweepReport(nodeId, node.Slug, node.Title, beats.Count, verdicts);
    }

    // ── Loop-until-dry convergence (2026-08-14) ─────────────────────────────────────

    /// <summary>
    /// Runs ONE sweep round as part of a "loop-until-dry" convergence campaign, tracking state
    /// in <see cref="NodeConvergenceState"/> so the campaign survives across sessions — this is
    /// what replaces "run the sweep N times regardless of what it found" (the user's own
    /// stated pattern this fix responds to: 5 rounds run, a 6th independent run still finding
    /// something new) with an actual convergence criterion.
    ///
    /// This is deliberately ONE round per call, not an internal while-loop: fixing a finding
    /// happens OUTSIDE this service (a human, the /logic-sweep skill's own fix-pass step, or an
    /// orchestrating agent) between rounds, so there is nothing for a tight in-process loop to
    /// wait on — calling this repeatedly across turns/sessions, with a fix pass in between, IS
    /// the loop.
    ///
    /// <b>The three outcomes:</b>
    /// <list type="bullet">
    /// <item><b>Skipped</b> — the book's content fingerprint hasn't changed since the last
    /// recorded round AND the required consecutive-dry-round count was already reached. No LLM
    /// calls made; report "already converged, nothing to do."</item>
    /// <item><b>Converged</b> — this round (plus however many immediately preceding it) reached
    /// <paramref name="requiredDryRounds"/> consecutive clean (zero-finding) rounds.</item>
    /// <item><b>Safety cap hit</b> — <paramref name="maxTotalRounds"/> rounds have run without
    /// converging. Filed as its own LOGICSWEEP-CONVERGENCE finding (escalate, don't loop
    /// forever) and the round counter resets so the next attempt — presumably after a
    /// structural rewrite of the offending section, not just another fix pass — gets a fresh
    /// budget.</item>
    /// </list>
    ///
    /// A round that finds anything (a "dirty" round) resets <c>ConsecutiveDryRounds</c> to 0
    /// even if the book's content hasn't otherwise changed — matching BlastRadiusService's own
    /// rationale that a fix pass is itself a source of risk, so convergence must be re-earned
    /// after every round that touched something, not just after the sweep happens to run clean
    /// once.
    /// </summary>
    public async Task<ConvergenceRoundResult> RunConvergenceRoundAsync(
        Guid nodeId, int requiredDryRounds = DefaultRequiredDryRounds, int maxTotalRounds = DefaultMaxTotalRounds,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        // IgnoreQueryFilters(): explicit nodeId, not an ambient scope (same bug class found and
        // fixed in BookArchiveService.ArchiveAsync/WalkAsync, 2026-08-17).
        var node = await db.Nodes.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new InvalidOperationException($"Node {nodeId} not found.");

        var fingerprint = await ComputeBookFingerprintAsync(db, nodeId, ct);
        var state = await db.NodeConvergenceStates.FirstOrDefaultAsync(s => s.NodeId == nodeId, ct);

        if (state != null && state.LastBookFingerprint == fingerprint && state.ConsecutiveDryRounds >= requiredDryRounds)
        {
            return new ConvergenceRoundResult(nodeId, node.Slug, Skipped: true, Converged: true, HitSafetyCap: false,
                state.ConsecutiveDryRounds, state.TotalRoundsRun, null,
                $"Already converged ({state.ConsecutiveDryRounds} consecutive dry rounds) and nothing has changed since the last round — skipping.");
        }

        // A book whose content changed since the last recorded state must re-earn convergence:
        // "converged" was a claim about the OLD text, not this one.
        var contentChanged = state == null || state.LastBookFingerprint != fingerprint;
        var consecutiveDry = contentChanged ? 0 : state!.ConsecutiveDryRounds;
        var totalRounds = (state?.TotalRoundsRun ?? 0) + 1;

        var report = await RunAsync(nodeId, ct);
        consecutiveDry = report.Findings.Count == 0 ? consecutiveDry + 1 : 0;
        var newFingerprint = await ComputeBookFingerprintAsync(db, nodeId, ct);

        var converged = consecutiveDry >= requiredDryRounds;
        var hitCap = !converged && totalRounds >= maxTotalRounds;

        if (state == null)
        {
            state = new NodeConvergenceState { NodeId = nodeId };
            db.NodeConvergenceStates.Add(state);
        }
        // Hitting the cap resets BOTH counters — the next attempt (presumably after a
        // structural rewrite, per the escalation finding below) gets a clean budget rather than
        // immediately re-tripping the same cap on its very next round.
        state.ConsecutiveDryRounds = hitCap ? 0 : consecutiveDry;
        state.TotalRoundsRun       = hitCap ? 0 : totalRounds;
        state.LastBookFingerprint  = newFingerprint;
        state.LastRoundAt          = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        findingsSvc.DeleteBySummaryPrefix($"node:{node.Slug}", "LOGICSWEEP-CONVERGENCE ");
        if (hitCap)
        {
            findingsSvc.Upsert($"node:{node.Slug}", chapterId: null, FindingCategory.StructuralFailure, FindingSeverity.Medium,
                $"LOGICSWEEP-CONVERGENCE [not-converging]: {totalRounds} sweep rounds run without reaching " +
                $"{requiredDryRounds} consecutive clean rounds — this book keeps surfacing new findings faster " +
                "than fix passes resolve them. Likely needs a structural rewrite of the offending section, not another fix pass.",
                snippet: null,
                suggestedFix: "Read the accumulated findings for a common thread (one scene, one character arc, one plot thread) and rewrite that section directly, rather than patching individual findings one at a time.");
        }

        var message = converged
            ? $"Converged after {totalRounds} total round(s) — {requiredDryRounds} consecutive clean rounds reached."
            : hitCap
                ? $"Safety cap hit at {totalRounds} rounds without converging — filed as its own finding. Round counters reset."
                : $"Round {totalRounds}: {report.Findings.Count} finding(s) this round, {consecutiveDry}/{requiredDryRounds} consecutive dry rounds so far.";

        return new ConvergenceRoundResult(nodeId, node.Slug, Skipped: false, converged, hitCap,
            consecutiveDry, totalRounds, report, message);
    }

    /// <summary>2026-08-30 addition — read-only convergence check for the publish-readiness gate
    /// (docs/LOGIC.md §9, condition 3): "is this book CURRENTLY at ≥N consecutive dry sweep
    /// rounds, against its book text as it stands right now" — same freshness check
    /// <see cref="RunConvergenceRoundAsync"/> uses in its own already-converged short-circuit
    /// (lines above), exposed standalone so a caller that only wants the status doesn't have to
    /// run (and persist) another sweep round just to ask the question.</summary>
    public async Task<bool> IsConvergedAsync(Guid nodeId, int requiredDryRounds = DefaultRequiredDryRounds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.NodeConvergenceStates.AsNoTracking().FirstOrDefaultAsync(s => s.NodeId == nodeId, ct);
        if (state == null || state.ConsecutiveDryRounds < requiredDryRounds) return false;
        var fingerprint = await ComputeBookFingerprintAsync(db, nodeId, ct);
        return state.LastBookFingerprint == fingerprint;
    }

    /// <summary>Book-wide content fingerprint: <see cref="Beat.ComputeHash"/> over the
    /// concatenation of every enabled beat's own ComputeHash, in the SAME order (leaf-descendant
    /// walk, ordered by BeatNodes.SortKey) <see cref="RunAsync"/> itself reads them in — any
    /// beat text change anywhere in the book changes this fingerprint. Computed from live
    /// Beat.Text directly rather than trusting the stored Beat.TextHash column, which can be
    /// null or stale for a beat that predates the stamping mechanism.</summary>
    private static async Task<string> ComputeBookFingerprintAsync(ProseDbContext db, Guid nodeId, CancellationToken ct)
    {
        var nodeIds = await NodeWorkbenchService.GetLeafDescendantIdsAsync(db, nodeId, ct);
        // Ordered chapter-then-SortKey, matching RunAsync's own fix below — raw SortKey alone
        // is chapter-local (every chapter's beats restart near the same values), so ordering
        // the whole book by it ties across chapters. A tie order SQL Server doesn't guarantee
        // stable would make this fingerprint flap between two runs of unchanged content,
        // defeating the whole point of a hash-gated "did anything change" check.
        var chapterOrder = nodeIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var rows = await db.BeatNodes.AsNoTracking()
            .Where(bn => nodeIds.Contains(bn.NodeId) && true && bn.Beat != null
                      && bn.Beat!.Text != null && bn.Beat.Text != "")
            .Select(bn => new { bn.NodeId, bn.SortKey, Text = bn.Beat!.Text })
            .ToListAsync(ct);
        var texts = rows
            .OrderBy(b => chapterOrder.TryGetValue(b.NodeId, out var idx) ? idx : int.MaxValue)
            .ThenBy(b => b.SortKey)
            .Select(b => b.Text);
        var combined = string.Join("|", texts.Select(Beat.ComputeHash));
        return Beat.ComputeHash(combined);
    }

    // ── Shared JSON-array parsing for all six dimensions ──────────────────────────

    /// <summary>Every dimension asks for the same finding shape — a JSON array of
    /// {beat_number, severity, evidence, fix} — so there is one parser instead of six.</summary>
    /// <summary>Beat-number-aware alternative to <see cref="AuditProseUtils.ClampProse"/> for an
    /// oversized book: keeps the same head+tail 50k-char scheme, but names the actual elided
    /// beat-number range in the placeholder and tells the model not to cite any beat inside it.
    /// A model handed "[... middle elided ...]" with no further detail has no way to know which
    /// "[Beat #N]" headers it never received, and — confirmed on VIGL's 2026-08-14 sweep —
    /// sometimes fabricates a plausible-sounding quote for one rather than staying silent. This
    /// is the prevention half of the fix; <see cref="QuotedEvidenceAppearsInBeat"/> is the
    /// post-hoc catch for whatever gets through anyway.</summary>
    internal static string BuildClampedProse(IReadOnlyList<AuditBeat> beats)
    {
        var full = string.Join("\n\n", beats.Select(b => $"{BeatHeader(b)}\n{b.Text}"));
        if (full.Length <= 100_000) return full;

        var head = full[..50_000];
        var tail = full[^50_000..];
        var visible = beats.Where(b => head.Contains(BeatHeader(b)) || tail.Contains(BeatHeader(b)))
            .Select(b => b.Number).ToHashSet();
        var elided = beats.Select(b => b.Number).Where(n => !visible.Contains(n)).ToList();
        var rangeNote = elided.Count == 0
            ? "an unspecified range of beats"
            : $"beats #{elided.Min()}-#{elided.Max()} ({elided.Count} beats)";

        return head
            + $"\n\n[... middle of the manuscript elided for length: {rangeNote} were NOT shown to "
            + "you. Do NOT report a finding citing any beat number in that range - you cannot see "
            + "its actual text, and any quote you attribute to it would be fabricated. ...]\n\n"
            + tail;
    }

    /// <summary>
    /// True when a returned entry is the model reporting a NON-defect — either a confirmation
    /// that prose and outline agree, or an admission it could not check something because the
    /// relevant beats weren't in its window. Neither is a finding, and persisting them is worse
    /// than useless: a reader who sees a BLOCKER whose evidence says "this matches the outline
    /// exactly" stops believing the other findings too.
    ///
    /// Phrase-matched deliberately narrowly, on explicit VERDICT language rather than anything
    /// that merely mentions agreement, so a genuine finding that happens to note "beat X is
    /// consistent, but beat Y contradicts it" survives. The tradeoff is accepted knowingly: the
    /// cost of wrongly dropping one finding is that the next sweep round finds it again, while
    /// the cost of persisting confirmations is that every finding becomes untrustworthy. If a
    /// real finding is ever suppressed here, the phrase that did it is in this list.
    /// </summary>
    internal static bool IsSelfDeclaredNonFinding(string evidence, string? fix)
    {
        var hay = (evidence + " " + (fix ?? "")).ToLowerInvariant();
        string[] verdicts =
        [
            "no contradiction",
            "not a contradiction",
            "no fix needed",
            "no fix required",
            "no action needed",
            "no action required",
            "this is not a defect",
            "is not a defect",
            "cannot verify",
            "could not verify",
            "unable to verify",
            "not visible in the beats provided",
            "beats provided do not",
            "were not provided",
            "not provided in the beats",
            "matches the bible",
            "prose is consistent with the bible",
            "consistent with the bible's",
            "matches the outline",
            "prose is consistent with the outline",
            "consistent with the outline's",
        ];
        if (verdicts.Any(v => hay.Contains(v, StringComparison.Ordinal))) return true;

        // Softer shape, gated on there being no fix proposed: an entry that merely concludes
        // "…which is consistent with X" and offers nothing to do is a confirmation wearing a
        // finding's clothes. Requiring the absent fix keeps a genuine "beat 12 is consistent, but
        // beat 14 contradicts it" finding — which always carries a fix — out of the filter.
        if (string.IsNullOrWhiteSpace(fix))
        {
            string[] soft = ["is consistent with", "are consistent with", "reads as consistent"];
            if (soft.Any(s => hay.Contains(s, StringComparison.Ordinal))) return true;
        }

        return false;
    }

    /// <summary>The per-beat header the audit prompts see. Carries the chapter title when known
    /// ("[Beat #3033 | Chapter 30 — The Gray Suit]") so a rule comparing prose against a
    /// chapter-keyed outline passage can tell which chapter it is actually reading — Beat.Number alone
    /// does not track chapter order on a large or restructured book. Falls back to the bare
    /// "[Beat #N]" form when no chapter title is available, which is also what every existing
    /// test fixture and the narrow blast-radius path produce.</summary>
    internal static string BeatHeader(AuditBeat b) =>
        string.IsNullOrWhiteSpace(b.ChapterTitle)
            ? $"[Beat #{b.Number}]"
            : $"[Beat #{b.Number} | {b.ChapterTitle}]";

    internal static IReadOnlyList<AuditVerdict> ParseFindingsArray(
        string ruleKey, string title, string raw, IReadOnlyList<AuditBeat> beats)
    {
        try
        {
            var start = raw.IndexOf('[');
            var end   = raw.LastIndexOf(']');
            if (start < 0 || end < start) return [];
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var results = new List<AuditVerdict>();
            foreach (var f in doc.RootElement.EnumerateArray())
            {
                try
                {
                    // beat_number is documented to the LLM as "<int or null>" for whole-book
                    // findings (plant/payoff, outline agreement) — JsonElement.TryGetInt32 THROWS
                    // InvalidOperationException on a JSON null (it doesn't fail soft like the
                    // name implies), so this must check ValueKind first or one whole-book finding
                    // silently discards every other finding in the same response via the outer
                    // catch below.
                    var beatNumber = f.TryGetProperty("beat_number", out var bn)
                        && bn.ValueKind == JsonValueKind.Number
                        && bn.TryGetInt32(out var n) ? n : (int?)null;
                    var citedBeat = beatNumber.HasValue
                        ? beats.FirstOrDefault(b => b.Number == beatNumber.Value)
                        : null;
                    var location = citedBeat?.Id.ToString();
                    var severity = f.TryGetProperty("severity", out var sv) ? sv.GetString()?.ToUpperInvariant() : null;
                    severity = severity is "BLOCKER" or "MODERATE" or "MINOR" or "DEVIATION" ? severity : "MODERATE";
                    var evidence = f.TryGetProperty("evidence", out var ev) ? ev.GetString() ?? "" : "";
                    if (evidence.Length == 0) continue; // don't persist an empty/malformed entry
                    // 2026-08-14 VIGL session bug: ClampProse (AuditProseUtils) keeps only the
                    // first+last 50k chars of an oversized book's concatenated prose, eliding the
                    // whole middle — but this lookup resolves beat_number against the FULL,
                    // unclamped `beats` list, so a model can cite a beat whose real text it never
                    // saw and still get a valid BeatId back, making a fabricated quote
                    // indistinguishable from a real finding (confirmed: beat #5656 was cited with
                    // a quote that does not exist anywhere in that beat's actual Text). Guard: if
                    // the evidence double-quotes specific text, at least one quoted phrase must
                    // actually appear in the cited beat, or the finding is a hallucinated citation
                    // and gets dropped rather than persisted as a false finding.
                    if (citedBeat != null && !QuotedEvidenceAppearsInBeat(evidence, citedBeat.Text))
                        continue;
                    var fix = f.TryGetProperty("fix", out var fx) ? fx.GetString() : null;
                    // Post-hoc catch for self-declared non-findings, the companion to the prompt's
                    // "an entry is a defect or it does not exist" rule (prevention) — same pairing
                    // as the hallucinated-citation guard above. Models persistently return
                    // CONFIRMATIONS ("this matches the outline exactly", "no contradiction") and
                    // NON-VERIFICATIONS ("cannot verify — those beats weren't provided") as
                    // findings, sometimes at BLOCKER severity: a 2026-08-24 VIGL round filed a
                    // BLOCKER whose own evidence concluded "the prose is consistent with the
                    // outline's locked kill choreography." Persisting those destroys trust in the
                    // whole instrument, which is what made every prior report say "don't run
                    // --until-dry on this book."
                    if (IsSelfDeclaredNonFinding(evidence, fix)) continue;
                    var evidenceWithBeat = beatNumber.HasValue ? $"Beat #{beatNumber}: {evidence}" : evidence;
                    results.Add(new AuditVerdict(ruleKey, title, severity, evidenceWithBeat, location, fix));
                }
                catch
                {
                    // One malformed entry must not discard every other finding in the same
                    // response — skip just this entry, not the whole array.
                }
            }
            return results;
        }
        catch { return []; }
    }

    /// <summary>True if the evidence contains no quoted text (nothing to verify — a paraphrased
    /// finding is never rejected by this check), OR at least one quoted phrase of meaningful
    /// length actually appears in the cited beat's real text. False means every quote in the
    /// evidence is fabricated — the model cited a beat number but the specific words it
    /// attributed to that beat don't exist there, the signature of a beat whose text fell inside
    /// <see cref="AuditProseUtils.ClampProse"/>'s elided middle.
    ///
    /// Checks BOTH double- and single-quoted spans (2026-08-14 gap fix — a real VIGL round-5
    /// finding fabricated a whole scene for beat #14657 using single quotes throughout,
    /// e.g. 'worked her fingers back into the seam', and the double-quote-only version of this
    /// check let it straight through). The single-quote pattern requires non-word boundaries on
    /// both sides (<c>(?&lt;!\w)'...'(?!\w)</c>) specifically so it does NOT treat a possessive or
    /// contraction apostrophe — "Orim's", "wasn't" — as a quotation mark.
    ///
    /// Minimum quote length is 8 chars, not 15 (2026-08-14, second gap fix same day): a round-7
    /// finding misattributed beat #5590's real text to a fabricated "beat #5603" using only
    /// short quoted fragments ('authorized', 'did not exist' — 10 and 14 chars) that the
    /// original 15-char floor let through unverified. 8 is still long enough that a coincidental
    /// substring match across unrelated beats stays unlikely, while catching short-fragment
    /// misattribution like this one.</summary>
    internal static bool QuotedEvidenceAppearsInBeat(string evidence, string beatText)
    {
        var doubleQuoted = Regex.Matches(evidence, "\"([^\"]{8,})\"").Select(m => m.Groups[1].Value);
        var singleQuoted = Regex.Matches(evidence, @"(?<!\w)'([^']{8,})'(?!\w)").Select(m => m.Groups[1].Value);
        var quotes = doubleQuoted.Concat(singleQuoted)
            .Select(q => Regex.Replace(q, @"\s+", " ").Trim())
            .Where(q => q.Length > 0)
            .ToList();
        if (quotes.Count == 0) return true;
        var normalizedBeat = Regex.Replace(beatText, @"\s+", " ");
        return quotes.Any(q => normalizedBeat.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    // ── The six dimensions ────────────────────────────────────────────────────────

    sealed class CausalityChainRule : ILlmAuditRule
    {
        public string Key => "causality";
        public string Title => "Causality chain";
        public int MaxResponseTokens => 4096;

        public (string System, string User) BuildPrompt(AuditContext ctx) => (
            """
            You are auditing one dimension of a story: the CAUSALITY CHAIN.
            Every event must have an established cause; every decision, a motivation; every
            capability, an on-page origin. Find breaks: an effect with no shown cause, a
            decision the text gives no motivation for, a character doing something they were
            never shown able to do.

            IMPORTANT — revealed later is NOT the same as never established. Many books use
            nonlinear intercutting, flashback structure, or a deliberate gradual/withheld reveal
            (mystery pacing, dramatic irony, first-mention texture that pays off chapters later).
            Before flagging a missing cause or origin, check the WHOLE book you were given, not
            just the beats preceding the one you're looking at in reading order — if any beat
            anywhere in the text (earlier OR later) establishes the cause, motivation, or
            capability, this is not a violation, even if it reads as unexplained in the moment.
            Only flag when the text never establishes it anywhere, or when a later beat actively
            CONTRADICTS the earlier one rather than merely explaining it after the fact.

            Return ONLY a JSON array (no prose wrapper), one entry per real problem found:
            [{"beat_number": <int>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "cite what happens and why it has no established cause", "fix": "one concrete sentence or null"}]
            Return [] if the causality chain holds. Do not invent problems you cannot cite a
            specific beat for. When uncertain, err toward fewer findings.

            NOT A FINDING, EVER: an entry whose evidence concludes the text is fine ("this is
            consistent", "no contradiction", "no fix needed"), or that says you could not check
            something because those beats were not provided to you. Neither is a defect in the
            book. If you would write one, return [] instead — [] is a correct, common answer.
            """,
            $"Beats:\n{ctx.Prose}");
        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);
    }

    sealed class KnowledgeStatesRule : ILlmAuditRule
    {
        public string Key => "knowledge_states";
        public string Title => "Knowledge states";
        public int MaxResponseTokens => 4096;

        public (string System, string User) BuildPrompt(AuditContext ctx) => (
            """
            You are auditing one dimension of a story: KNOWLEDGE STATES.
            Track who knows what, and when they learned it. Nobody may act on knowledge they
            have not yet been shown to possess — a character referencing a fact, name, or event
            before the text establishes they learned it is a violation.

            IMPORTANT — revealed later is NOT the same as never established. Many books use
            nonlinear intercutting, flashback structure, or a deliberate gradual/withheld reveal
            (mystery pacing, dramatic irony, first-mention texture that pays off chapters later).
            Before flagging a character acting on unestablished knowledge, check the WHOLE book
            you were given, not just the beats preceding the one you're looking at in reading
            order — if any beat anywhere in the text (earlier OR later) shows how/when that
            character learned it, or the book's own structure makes clear this is a flash-forward
            or intentionally withheld origin, this is not a violation. Only flag when the
            knowledge is never grounded anywhere in the full text, or when the timeline is
            genuinely impossible (they could not have learned it by any account, not merely that
            the text delays telling the reader how).

            Return ONLY a JSON array (no prose wrapper), one entry per real problem found:
            [{"beat_number": <int>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "name who acts on knowledge they shouldn't have and cite what they say/do", "fix": "one concrete sentence or null"}]
            Return [] if knowledge states are consistent. Do not invent problems you cannot cite
            a specific beat for. When uncertain, err toward fewer findings.

            NOT A FINDING, EVER: an entry whose evidence concludes the text is fine ("this is
            consistent", "no contradiction", "no fix needed"), or that says you could not check
            something because those beats were not provided to you. Neither is a defect in the
            book. If you would write one, return [] instead — [] is a correct, common answer.
            """,
            $"Beats:\n{ctx.Prose}");
        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);
    }

    sealed class TimelineRule : ILlmAuditRule
    {
        public string Key => "timeline";
        public string Title => "Timeline";
        public int MaxResponseTokens => 4096;

        public (string System, string User) BuildPrompt(AuditContext ctx) => (
            """
            You are auditing one dimension of a story: the TIMELINE.
            Reconstruct the book's internal clock from every date, duration, age, and
            "N days/months/years" claim in the text. Find impossibilities: a claimed year
            that's after or before the story's own established present, an age that
            contradicts a stated birth year or tenure, two elapsed-time claims that can't both
            be true, an event cited as happening before the character doing the citing could
            have known about it.

            Return ONLY a JSON array (no prose wrapper), one entry per real problem found:
            [{"beat_number": <int>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "quote the conflicting time claims and do the arithmetic", "fix": "one concrete sentence or null"}]
            Return [] if the timeline holds. Do not invent problems you cannot cite a specific
            beat for. When uncertain, err toward fewer findings.

            NOT A FINDING, EVER: an entry whose evidence concludes the text is fine ("this is
            consistent", "no contradiction", "no fix needed"), or that says you could not check
            something because those beats were not provided to you. Neither is a defect in the
            book. If you would write one, return [] instead — [] is a correct, common answer.
            """,
            $"Beats:\n{ctx.Prose}");
        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);
    }

    sealed class PlantPayoffRule : ILlmAuditRule
    {
        public string Key => "plant_payoff";
        public string Title => "Plant/payoff ledger";
        public int MaxResponseTokens => 4096;

        public (string System, string User) BuildPrompt(AuditContext ctx)
        {
            var plants = ctx.Extra.TryGetValue("plants", out var p) ? (List<PlantPayoff>)p! : [];
            var registry = plants.Count > 0
                ? "\n\nRegistered plant/payoff pairs for this node:\n" + string.Join("\n", plants.Select(pl =>
                    $"  [{pl.Category}] PLANT: {pl.PlantDescription} | PAYOFF: {pl.PayoffDescription} | " +
                    $"plant beat set: {pl.PlantBeatId != null} | payoff beat set: {pl.PayoffBeatId != null}"))
                : "\n\n(No plants registered for this node.)";
            return (
                $$"""
                You are auditing one dimension of a story: the PLANT/PAYOFF LEDGER, checked
                TWO WAYS. Every plant (a detail seeded early) must pay off later; every payoff
                (a reveal, a callback, a "of course") must have been genuinely planted earlier,
                not asserted cold. Cross-reference the registered plant/payoff pairs below
                against the actual prose — a registered plant whose payoff beat is unset is a
                candidate orphan; a payoff whose plant beat is unset needs the prose checked for
                whether it was actually seeded on the page.{{registry}}

                Return ONLY a JSON array (no prose wrapper), one entry per real problem found:
                [{"beat_number": <int or null>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "name the plant or payoff and what's missing on which side", "fix": "one concrete sentence or null"}]
                Return [] if the ledger is clean both ways. Do not invent problems you cannot
                cite a specific beat or registered pair for. When uncertain, err toward fewer
                findings.

                NOT A FINDING, EVER: an entry whose evidence concludes the text is fine ("this
                is consistent", "no contradiction", "no fix needed"), or that says you could not
                check something because those beats were not provided to you. Neither is a defect
                in the book. If you would write one, return [] instead — [] is a correct answer.
                """,
                $"Beats:\n{ctx.Prose}");
        }
        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);
    }

    sealed class OrphanReferencesRule : ILlmAuditRule
    {
        public string Key => "orphan_refs";
        public string Title => "Orphan references";
        public int MaxResponseTokens => 4096;

        public (string System, string User) BuildPrompt(AuditContext ctx)
        {
            var disabled = ctx.Extra.TryGetValue("disabledSnippets", out var d) ? (List<string>)d! : [];
            var disabledBlock = disabled.Count > 0
                ? "\n\nSnippets from CUT/DISABLED beats (no longer part of the book — flag any live beat that still depends on or references content that only appeared in one of these):\n"
                  + string.Join("\n---\n", disabled)
                : "\n\n(No disabled beats recorded for this node.)";
            return (
                $$"""
                You are auditing one dimension of a story: ORPHAN REFERENCES.
                Find anything in the live prose that references content which no longer exists
                in the book — a name, object, or event that was apparently cut, merged, or
                renamed elsewhere, leaving a dangling reference behind (e.g. a character
                mentioned once and never again in a way that reads like a plan changed
                mid-draft, not a deliberate open thread).{{disabledBlock}}

                Return ONLY a JSON array (no prose wrapper), one entry per real problem found:
                [{"beat_number": <int>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "cite the dangling reference and what it seems to be missing", "fix": "one concrete sentence or null"}]
                Return [] if there are no orphan references. Do not flag a deliberately
                unresolved mystery as an orphan reference — only flag what reads like leftover
                debris from a cut plan. When uncertain, err toward fewer findings.

                NOT A FINDING, EVER: an entry whose evidence concludes the text is fine ("this
                is consistent", "no contradiction", "no fix needed"), or that says you could not
                check something because those beats were not provided to you. Neither is a defect
                in the book. If you would write one, return [] instead — [] is a correct answer.
                """,
                $"Beats:\n{ctx.Prose}");
        }
        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);
    }

    sealed class OutlineAgreementRule : ILlmAuditRule
    {
        public string Key => "outline_agreement";
        public string Title => "Outline agreement";
        public int MaxResponseTokens => 4096;

        public (string System, string User) BuildPrompt(AuditContext ctx)
        {
            var outlineText = ctx.Extra.TryGetValue("outline", out var b) ? (string?)b : null;
            if (!string.IsNullOrWhiteSpace(outlineText)) outlineText = WithholdSubtextSections(outlineText!);
            var outlineBlock = string.IsNullOrWhiteSpace(outlineText)
                ? "\n\n(No NodeOutline recorded for this node.)"
                : $"\n\nNode outline (hand-authored facts, arc, structural notes):\n{Clamp(outlineText!, 30000)}";
            return (
                $$"""
                You are auditing one dimension of a story: OUTLINE AGREEMENT.
                The prose and the node's hand-authored outline must tell the same story. Find
                contradictions — a fact the outline states that the prose contradicts, or prose
                that establishes something the outline doesn't know about and should.
                No side is automatically authoritative (Outline ⇄ Book ⇄ Entities is a three-way
                symbiosis; Trinity reconciliation is the canonical case-by-case arbiter). For each
                contradiction, say which side you think is stale and WHY, citing evidence from
                both — never apply a blanket "prose wins" or "outline wins" rule.{{outlineBlock}}

                CHAPTER ATTRIBUTION — READ THIS BEFORE COMPARING ANYTHING.
                Each beat below is labeled "[Beat #N | Chapter Title]". The beat NUMBER is a
                global id: it is NOT chapter-local and does NOT tell you which chapter a beat is
                in, nor its reading order. Only the chapter title in that header does.
                The outline describes its scenes BY CHAPTER. So before reporting that a beat
                contradicts an outline passage, confirm the beat's own chapter title matches the
                chapter the outline passage is talking about. If the outline describes a scene as
                happening in one chapter and the beat you are looking at is labeled a different
                chapter, those are two DIFFERENT scenes — that is not a contradiction, and
                reporting it as one is the single most common false positive on this dimension.
                If a beat carries no chapter title, do not guess its chapter from its number.

                AN ENTRY IS A DEFECT OR IT DOES NOT EXIST. Two kinds of non-finding keep getting
                reported; never emit either one:
                1. CONFIRMATIONS. If outline and prose agree, say nothing at all about it. Do not
                   emit an entry whose evidence concludes "this is consistent" or "no fix needed".
                2. NON-VERIFICATION. You are shown an excerpt, not the whole book. If checking an
                   outline claim would need a beat you were not given, STAY SILENT about it. Do not
                   emit an entry saying you "cannot verify", that a beat "is not visible in the
                   beats provided", or asking to be given more beats. A gap in what you were shown
                   is not a defect in the book, and reporting it as one is a false positive.
                3. SUBTEXT DOCTRINE. An outline passage that is marked as never stated on the page
                   ("never stated on page", "unstated but legible", "never confirmed by any
                   character or by narration", "author ruling ... reveal mechanism", "the reader
                   assembles it in hindsight") describes what a reader should be able to INFER,
                   not what the prose must SAY. For such passages the prose complying means the
                   prose stays silent. Never report "the prose does not explain X", "the prose
                   treats X as ambiguous", or propose adding a line that states the mechanism —
                   that would break the doctrine the outline itself lays down. Report against
                   such a passage ONLY if the prose states the opposite outright, or if the
                   outline says a specific clue must be on the page and it is not. (Found live
                   2026-09-05: four consecutive sweep rounds filed the same §1c "the prose leaves
                   the loop ambiguous" finding against a section whose first line is that the loop
                   is never stated; each one was a false positive.)
                Report a contradiction only when you can see BOTH sides of it in front of you: the
                outline text, and the prose that actually conflicts with it.

                Return ONLY a JSON array (no prose wrapper), one entry per real problem found:
                [{"beat_number": <int or null>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "quote the outline claim and the contradicting prose, name the beat's chapter and the outline passage's chapter, and say which side appears stale", "fix": "one concrete sentence or null"}]
                Return [] if outline and prose agree, or if you were not shown enough to judge.
                Returning [] is a correct and common answer. Do not invent problems you cannot
                cite specific outline text and prose for. When uncertain, err toward fewer findings.
                """,
                $"Beats:\n{ctx.Prose}");
        }
        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);

        static string Clamp(string s, int max) => s.Length <= max ? s : s[..max] + "\n[...elided...]";
    }

    // An outline section whose HEADING declares its content is never stated on the page is
    // reveal doctrine — instructions to the writer about what the reader must be able to infer
    // and what the prose must NOT say. Comparing prose against it can only ever produce
    // "the prose does not explain X" findings, which are the doctrine working as designed. The
    // prompt rule (SUBTEXT DOCTRINE) was not enough: after it shipped, BCODA rounds 2–3 still
    // filed §1c findings from fresh angles each time (2026-09-05). Withholding the section is
    // deterministic; the model sees a one-line stub so it knows the section exists.
    static readonly System.Text.RegularExpressions.Regex SubtextHeading =
        new(@"never stated|unstated but legible|reveal mechanism|never confirmed on( the)? page",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string WithholdSubtextSections(string outline)
    {
        var sb = new System.Text.StringBuilder();
        int skipLevel = 0;
        foreach (var raw in outline.Replace("\r", "").Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^(#{2,6})\s");
            if (m.Success)
            {
                int lvl = m.Groups[1].Value.Length;
                if (skipLevel > 0 && lvl <= skipLevel) skipLevel = 0;
                if (skipLevel == 0 && SubtextHeading.IsMatch(raw))
                {
                    skipLevel = lvl;
                    // Neutral stub. The earlier wording ("the prose is required NOT to state it")
                    // read to the model as a rule to enforce, and it filed a MODERATE against BCODA's
                    // finale contact beat (#5220) for *stating* the entity's nature — the mirror image
                    // of the absence-findings this method exists to stop (2026-09-05). What the withheld
                    // section says on the page is the author's call, not this dimension's.
                    sb.Append(raw).Append("  [section withheld from this dimension — authorial subtext, not on-page claims; make no finding about its absence or its presence]").Append('\n');
                    continue;
                }
            }
            if (skipLevel == 0) sb.Append(raw).Append('\n'); // explicit LF: AppendLine would emit CRLF on Windows and re-introduce the \r stripped above
        }
        return sb.ToString();
    }

    /// <summary>
    /// Seventh dimension (extends docs/LOGIC.md's original six): INSERTED-BEAT DRIFT.
    ///
    /// <b>Why this exists.</b> Beats written in one pass sit on an even <c>BeatNodes.SortKey</c>
    /// grid (50, 100, 150…). A beat added later is wedged between two existing ones at their
    /// midpoint, so repeated insertion leaves a binary-subdivision trail — 75, then 87.5, then
    /// 93.75, then 96.875. Those inserted beats are written against a neighbourhood that is
    /// already fixed, and nothing forces the author to re-read what the surrounding prose already
    /// asserted. That is precisely where state contradictions accumulate, and the other six
    /// dimensions miss them because each reads the book as a flat sequence with no memory of
    /// which beats arrived late.
    ///
    /// Empirically (TRNY, 2026-08-02) three real defects survived THREE full logic sweeps and all
    /// three sat on fractional keys: a character holding "three coins" at 93.75/96.875 after the
    /// coins were stolen on-page at 50 and while 100 said "empty pockets"; a beat at 96.875
    /// recalling a guard encounter "an hour ago" that does not occur until 100–200; and a scene
    /// that was chronologically first sorting last, contradicting three beats about where an
    /// object was stored.
    ///
    /// <b>Selection is deterministic, judgement is not.</b> Which beats arrived late is a fact
    /// about the sort keys and is computed here in code — no model is asked to guess it. Only the
    /// narrow question "does this late beat contradict its fixed neighbours" goes to the LLM, and
    /// only the late beats plus their bracketing neighbours are sent, not the whole book. When a
    /// node has no inserted beats the rule costs one trivial call and returns [].
    /// </summary>
    internal sealed class InsertedBeatDriftRule : ILlmAuditRule
    {
        public string Key => "inserted_beat_drift";
        public string Title => "Inserted-beat drift";
        public int MaxResponseTokens => 4096;

        /// <summary>
        /// A beat is "late-inserted" when its gap to BOTH neighbours is at most half the node's
        /// original grid spacing — i.e. it was squeezed in rather than laid down with the run.
        ///
        /// <b>The grid estimate is the median of the LARGER HALF of gaps, not the median of all
        /// of them.</b> Insertions contribute their own small gaps, so on a heavily-subdivided
        /// node they outnumber the original spacing and drag a plain median down below the very
        /// insertions being looked for — with TRNY Ch4's real keys (50, 75, 87.5, 93.75, 96.875,
        /// 100, 150, 200, 250) a plain median gives 25, which silently misses 75 and 87.5, the
        /// first two insertions. Taking the upper half recovers the true 50-step grid. It is also
        /// steadier than max-gap, which one unusually long jump would blow out.
        ///
        /// Comparison is <c>&lt;=</c> half the grid so a FIRST insertion — landing on the exact
        /// midpoint, gaps of exactly grid/2 on both sides — is caught; that is the single most
        /// common case.
        ///
        /// Deliberately NOT "SortKey has a fractional part": a node whose original grid is 0.5, or
        /// which has been wholly renumbered, would false-positive on every beat, and a node using
        /// integer midpoints (100, 150, 175) would false-negative. Relative spacing is the signal.
        /// Endpoints are never candidates — a first or last beat has only one neighbour.
        /// </summary>
        internal static IReadOnlyList<AuditBeat> FindInserted(IReadOnlyList<AuditBeat> beats)
        {
            if (beats.Count < 4) return [];
            var ordered = beats.OrderBy(b => b.SortKey).ToList();

            var gaps = new List<double>();
            for (var i = 1; i < ordered.Count; i++) gaps.Add(ordered[i].SortKey - ordered[i - 1].SortKey);
            var positive = gaps.Where(g => g > 0).OrderBy(g => g).ToList();
            if (positive.Count == 0) return [];

            var upperHalf = positive.Skip(positive.Count / 2).ToList();
            var grid = upperHalf[upperHalf.Count / 2];
            if (grid <= 0) return [];

            var threshold = grid / 2.0;
            var hits = new List<AuditBeat>();
            for (var i = 1; i < ordered.Count - 1; i++)
            {
                var before = ordered[i].SortKey - ordered[i - 1].SortKey;
                var after  = ordered[i + 1].SortKey - ordered[i].SortKey;
                if (before <= threshold && after <= threshold) hits.Add(ordered[i]);
            }
            return hits;
        }

        /// <summary>The fixed beats immediately before and after each inserted run — the state a
        /// late beat must not contradict.</summary>
        internal static IReadOnlyList<AuditBeat> FindAnchors(
            IReadOnlyList<AuditBeat> beats, IReadOnlyList<AuditBeat> inserted)
        {
            var insertedIds = inserted.Select(b => b.Id).ToHashSet();
            var ordered = beats.OrderBy(b => b.SortKey).ToList();
            var anchors = new List<AuditBeat>();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (insertedIds.Contains(ordered[i].Id)) continue;
                var touchesRun = (i > 0 && insertedIds.Contains(ordered[i - 1].Id))
                              || (i < ordered.Count - 1 && insertedIds.Contains(ordered[i + 1].Id));
                if (touchesRun) anchors.Add(ordered[i]);
            }
            return anchors;
        }

        public (string System, string User) BuildPrompt(AuditContext ctx)
        {
            var inserted = FindInserted(ctx.Beats);
            if (inserted.Count == 0)
                return ("Reply with exactly: []", "No inserted beats to check. Reply []");

            var anchors = FindAnchors(ctx.Beats, inserted);
            var insertedIds = inserted.Select(b => b.Id).ToHashSet();
            var window = inserted.Concat(anchors).OrderBy(b => b.SortKey)
                .Select(b => $"[Beat #{b.Number}] ({(insertedIds.Contains(b.Id) ? "INSERTED LATER" : "pre-existing anchor")})\n{b.Text}");

            return (
                """
                You are auditing one dimension of a story: INSERTED-BEAT DRIFT.

                Some beats below are marked INSERTED LATER — they were added between beats that
                already existed. The others are pre-existing anchors. An inserted beat must not
                contradict the anchors around it.

                Check ONLY these, and only against the beats shown:
                1. STATE — money and possessions carried, injuries, what a character is holding or
                   wearing, where an object is stored, who is present, time of day.
                2. RETROSPECTIVE REFERENCES POINTING FORWARD — an inserted beat recalling an event
                   ("an hour ago…", "he remembered…") that only happens in a LATER beat.
                3. ORDERING — an inserted beat that can only be true before an anchor it is placed
                   after (or vice versa).

                Return ONLY a JSON array (no prose wrapper), one entry per real contradiction:
                [{"beat_number": <int>, "severity": "BLOCKER"|"MODERATE"|"MINOR", "evidence": "quote the inserted beat AND the anchor it contradicts", "fix": "one concrete sentence or null"}]
                Cite the specific inserted beat and the specific anchor text it conflicts with.
                Return [] if the inserted beats are consistent with their anchors. Do not report a
                difference that is merely later in time and consistent. When uncertain, return [].

                NOT A FINDING, EVER: an entry whose evidence concludes the text is fine ("this
                is consistent", "no contradiction", "no fix needed"), or that says you could not
                check something because those beats were not provided to you. Neither is a defect
                in the book. If you would write one, return [] instead — [] is a correct answer.
                """,
                $"Beats:\n{string.Join("\n\n", window)}");
        }

        public IReadOnlyList<AuditVerdict> ParseResponse(string raw, AuditContext ctx) => ParseFindingsArray(Key, Title, raw, ctx.Beats);
    }
}

public record LogicSweepReport(
    Guid NodeId, string NodeSlug, string NodeTitle, int BeatCount, IReadOnlyList<AuditVerdict> Findings)
{
    public int BlockerCount   => Findings.Count(f => f.Severity == "BLOCKER");
    public int ModerateCount  => Findings.Count(f => f.Severity == "MODERATE");
    public int MinorCount     => Findings.Count(f => f.Severity == "MINOR");
    public bool Clean         => Findings.Count == 0;
}

/// <summary>One round of a loop-until-dry convergence campaign — see
/// <see cref="LogicSweepService.RunConvergenceRoundAsync"/>.</summary>
public record ConvergenceRoundResult(
    Guid NodeId, string NodeSlug, bool Skipped, bool Converged, bool HitSafetyCap,
    int ConsecutiveDryRounds, int TotalRoundsRun, LogicSweepReport? Report, string Message);
