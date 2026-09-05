using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Prose.Core.Data;
using Prose.Core.Interfaces;

namespace Prose.Core.Services;

/// <summary>
/// A single Entity row that's part of a duplicate-name candidate group.
/// </summary>
public sealed record DuplicateEntityCandidate(
    Guid Id,
    string Name,
    Guid? OriginNodeId,
    string? DescriptionSnippet,
    int MentionCount);

/// <summary>
/// Two or more character Entities in the same universe whose names are identical or very
/// close, sharing the same disambiguation scope (both universe-wide, or the same OriginNodeId) —
/// meaning <see cref="EntityDisambiguationService"/>'s legitimate same-name-different-book
/// mechanism does not explain the overlap. A genuine candidate for the author to merge or
/// explicitly disambiguate.
/// </summary>
public sealed record DuplicateEntityGroup(
    string MatchedOn,
    IReadOnlyList<DuplicateEntityCandidate> Candidates);

/// <summary>
/// Deterministic scan for duplicate/near-duplicate character Entity rows — no LLM. Generalizes
/// a real bug found manually on 2026-08-10: TEST's protagonist "Bear" had two separate Entity
/// rows ("Boris Johansen" and "Boris Johanssen" — a one-letter spelling difference), seeded from
/// two different drafts of the same book and never reconciled. Nothing before this service could
/// surface that class of bug mechanically; it was found by hand-grepping beat text during a
/// cross-book story-weaving investigation.
///
/// Two detection passes, scoped to one EntityType at a time (default "character" — the
/// highest-value and by far the most numerous type, ~1,864 in GLMZ alone as of 2026-08-10; pass
/// a different type, e.g. "faction" or "place", to check those instead. Always single-type: a
/// full cross-type pairwise scan would be far more expensive for comparatively little narrative
/// payoff, since a character and a weapon sharing a name is never actually a duplicate row):
///   1. Exact match after normalizing whitespace/case — catches straightforward duplicates.
///   2. Near-duplicate — names exactly 1 edit apart (insert/delete/substitute one character,
///      e.g. "Johansen"/"Johanssen"), checked only between lexicographically adjacent entries
///      after sorting (a sliding window), which keeps the scan O(n log n) instead of O(n²)
///      pairwise comparisons across the whole universe. Deliberately tight — edit distance 2
///      produced heavy false-positive noise on the first live run against GLMZ ("Marco"/
///      "Marcus", "Pip"/"Piper", "Sable"/"Salve" — all genuinely different characters).
///
/// A pair is excluded (not a bug) when <see cref="Data.Entities.Entity.OriginNodeId"/> is set to
/// DIFFERENT non-null values on each candidate — that's exactly what OriginNodeId exists for
/// (see its doc comment and <see cref="EntityDisambiguationService"/>): two genuinely different
/// characters who happen to share a name across different books' continuity.
///
/// No LLM calls — fast, deterministic. Available via `prose --duplicate-entity-scan --universe
/// &lt;slug&gt;`.
/// </summary>
public class DuplicateEntityScanService(IDbContextFactory<ProseDbContext> dbFactory, ILlmService llm)
{
    // Distance 1 catches the real bug class this service exists for (a single added/changed/
    // removed character — "Johansen"/"Johanssen", "Ines"/"Inés") while staying quiet on
    // genuinely different short names that a looser threshold flags as noise (distance 2 alone
    // matched "Marco"/"Marcus", "Pip"/"Piper", "Sable"/"Salve", "Sine"/"Siren" against the live
    // GLMZ universe on first run, 2026-08-10 — all real, distinct characters, not duplicates).
    private const int MaxEditDistance = 1;
    private const int SlidingWindow = 5;

    private sealed record EntityRow(Guid Id, string Name, Guid? OriginNodeId, string? Description);

    public Task<IReadOnlyList<DuplicateEntityGroup>> ScanAsync(Guid universeId, CancellationToken ct = default) =>
        ScanAsync(universeId, "character", ct);

    /// <summary>
    /// Scans one EntityType within a universe. "character" is the default and highest-value
    /// target (see class doc comment), but the same bug class — two unreconciled draft rows for
    /// the same world object — applies to any type; "faction" and "place" are cheap to check
    /// (230 / 720 rows in GLMZ as of 2026-08-10) and narratively significant enough that a
    /// duplicate would matter as much as a character one.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateEntityGroup>> ScanAsync(Guid universeId, string entityType, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var entities = await db.Entities.AsNoTracking()
            .Where(e => e.UniverseId == universeId && e.EntityType == entityType)
            .Select(e => new EntityRow(e.Id, e.Name, e.OriginNodeId, e.Description))
            .ToListAsync(ct);

        if (entities.Count < 2) return [];

        var entityIds = entities.Select(e => e.Id).ToList();
        var mentionCounts = await db.BeatEntityMentions.AsNoTracking()
            .Where(m => entityIds.Contains(m.EntityId))
            .GroupBy(m => m.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EntityId, x => x.Count, ct);

        DuplicateEntityCandidate ToCandidate(EntityRow e) => new(
            e.Id, e.Name, e.OriginNodeId,
            e.Description == null ? null : Snippet(e.Description),
            mentionCounts.GetValueOrDefault(e.Id, 0));

        var groups = new List<DuplicateEntityGroup>();
        var alreadyGrouped = new HashSet<Guid>();

        // Pass 1: exact match after normalization.
        var byNormalized = entities
            .GroupBy(e => Normalize(e.Name))
            .Where(g => g.Count() > 1);

        foreach (var g in byNormalized)
        {
            var members = g.ToList();
            if (!SharesDisambiguationScope(members.Select(m => (Guid?)m.OriginNodeId))) continue;

            groups.Add(new DuplicateEntityGroup(
                $"exact match: \"{g.Key}\"",
                members.Select(m => ToCandidate(m)).ToList()));
            foreach (var m in members) alreadyGrouped.Add(m.Id);
        }

        // Pass 2: near-duplicate, sliding window over sorted normalized names.
        var sorted = entities
            .Where(e => !alreadyGrouped.Contains(e.Id))
            .OrderBy(e => Normalize(e.Name), StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            for (int j = i + 1; j < Math.Min(i + 1 + SlidingWindow, sorted.Count); j++)
            {
                var a = sorted[i];
                var b = sorted[j];
                if (alreadyGrouped.Contains(a.Id) || alreadyGrouped.Contains(b.Id)) continue;

                var na = Normalize(a.Name);
                var nb = Normalize(b.Name);
                if (na == nb) continue; // already covered by pass 1

                var distance = LevenshteinDistance(na, nb);
                if (distance == 0 || distance > MaxEditDistance) continue;
                if (!SharesDisambiguationScope([a.OriginNodeId, b.OriginNodeId])) continue;

                groups.Add(new DuplicateEntityGroup(
                    $"near match (edit distance {distance}): \"{a.Name}\" / \"{b.Name}\"",
                    [ToCandidate(a), ToCandidate(b)]));
                alreadyGrouped.Add(a.Id);
                alreadyGrouped.Add(b.Id);
            }
        }

        return groups;
    }

    /// <summary>
    /// Narrow, single-entity variant of <see cref="ScanAsync(Guid, string, CancellationToken)"/>
    /// for the write-gate's async post-save hook (project plan "Make Prose.Hub the real
    /// gatekeeper", 2026-08-22 Phase 0): after ONE entity is created or renamed, checks it against
    /// its own universe+type cohort for an exact or near-duplicate name, instead of re-scanning
    /// the whole corpus on every single write. Returns at most one group (this entity's own), not
    /// every duplicate group in the universe — callers that need the full picture still want
    /// <see cref="ScanAsync(Guid, string, CancellationToken)"/>.
    /// </summary>
    public async Task<IReadOnlyList<DuplicateEntityGroup>> CheckSingleEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var target = await db.Entities.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.Id == entityId)
            .Select(e => new { e.Id, e.Name, e.OriginNodeId, e.Description, e.UniverseId, e.EntityType })
            .FirstOrDefaultAsync(ct);
        if (target == null) return [];

        var cohort = await db.Entities.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.UniverseId == target.UniverseId && e.EntityType == target.EntityType && e.Id != target.Id)
            .Select(e => new EntityRow(e.Id, e.Name, e.OriginNodeId, e.Description))
            .ToListAsync(ct);
        if (cohort.Count == 0) return [];

        var targetNorm = Normalize(target.Name);
        var matches = new List<EntityRow>();
        string? matchKind = null;

        foreach (var candidate in cohort)
        {
            if (!SharesDisambiguationScope([target.OriginNodeId, candidate.OriginNodeId])) continue;
            var candNorm = Normalize(candidate.Name);
            if (candNorm == targetNorm)
            {
                matches.Add(candidate);
                matchKind ??= $"exact match: \"{targetNorm}\"";
            }
            else if (LevenshteinDistance(targetNorm, candNorm) <= MaxEditDistance)
            {
                matches.Add(candidate);
                matchKind ??= $"near match (edit distance <= {MaxEditDistance}): \"{target.Name}\" / \"{candidate.Name}\"";
            }
        }

        if (matches.Count == 0) return [];

        var targetRow = new EntityRow(target.Id, target.Name, target.OriginNodeId, target.Description);
        var allIds = matches.Select(m => m.Id).Append(target.Id).ToList();
        var mentionCounts = await db.BeatEntityMentions.AsNoTracking()
            .Where(m => allIds.Contains(m.EntityId))
            .GroupBy(m => m.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EntityId, x => x.Count, ct);

        DuplicateEntityCandidate ToCandidate(EntityRow e) => new(
            e.Id, e.Name, e.OriginNodeId,
            e.Description == null ? null : Snippet(e.Description),
            mentionCounts.GetValueOrDefault(e.Id, 0));

        var members = matches.Append(targetRow).Select(ToCandidate).ToList();
        return [new DuplicateEntityGroup(matchKind!, members)];
    }

    /// <summary>
    /// True when the candidates are NOT legitimately disambiguated by OriginNodeId — i.e. they
    /// share the same scope (all null, or all the same non-null value) rather than each pointing
    /// at a different book. Different non-null values means "different books, deliberately
    /// distinct characters" — not a bug.
    /// </summary>
    internal static bool SharesDisambiguationScope(IEnumerable<Guid?> originNodeIds)
    {
        var distinct = originNodeIds.Distinct().ToList();
        return distinct.Count == 1;
    }

    internal static string Normalize(string name) =>
        string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // ── broad LLM-assisted scan (report-only), 2026-08-17 ───────────────────────
    // ScanAsync above (exact / edit-distance-1) structurally cannot catch "Dame Lyra" vs.
    // "Dame Lyra of House Ocipheus" -- a different name entirely for the same person, confirmed
    // as a real, non-cosmetic corpus-wide problem (contradictory rank/role/biography, not just
    // redundant naming). Two design constraints drove this shape, not a hardcoded title list and
    // not one LLM call per candidate:
    //   - Must generalize across every universe/genre forever, so nothing here enumerates
    //     English titles/ranks/honorifics -- the LLM's own knowledge of naming conventions in any
    //     language/genre does that job instead.
    //   - Cost must scale with actual ambiguity found, not corpus size. Two-stage funnel:
    //     Stage 1 -- ONE bulk call per (universe, entityType): give the LLM just the flat list of
    //       names (no descriptions) and ask which look like the same entity under different
    //       names. Cheap and O(1) in call count regardless of roster size.
    //     Stage 2 -- one judgment call PER SURVIVING CLUSTER from stage 1 (expected: tens, not
    //       hundreds, per this session's manual duplicate-rate estimate) -- the only step that
    //       reads descriptions and makes the real same/different call.
    // Scoped to OriginNodeId == null rows only -- confirmed this session that's where ~99.5% of
    // rows live and where every real duplicate was found; a non-null OriginNodeId is
    // EntityDisambiguationService's legitimate same-name-different-book marker, not something
    // this scan should second-guess.
    //
    // Report-only: nothing is merged here. Feed high-confidence output to MergeAsync below only
    // after human review -- see the plan's own validation step before any merge execution exists.

    private const int MaxStage2DescriptionChars = 400;

    public sealed record BroadDuplicateVerdict(bool SameEntity, string Confidence, Guid? SuggestedWinnerId, string Reasoning);
    public sealed record BroadDuplicateGroup(IReadOnlyList<DuplicateEntityCandidate> Candidates, BroadDuplicateVerdict Verdict);

    private sealed record FullEntityRow(Guid Id, string Name, string? Description);

    public async Task<IReadOnlyList<BroadDuplicateGroup>> ScanBroadAsync(
        Guid universeId, string entityType = "character", CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var fullRows = await db.Entities.AsNoTracking()
            .Where(e => e.UniverseId == universeId && e.EntityType == entityType && e.OriginNodeId == null)
            .Select(e => new FullEntityRow(e.Id, e.Name, e.Description))
            .ToDictionaryAsync(e => e.Id, ct);

        if (fullRows.Count < 2) return [];

        var indexed = fullRows.Values.Select((e, i) => (Index: i, e.Id)).ToList();
        var namesBlock = string.Join('\n', indexed.Select(x => $"{x.Index}: {fullRows[x.Id].Name}"));

        var stage1System = """
            You are auditing a fiction database's character/entity roster for duplicate rows --
            the same real entity recorded more than once under different names (a title added, a
            rank/code suffix appended, a fuller or shorter form of the same name, a pluralization
            typo, etc.).

            You will get a numbered list of names, one per database row. Group the numbers that
            plausibly refer to the SAME real entity under different names. Use your own knowledge
            of titles, honorifics, ranks, epithets, and naming conventions across any language or
            genre -- do not assume English or any specific genre or setting.

            Rules:
            - Only output groups with 2 or more members. Omit every name with no plausible match.
            - Do not group two names just because they share one common word if the rest suggests
              genuinely different people (e.g. two unrelated characters who happen to share a
              common surname) -- a low-confidence group is fine, each one gets verified separately
              afterward; just don't invent a connection with nothing behind it.
            - Output STRICT JSON only, no prose, no markdown fence:
              {"groups": [[3, 17, 42], [8, 9]]} -- arrays of the row numbers given above.
            """;
        var stage1User = $"Rows:\n{namesBlock}\n\nGroup now.";

        var stage1Raw = await llm.GenerateAsync(stage1System, stage1User, temperature: 0.2, maxTokens: 4096, ct: ct);
        var indexGroups = ParseIndexGroups(stage1Raw, indexed.Count);
        if (indexGroups.Count == 0) return [];

        var allIds = fullRows.Keys.ToList();
        var mentionCounts = await db.BeatEntityMentions.AsNoTracking()
            .Where(m => allIds.Contains(m.EntityId))
            .GroupBy(m => m.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.EntityId, x => x.Count, ct);

        var results = new List<BroadDuplicateGroup>();
        foreach (var group in indexGroups)
        {
            var ids = group.Select(i => indexed[i].Id).ToList();
            var candidates = ids.Select(id =>
            {
                var row = fullRows[id];
                return new DuplicateEntityCandidate(
                    row.Id, row.Name, null,
                    row.Description == null ? null : Snippet(row.Description),
                    mentionCounts.GetValueOrDefault(row.Id, 0));
            }).ToList();

            var verdict = await JudgeClusterAsync(candidates, fullRows, ct);
            results.Add(new BroadDuplicateGroup(candidates, verdict));
        }

        return results;
    }

    private async Task<BroadDuplicateVerdict> JudgeClusterAsync(
        IReadOnlyList<DuplicateEntityCandidate> candidates,
        IReadOnlyDictionary<Guid, FullEntityRow> fullRows,
        CancellationToken ct)
    {
        const string stage2System = """
            You are auditing a fiction database for duplicate character/entity rows. You will be
            given 2 or more Entity rows that a bulk name-similarity pass flagged as PLAUSIBLY the
            same real entity recorded more than once. Decide, using ONLY the given names and
            descriptions -- never guess beyond what's given:

            - sameEntity: true if these rows describe the same real person/place/faction under
              different names or spellings; false if they are genuinely different entities that
              happen to share a name or part of one.
            - confidence: "high", "medium", or "low".
            - winnerId: if sameEntity is true, the id (given below) of whichever row has the most
              complete/specific description -- the one that should survive a merge. null otherwise.
            - reasoning: one or two sentences citing the specific facts that drove the decision.

            Output STRICT JSON only, no prose, no markdown fence:
            {"sameEntity": bool, "confidence": "high|medium|low", "winnerId": "guid-or-null", "reasoning": "..."}
            """;

        var rowsText = string.Join("\n\n", candidates.Select(c =>
        {
            var desc = fullRows[c.Id].Description;
            var capped = string.IsNullOrWhiteSpace(desc) ? "(none)"
                : desc.Length <= MaxStage2DescriptionChars ? desc : desc[..MaxStage2DescriptionChars].TrimEnd() + "…";
            return $"id={c.Id}\nname=\"{c.Name}\"\nmentionCount={c.MentionCount}\ndescription=\"{capped}\"";
        }));

        var raw = await llm.GenerateAsync(stage2System, rowsText, temperature: 0.1, maxTokens: 512, ct: ct);
        return ParseVerdict(raw);
    }

    private static List<List<int>> ParseIndexGroups(string raw, int maxIndexExclusive)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripCodeFence(raw));
            if (!doc.RootElement.TryGetProperty("groups", out var groupsEl) || groupsEl.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<List<int>>();
            foreach (var g in groupsEl.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Array) continue;
                var indices = g.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.GetInt32())
                    .Where(i => i >= 0 && i < maxIndexExclusive)
                    .Distinct()
                    .ToList();
                if (indices.Count >= 2) result.Add(indices);
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            // Malformed LLM output -- report nothing rather than crash a corpus-wide run on one
            // bad response; the caller can re-run for just this (universe, entityType).
            return [];
        }
    }

    private static BroadDuplicateVerdict ParseVerdict(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripCodeFence(raw));
            var root = doc.RootElement;
            // Defensive: despite the "output an object" instruction, some responses wrap it in a
            // single-element array -- unwrap rather than crash a whole corpus-wide run on one
            // model's formatting quirk.
            if (root.ValueKind == JsonValueKind.Array)
                root = root.EnumerateArray().FirstOrDefault();
            if (root.ValueKind != JsonValueKind.Object)
                return new BroadDuplicateVerdict(false, "low", null, "(judge response was not a JSON object -- treat as uncertain, needs human review)");

            var same = root.TryGetProperty("sameEntity", out var s) && s.ValueKind == JsonValueKind.True;
            var confidence = root.TryGetProperty("confidence", out var c) ? c.GetString() ?? "low" : "low";
            Guid? winnerId = root.TryGetProperty("winnerId", out var w) && w.ValueKind == JsonValueKind.String
                && Guid.TryParse(w.GetString(), out var wg) ? wg : null;
            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
            return new BroadDuplicateVerdict(same, confidence, winnerId, reasoning);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new BroadDuplicateVerdict(false, "low", null, "(unparseable judge response -- treat as uncertain, needs human review)");
        }
    }

    private static string StripCodeFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline >= 0) t = t[(firstNewline + 1)..];
        if (t.EndsWith("```")) t = t[..^3];
        return t.Trim();
    }

    // ── merge (AutoCorrect auto-fix surface, 2026-08-14) ──────────────────────

    public sealed record EntityMergeResult(Guid WinnerId, Guid LoserId, int RowsRelinked, int RowsDeletedForCollision, List<RowMutationUndo> UndoLog,
        int BeatsRetagged = 0, int OutlinesRetagged = 0);

    /// <summary>
    /// Merges <paramref name="loserId"/> into <paramref name="winnerId"/>: every real foreign-key
    /// reference to the loser (<see cref="EntityForeignKeyCatalog"/> — actual <c>sys.foreign_keys</c>
    /// metadata, not a name-pattern guess) is repointed at the winner, then the loser's own
    /// <c>Entities</c> row is physically deleted. Recoverable two ways: the generic undo ledger
    /// (<see cref="SelfHealLedgerService"/>, re-inserts from the captured JSON in <c>UndoLog</c>) for
    /// a specific run, or <c>Entities_History</c> directly (<c>Entities</c> is a system-versioned
    /// temporal table — <c>FOR SYSTEM_TIME AS OF</c> against any timestamp before the merge still
    /// returns the loser's full prior row) as a second, independent recovery path. No status flag is
    /// used or needed — the row's absence from the live table IS the fact of having been merged
    /// away (SS-A-temporal-hygiene: no IsActive/IsEnabled-style column on any versioned table).
    ///
    /// A few referencing tables enforce a 1:1 relationship with an Entity (e.g. <c>EntityEmbeddings</c>
    /// — one cached vector per entity); relinking would collide with the winner's own existing row.
    /// Detected via a unique-constraint violation on the relink UPDATE and handled by deleting the
    /// loser's row in that table instead (its full content is still captured in the undo log).
    /// </summary>
    public async Task<EntityMergeResult> MergeAsync(Guid winnerId, Guid loserId, CancellationToken ct = default)
    {
        if (winnerId == loserId) throw new ArgumentException("Cannot merge an entity into itself.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Entities.AsNoTracking().AnyAsync(e => e.Id == loserId, ct))
            throw new InvalidOperationException($"Loser entity {loserId} not found.");
        if (!await db.Entities.AsNoTracking().AnyAsync(e => e.Id == winnerId, ct))
            throw new InvalidOperationException($"Winner entity {winnerId} not found.");

        var fkColumns = await EntityForeignKeyCatalog.DiscoverAsync(db, ct);
        var undoLog = new List<RowMutationUndo>();
        int relinked = 0, deletedForCollision = 0;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Defer FK enforcement for every constraint this merge might touch, for the whole
        // transaction. Found live 2026-08-18: a subtype table's own PK-as-FK row (e.g.
        // Characters.Id -> Entities.Id) and its detail tables (CharacterAffiliations.CharacterId
        // -> Characters.Id) can form a real chicken-and-egg case — relinking a detail table to the
        // winner requires the winner's subtype-table row to already exist, but updating the
        // subtype table's own Id to the winner's value fails immediately if any detail row still
        // references the old (loser's) value. Neither ordering of the two UPDATEs alone succeeds
        // (SQL Server checks each FK at the statement that changes it, not at commit). NOCHECK lets
        // every relink in this transaction run in any order; WITH CHECK CHECK CONSTRAINT right
        // before commit fully revalidates every one of them and throws (rolling back the whole
        // merge) if anything was left inconsistent, so this is not weaker safety — just deferred to
        // the end of the transaction instead of per-statement.
        var constraintsToToggle = fkColumns
            .Select(fk => (fk.Table, fk.ConstraintName))
            .Distinct()
            .ToList();
        foreach (var (table, constraintName) in constraintsToToggle)
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[{table}] NOCHECK CONSTRAINT [{constraintName}]", ct);

        foreach (var fk in fkColumns)
        {
            var (table, column, pkColumn) = (fk.Table, fk.Column, fk.PkColumn);

            // Composite-PK tables (BeatEntityMentions, EntityTags, EntityTaxonomies — all CASCADE
            // to Entities) can't safely go through the update/relink path: an "update" undo entry
            // needs PkColumn to uniquely identify the row for later point-in-time reversal, and no
            // single column of a composite key still does once other rows share the winner's id
            // post-relink. Route straight to capture-and-delete instead — found live 2026-08-17
            // when the FIRST version of this discovery silently excluded these tables entirely,
            // letting SQL Server's own CASCADE untrackably delete the loser's rows the moment its
            // Entities row was removed below (5 real BeatEntityMentions rows lost that way on the
            // first real M-101 merge). This does not relink the loser's mentions/tags/taxonomies to
            // the winner — it deletes them, captured and reversible — which is the maximum safety
            // this composite-key shape allows without risking an ambiguous future reversal.
            if (fk.IsCompositeKey)
            {
                var deletedComposite = await DeleteAndCaptureAsync(db, table, column, pkColumn, loserId, ct);
                undoLog.AddRange(deletedComposite);
                deletedForCollision += deletedComposite.Count;
                continue;
            }

            List<string> touchedPks;
            try
            {
                touchedPks = await RelinkAndCaptureAsync(db, table, column, pkColumn, winnerId, loserId, ct);
            }
            catch (SqlException ex) when (IsUniqueConstraintViolation(ex))
            {
                var deleted = await DeleteAndCaptureAsync(db, table, column, pkColumn, loserId, ct);
                undoLog.AddRange(deleted);
                deletedForCollision += deleted.Count;
                continue;
            }

            foreach (var pk in touchedPks)
                undoLog.Add(new RowMutationUndo("update", table, pkColumn, pk,
                    new Dictionary<string, string?> { [column] = loserId.ToString() }));
            relinked += touchedPks.Count;
        }

        // Post-relink self-alias cleanup (found live 2026-08-22, first real merges after this
        // method shipped): relinking a *Aliases row's {Type}Id to the winner has no idea whether
        // the loser's alias Value happens to equal the WINNER's own canonical Name — e.g. merging
        // "Chen" (loser) into "Mrs. Chen" (winner) relinked a "Mrs. Chen" alias row (originally on
        // the loser) onto the winner, producing a self-referential alias the generic FK-relink
        // loop above has no way to know is now redundant/wrong. Confirmed live: exactly this
        // happened for two of the first three real BCODA merges (Femi Kasparov→Femi, Chen→Mrs.
        // Chen), caught by WorldValidationTests.NoSelfAliases. Any table whose name ends in
        // "Aliases" is assumed to follow this codebase's uniform alias-bridge shape (Id,
        // {Type}Id, Position, Value — confirmed for Character/Place/Faction/Weapon); the cleanup
        // is skipped, not thrown, for any table that doesn't actually have a Value column.
        var winnerName = await db.Entities.AsNoTracking()
            .Where(e => e.Id == winnerId).Select(e => e.Name).FirstOrDefaultAsync(ct) ?? "";
        if (winnerName.Length > 0)
        {
            foreach (var fk in fkColumns.Where(fk => fk.Table.EndsWith("Aliases", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var selfAliasUndo = await DeleteSelfAliasesAndCaptureAsync(
                        db, fk.Table, fk.Column, fk.PkColumn, winnerId, winnerName, ct);
                    undoLog.AddRange(selfAliasUndo);
                }
                catch (SqlException) { /* table doesn't have a Value column — not an alias-shaped table after all, skip */ }
            }
        }

        // Entity tags live INSIDE prose and outlines as <entity … guid="…"> attributes — text, not
        // FK columns — so the sys.foreign_keys catalog above cannot see them. Without this pass every
        // beat that named the loser keeps pointing at a row that no longer exists (found live
        // 2026-09-05: merging the duplicate "Walt" into Breckenridge left BCODA #3277's
        // <entity>Walt</entity> tag on the deleted guid, invisible to every reader). Rewrite the
        // attribute in place through EF so ProseDbContext.StampBeatTextHash re-stamps TextHash and
        // hash-gated consumers see the change honestly. Beats and Nodes are system-versioned —
        // *_History is the undo path for this, same as the loser's own row.
        var loserTag  = $"guid=\"{loserId}\"";
        var winnerTag = $"guid=\"{winnerId}\"";
        var taggedBeats = await db.Beats.Where(b => b.Text != null && b.Text.Contains(loserTag)).ToListAsync(ct);
        foreach (var b in taggedBeats) b.Text = b.Text!.Replace(loserTag, winnerTag, StringComparison.Ordinal);
        var taggedNodes = await db.Nodes.IgnoreQueryFilters()
            .Where(n => n.NodeOutline != null && n.NodeOutline.Contains(loserTag)).ToListAsync(ct);
        foreach (var n in taggedNodes) n.NodeOutline = n.NodeOutline!.Replace(loserTag, winnerTag, StringComparison.Ordinal);
        if (taggedBeats.Count > 0 || taggedNodes.Count > 0) await db.SaveChangesAsync(ct);
        var beatsRetagged = taggedBeats.Count;

        // Delete the loser's own Entities row last — every real FK pointing at it was relinked
        // above, so this is now safe. Reuses DeleteAndCaptureAsync exactly as-is (same
        // capture-as-JSON-then-delete shape already used for the 1:1-collision case) rather than
        // a bespoke soft-disable path — one mechanism, one undo shape, for both cases.
        var loserDeleteUndo = await DeleteAndCaptureAsync(db, "Entities", "Id", "Id", loserId, ct);
        undoLog.AddRange(loserDeleteUndo);

        foreach (var (table, constraintName) in constraintsToToggle)
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE [dbo].[{table}] WITH CHECK CHECK CONSTRAINT [{constraintName}]", ct);

        await tx.CommitAsync(ct);
        return new EntityMergeResult(winnerId, loserId, relinked, deletedForCollision, undoLog, beatsRetagged, taggedNodes.Count);
    }

    private static bool IsUniqueConstraintViolation(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627);

    private static async Task<List<string>> RelinkAndCaptureAsync(
        ProseDbContext db, string table, string column, string pkColumn, Guid winnerId, Guid loserId, CancellationToken ct)
    {
        var sql = $"""
            UPDATE [dbo].[{table}]
            SET [{column}] = @winner
            OUTPUT CONVERT(nvarchar(64), inserted.[{pkColumn}])
            WHERE [{column}] = @loser
            """;
        var pars = new object[] { new SqlParameter("@winner", winnerId), new SqlParameter("@loser", loserId) };
        return await db.Database.SqlQueryRaw<string>(sql, pars).ToListAsync(ct);
    }

    /// <summary>Captures every column of each matching row as JSON (for undo re-insert), then
    /// deletes them. Originally used only for the rare 1:1-collision case (see MergeAsync doc
    /// comment) against non-temporal tables; now also used to delete the loser's own Entities
    /// row, which IS system-versioned — <c>SysStart</c>/<c>SysEnd</c> are excluded from the
    /// capture because they're <c>GENERATED ALWAYS</c> columns SQL Server refuses to accept an
    /// explicit value for, which would otherwise silently break re-insert-based undo the moment
    /// this runs against any temporal table (found live 2026-08-17: the "delete" op's undo
    /// completed the SELECT/DELETE fine but the re-INSERT it enables was never actually
    /// exercised until this session, since no prior caller pointed this at a temporal table).
    ///
    /// Captures the WHOLE matching set as one JSON array (<c>FOR JSON AUTO</c>, with the array
    /// wrapper), not a per-row correlated subquery keyed on <paramref name="pkColumn"/> — a second
    /// real bug found live 2026-08-17, immediately after the first: the per-row correlated-subquery
    /// form (<c>r2.[pkColumn] = r1.[pkColumn]</c>) silently assumed <paramref name="pkColumn"/>
    /// uniquely identifies exactly one row, which was always true before composite-key tables
    /// (<see cref="EntityForeignKeyCatalog.EntityFk.IsCompositeKey"/>) started reaching this method —
    /// for those, <paramref name="pkColumn"/> is only a REPRESENTATIVE single column, and whenever
    /// the loser has more than one row sharing that column's value (e.g. two EntityTags rows, both
    /// naturally sharing the same EntityId), the correlated subquery matched every sibling row per
    /// iteration, producing multiple JSON objects concatenated with no array wrapper — invalid JSON
    /// that threw on deserialize. A single array-returning query has no such assumption; it's
    /// correct regardless of how many rows match or what shape the table's real primary key is.</summary>
    private static readonly string[] TemporalPeriodColumns = ["SysStart", "SysEnd"];

    private static async Task<List<RowMutationUndo>> DeleteAndCaptureAsync(
        ProseDbContext db, string table, string column, string pkColumn, Guid loserId, CancellationToken ct)
    {
        var jsonChunks = await db.Database.SqlQueryRaw<string>($"""
            SELECT * FROM [dbo].[{table}] WHERE [{column}] = @loser FOR JSON AUTO
            """, [new SqlParameter("@loser", loserId)]).ToListAsync(ct);

        // SQL Server splits a long FOR JSON result across MULTIPLE rows (~2033 chars each) —
        // the caller is expected to concatenate all of them to get the complete JSON text. Taking
        // only the first chunk (a bug introduced, then caught, live in this same session) silently
        // truncated any row wide enough to need more than one chunk (Characters, with dozens of
        // columns, hit this immediately). A query matching zero rows returns zero result rows at
        // all (not one row with an empty/null string) — string.Concat of an empty list is "".
        var json = string.Concat(jsonChunks);

        var result = new List<RowMutationUndo>();
        if (!string.IsNullOrEmpty(json))
        {
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json)
                ?? throw new InvalidOperationException($"Could not parse captured row-set JSON for {table}.");
            foreach (var dict in rows)
            {
                var pkValue = dict.TryGetValue(pkColumn, out var pkv) ? pkv?.ToString() ?? "" : "";
                var columns = dict
                    .Where(kv => !TemporalPeriodColumns.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
                result.Add(new RowMutationUndo("delete", table, pkColumn, pkValue, columns));
            }
        }

        await db.Database.ExecuteSqlRawAsync(
            $"DELETE FROM [dbo].[{table}] WHERE [{column}] = @loser", [new SqlParameter("@loser", loserId)], ct);

        return result;
    }

    /// <summary>Same capture-then-delete shape as <see cref="DeleteAndCaptureAsync"/>, but scoped
    /// to post-merge self-alias cleanup: deletes rows on <paramref name="table"/> where the FK
    /// column already equals <paramref name="winnerId"/> (i.e. relinked there by this same merge)
    /// AND the row's own <c>Value</c> matches <paramref name="winnerName"/> case-insensitively —
    /// a genuine self-alias, redundant now that the row points at the entity whose own name it
    /// duplicates. Throws <see cref="SqlException"/> (caught by the caller) if the table has no
    /// <c>Value</c> column, meaning it isn't actually alias-shaped despite the name match.</summary>
    private static async Task<List<RowMutationUndo>> DeleteSelfAliasesAndCaptureAsync(
        ProseDbContext db, string table, string column, string pkColumn, Guid winnerId, string winnerName, CancellationToken ct)
    {
        var pars = new object[] { new SqlParameter("@winner", winnerId), new SqlParameter("@name", winnerName) };

        var jsonChunks = await db.Database.SqlQueryRaw<string>($"""
            SELECT * FROM [dbo].[{table}] WHERE [{column}] = @winner AND LOWER([Value]) = LOWER(@name) FOR JSON AUTO
            """, pars).ToListAsync(ct);
        var json = string.Concat(jsonChunks);

        var result = new List<RowMutationUndo>();
        if (!string.IsNullOrEmpty(json))
        {
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json)
                ?? throw new InvalidOperationException($"Could not parse captured row-set JSON for {table}.");
            foreach (var dict in rows)
            {
                var pkValue = dict.TryGetValue(pkColumn, out var pkv) ? pkv?.ToString() ?? "" : "";
                var columns = dict
                    .Where(kv => !TemporalPeriodColumns.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
                result.Add(new RowMutationUndo("delete", table, pkColumn, pkValue, columns));
            }

            await db.Database.ExecuteSqlRawAsync(
                $"DELETE FROM [dbo].[{table}] WHERE [{column}] = @winner AND LOWER([Value]) = LOWER(@name)", pars, ct);
        }

        return result;
    }

    private static string Snippet(string description) =>
        description.Length <= 120 ? description : description[..120].TrimEnd() + "…";

    /// <summary>Standard iterative Levenshtein edit distance (insert/delete/substitute), O(len1*len2).</summary>
    internal static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}
