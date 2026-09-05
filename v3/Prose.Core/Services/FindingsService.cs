using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Prose.Core.Data;
using Prose.Core.Data.Entities;
using Prose.Core.Interfaces;

namespace Prose.Core.Services;

// SemanticDrift/ReaderKnows/StructuralFailure/Liberty/Causality/Interpersonal/AffectBehavior/Xray/
// BookAudit/StoryScope added 2026-08-08: these subsystems previously all hardcoded
// FindingCategory.Other because no dedicated value existed, which flattened ~5,200 real findings
// into one undifferentiated bucket the /findings category-filter chips couldn't distinguish —
// the main reason the findings backlog had ~0 triage throughput despite the UI supporting it.
// CraftAudit (also added 2026-08-08) was retired the same day: CraftRuleAuditService, its sole
// writer, was merged into BeatChecklistGateService (FindingCategory.CraftChecklist) — the two
// services independently parsed the same CRAFT.md §8 rules, and the checklist's per-beat,
// hash-cached implementation is the more complete one. No historical backlog existed under
// CraftAudit at the time of the merge, so the value was removed rather than kept for history.
public enum FindingCategory { Contradiction, Cliche, Anachronism, Voice, OutlineDrift, GearContradiction, BehaviorContradiction, ProseHealth, NearDuplicate, ComprehensionDefect, CraftChecklist, ReaderGripe, SemanticDrift, StructuralFailure, Liberty, Causality, Interpersonal, AffectBehavior, Xray, BookAudit, StoryScope, Craft, EntityDrift, Other }
public enum FindingSeverity { Low, Medium, High }
public enum FindingStatus   { New, Triaged, Applied, Dismissed }

public record Finding(
    long Id,
    DateTime DetectedAt,
    string FilePath,
    string? ChapterId,
    FindingCategory Category,
    FindingSeverity Severity,
    string Summary,
    string? Snippet,
    string? SuggestedFix,
    FindingStatus Status,
    DateTime? ResolvedAt);

/// <summary>
/// SQL Server-backed inbox of findings detected by ContinuousQualityService and
/// any future analyzer. Migrated from SQLite (<c>findings.db</c>) to the unified
/// Prose database 2026-05-09 — one source of truth across the app.
///
/// Schema bootstrap (idempotent) runs in the constructor so existing dev DBs
/// auto-upgrade without a separate migration step. If a legacy
/// <c>findings.db</c> file exists at the data root, its rows are copied into
/// the new <c>Findings</c> table on first construction (only when the SQL
/// Server table is empty), then the SQLite file is renamed to
/// <c>findings.db.imported</c> so the import is one-shot.
/// </summary>
public class FindingsService
{
    private readonly IDbContextFactory<ProseDbContext> dbFactory;
    private readonly IPathProvider paths;

    public FindingsService(
        IDbContextFactory<ProseDbContext> dbFactory,
        IPathProvider paths)
    {
        this.dbFactory = dbFactory;
        this.paths     = paths;
        EnsureSchema();
        TryImportLegacySqlite();
    }

    private void EnsureSchema()
    {
        // EF model already declares the table — but EnsureCreated only creates
        // missing tables on a brand-new DB. Idempotent CREATE TABLE here so
        // existing databases pick up the new table without a full migration.
        using var db = dbFactory.CreateDbContext();
        if (!db.Database.IsSqlServer()) return;
        db.Database.ExecuteSqlRaw("""
            IF OBJECT_ID(N'[dbo].[Findings]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[Findings] (
                    [Id]            BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [DetectedAt]    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
                    [FilePath]      NVARCHAR(900) NOT NULL,
                    [ChapterId]     NVARCHAR(80)  NULL,
                    [Category]      NVARCHAR(40)  NOT NULL,
                    [Severity]      NVARCHAR(20)  NOT NULL,
                    [Summary]       NVARCHAR(MAX) NOT NULL,
                    [Snippet]       NVARCHAR(MAX) NULL,
                    [SuggestedFix]  NVARCHAR(MAX) NULL,
                    [Status]        NVARCHAR(20)  NOT NULL,
                    [ResolvedAt]    DATETIME2     NULL,
                    [DedupKey]      NVARCHAR(450) NOT NULL
                );
                CREATE UNIQUE INDEX [UQ_Findings_DedupKey] ON [dbo].[Findings]([DedupKey]);
                CREATE INDEX [IX_Findings_Status]   ON [dbo].[Findings]([Status]);
                CREATE INDEX [IX_Findings_FilePath] ON [dbo].[Findings]([FilePath]);
                CREATE INDEX [IX_Findings_ChapterId] ON [dbo].[Findings]([ChapterId]);
            END;
            """);

        // RFC 0011 Brick 2 — idempotent in-place add for existing DBs, same self-upgrading
        // pattern as the table bootstrap above (this service predates EF migrations being the
        // norm for this project and still auto-upgrades existing dev DBs without one).
        db.Database.ExecuteSqlRaw("""
            IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Findings]') AND name = 'SourceRuleVersion')
            BEGIN
                ALTER TABLE [dbo].[Findings] ADD [SourceRuleVersion] NVARCHAR(40) NULL;
                CREATE INDEX [IX_Findings_Category_SourceRuleVersion] ON [dbo].[Findings]([Category], [SourceRuleVersion]);
            END;
            """);
    }

    /// <summary>
    /// One-shot copy of any legacy <c>findings.db</c> rows into SQL Server.
    /// Skipped when the SQL table already has rows (so re-runs after partial
    /// import don't duplicate). The legacy file is renamed to
    /// <c>findings.db.imported</c> on success — easy to spot in the data
    /// folder, never imported twice.
    /// </summary>
    private void TryImportLegacySqlite()
    {
        var legacy = Path.Combine(paths.MutableDataDir, "findings.db");
        if (!File.Exists(legacy)) return;

        using var db = dbFactory.CreateDbContext();
        if (db.Findings.Any()) return; // already populated; don't risk duplicates

        try
        {
            using var conn = new SqliteConnection($"Data Source={legacy};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT detected_at, file_path, chapter_id, category, severity, summary, snippet, suggested_fix, status, resolved_at, dedup_key FROM findings;";
            using var rdr = cmd.ExecuteReader();
            var batch = new List<FindingRow>();
            while (rdr.Read())
            {
                batch.Add(new FindingRow
                {
                    DetectedAt    = ParseDate(rdr.GetString(0)) ?? DateTime.UtcNow,
                    FilePath      = rdr.GetString(1),
                    ChapterId     = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Category      = rdr.GetString(3),
                    Severity      = rdr.GetString(4),
                    Summary       = rdr.GetString(5),
                    Snippet       = rdr.IsDBNull(6) ? null : rdr.GetString(6),
                    SuggestedFix  = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                    Status        = rdr.GetString(8),
                    ResolvedAt    = rdr.IsDBNull(9) ? null : ParseDate(rdr.GetString(9)),
                    DedupKey      = rdr.GetString(10),
                });
            }
            if (batch.Count > 0)
            {
                db.Findings.AddRange(batch);
                db.SaveChanges();
            }
            // Rename the legacy file so the import is single-shot. We don't
            // delete — keep it on disk as a rollback / audit artefact.
            File.Move(legacy, legacy + ".imported", overwrite: true);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "FindingsService: legacy findings.db import failed; SQL table is empty and legacy file remains in place");
        }
    }

    private static DateTime? ParseDate(string s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    /// <summary>Prefix every beat-anchored finding's <c>FilePath</c> carries.</summary>
    internal const string BeatFilePathPrefix = "beat:";

    /// <summary>
    /// Dismiss open findings anchored to a beat that no longer exists in any node.
    ///
    /// <b>The bug this closes.</b> Beat-scoped findings (ENTITY-CONFLICT from
    /// <see cref="EntityContextService"/>, and anything else writing <c>beat:&lt;guid&gt;</c>) are
    /// written while a beat is live. When that beat is later removed — a replot, a rename, a
    /// superseded draft — its BeatNode row is gone, but the finding stays New forever, still
    /// quoting prose that is no longer in the book. On TRNY (2026-08-02) that produced 19 open
    /// Medium findings, 10 of them quoting a character name that had been renamed out of the
    /// manuscript entirely; every one was a false positive and each had to be re-verified by hand
    /// against the live prose before it could be dismissed.
    ///
    /// A finding is only reaped when the beat has NO remaining BeatNode row anywhere — a beat
    /// shared across nodes stays live as long as one membership exists — and only if still open,
    /// so a human's Applied/Dismissed decision is never overwritten.
    /// </summary>
    public async Task<int> DismissStaleBeatFindingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var open = await db.Findings
            .Where(f => (f.Status == "New" || f.Status == "Triaged") && f.FilePath.StartsWith(BeatFilePathPrefix))
            .ToListAsync(ct);
        if (open.Count == 0) return 0;

        var liveKeys = (await db.BeatNodes.AsNoTracking()
                .Select(bn => bn.BeatId)
                .Distinct()
                .ToListAsync(ct))
            .Select(id => id.ToString("N"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = open
            .Where(f => !liveKeys.Contains(f.FilePath[BeatFilePathPrefix.Length..].Trim()))
            .ToList();

        foreach (var f in stale)
        {
            f.Status     = "Dismissed";
            f.ResolvedAt = DateTime.UtcNow;
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public long Upsert(
        string filePath,
        string? chapterId,
        FindingCategory category,
        FindingSeverity severity,
        string summary,
        string? snippet,
        string? suggestedFix,
        string? sourceRuleVersion = null)
    {
        var dedup = $"{filePath}|{category}|{summary}".ToLowerInvariant();
        // 450 NVARCHAR cap on the column — truncate quietly on the rare
        // pathological summary so the unique index never rejects.
        if (dedup.Length > 450) dedup = dedup[..450];

        using var db = dbFactory.CreateDbContext();
        var existing = db.Findings.FirstOrDefault(f => f.DedupKey == dedup);
        if (existing != null)
        {
            // Conflict update — same shape as the prior SQLite UPSERT.
            existing.Severity          = severity.ToString();
            existing.Snippet           = snippet;
            existing.SuggestedFix      = suggestedFix;
            existing.DetectedAt        = DateTime.UtcNow;
            existing.SourceRuleVersion = sourceRuleVersion;
            db.SaveChanges();
            return existing.Id;
        }

        var row = new FindingRow
        {
            DetectedAt         = DateTime.UtcNow,
            FilePath           = filePath,
            ChapterId          = chapterId,
            Category           = category.ToString(),
            Severity           = severity.ToString(),
            Summary            = summary,
            Snippet            = snippet,
            SuggestedFix       = suggestedFix,
            Status             = nameof(FindingStatus.New),
            DedupKey           = dedup,
            SourceRuleVersion  = sourceRuleVersion,
        };
        db.Findings.Add(row);
        try
        {
            db.SaveChanges();
            return row.Id;
        }
        catch (DbUpdateException)
        {
            // Concurrent upsert: another caller inserted the same dedup key between our
            // FirstOrDefault check and our SaveChanges. Re-read the winner's row.
            db.ChangeTracker.Clear();
            var winner = db.Findings.FirstOrDefault(f => f.DedupKey == dedup);
            if (winner != null) return winner.Id;
            throw;
        }
    }

    public sealed record StaleFindingGroup(string Category, string FilePath, int StaleCount, int TotalCount);

    /// <summary>
    /// RFC 0011 Brick 2 — the generic staleness query every check category shares instead of
    /// hand-rolling its own (the exact gap that let <c>BeatVerification</c>'s equivalent bug
    /// survive two separate manual re-audits in one session before it got a table-scoped fix).
    /// Pass the version each category currently considers "current" (e.g.
    /// <c>{"CraftChecklist": BeatChecklistGateService.PromptVersion}</c>) — this service has no
    /// opinion on what a rule version means, only on comparing what's stored to what's given.
    /// Findings with a null <see cref="FindingRow.SourceRuleVersion"/> (no version ever recorded,
    /// including every finding filed before this column existed) count as stale by definition.
    /// Only open (New/Triaged) findings are considered — a Dismissed/Applied finding's staleness
    /// no longer matters.
    /// </summary>
    public async Task<IReadOnlyList<StaleFindingGroup>> GetStaleCategoriesAsync(
        IReadOnlyDictionary<string, string> currentVersionByCategory, CancellationToken ct = default)
    {
        if (currentVersionByCategory.Count == 0) return Array.Empty<StaleFindingGroup>();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var categories = currentVersionByCategory.Keys.ToList();
        var rows = await db.Findings.AsNoTracking()
            .Where(f => (f.Status == "New" || f.Status == "Triaged") && categories.Contains(f.Category))
            .Select(f => new { f.Category, f.FilePath, f.SourceRuleVersion })
            .ToListAsync(ct);

        return rows
            .GroupBy(f => (f.Category, Book: BookPrefix(f.FilePath)))
            .Select(g => new StaleFindingGroup(
                g.Key.Category, g.Key.Book,
                StaleCount: g.Count(f => f.SourceRuleVersion != currentVersionByCategory[g.Key.Category]),
                TotalCount: g.Count()))
            .Where(g => g.StaleCount > 0)
            .OrderByDescending(g => g.StaleCount)
            .ToList();
    }

    /// <summary>"node:slug" or "node:slug/beat:guid" -> "node:slug" — groups per-beat findings
    /// under their owning book for the staleness report, same convention every category's
    /// FilePath already follows (see DeleteBySummaryPrefix).</summary>
    private static string BookPrefix(string filePath)
    {
        var i = filePath.IndexOf("/beat:", StringComparison.Ordinal);
        return i < 0 ? filePath : filePath[..i];
    }

    public IReadOnlyList<Finding> List(FindingStatus? status = null, int limit = 200, string? filePathPrefix = null)
    {
        // Severity ordering: High first, then Medium, then Low. Newest within
        // each bucket. Keeps damning findings at the top of the inbox without
        // burying recent low-severity noise.
        //
        // filePathPrefix (2026-09-01): without it, a book's own findings — especially
        // Medium-severity ones like FACT-LEDGER — are invisible in practice once the
        // corpus-wide High-severity backlog exceeds `limit`. PublishReadinessAsync already
        // scopes this way (BookHealthService.cs); this brings the same scoping to the
        // general-purpose read surfaces (CLI/MCP) that triage actually happens through.
        using var db = dbFactory.CreateDbContext();
        var q = db.Findings.AsNoTracking().AsQueryable();
        if (status is FindingStatus s)
        {
            var key = s.ToString();
            q = q.Where(f => f.Status == key);
        }
        if (!string.IsNullOrWhiteSpace(filePathPrefix))
            q = q.Where(f => f.FilePath.StartsWith(filePathPrefix));
        var rows = q
            .OrderBy(f => f.Severity == "High" ? 0 : f.Severity == "Medium" ? 1 : f.Severity == "Low" ? 2 : 3)
            .ThenByDescending(f => f.DetectedAt)
            .Take(limit)
            .ToList();
        return rows.Select(ToFinding).ToList();
    }

    public Finding? Get(long id)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Findings.AsNoTracking().FirstOrDefault(f => f.Id == id);
        return row == null ? null : ToFinding(row);
    }

    public int CountByStatus(FindingStatus status)
    {
        using var db = dbFactory.CreateDbContext();
        var key = status.ToString();
        return db.Findings.Count(f => f.Status == key);
    }

    /// <summary>
    /// Findings attached to a specific chapter, severity-sorted then most-recent.
    /// Driven by the editor sidebar on Write.razor — that view wants the
    /// in-progress findings for *this* chapter, not the whole project inbox.
    /// </summary>
    public IReadOnlyList<Finding> ListByChapter(string chapterId, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(chapterId)) return Array.Empty<Finding>();
        using var db = dbFactory.CreateDbContext();
        var rows = db.Findings.AsNoTracking()
            .Where(f => f.ChapterId == chapterId)
            .OrderBy(f => f.Severity == "High" ? 0 : f.Severity == "Medium" ? 1 : f.Severity == "Low" ? 2 : 3)
            .ThenByDescending(f => f.DetectedAt)
            .Take(limit)
            .ToList();
        return rows.Select(ToFinding).ToList();
    }

    public void SetStatus(long id, FindingStatus status)
    {
        using var db = dbFactory.CreateDbContext();
        var row = db.Findings.FirstOrDefault(f => f.Id == id);
        if (row == null) return;
        row.Status = status.ToString();
        row.ResolvedAt = (status == FindingStatus.Applied || status == FindingStatus.Dismissed)
            ? DateTime.UtcNow : null;
        db.SaveChanges();
    }

    /// <summary>
    /// Set status on every open (New/Triaged) finding matching an optional category and/or
    /// summary-prefix filter — at least one filter is required so this can't accidentally sweep
    /// the entire inbox. Only ever narrows to New/Triaged rows: a finding already Applied or
    /// Dismissed reflects a human decision that a bulk sweep must never overwrite.
    ///
    /// Exists because the one-at-a-time <c>SetStatus</c> above is the only mutation path the
    /// Findings inbox had (2026-08-13 audit: 19,239 New vs 103 ever Applied — the volume, not a
    /// missing capability, was the actual bottleneck). Lets an operator clear a whole
    /// category/prefix's backlog — e.g. every pre-fix per-beat SWAIN row — in one call instead of
    /// thousands of individual triage decisions nobody was ever going to make.
    /// </summary>
    public async Task<int> BulkSetStatusAsync(
        FindingStatus status,
        FindingCategory? category = null,
        string? summaryPrefix = null,
        string? filePathPrefix = null,
        CancellationToken ct = default)
    {
        if (category is null && string.IsNullOrWhiteSpace(summaryPrefix) && string.IsNullOrWhiteSpace(filePathPrefix))
            throw new ArgumentException("BulkSetStatusAsync requires at least one of category/summaryPrefix/filePathPrefix — refusing to sweep the entire inbox unfiltered.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var q = db.Findings.Where(f => f.Status == "New" || f.Status == "Triaged");
        if (category is FindingCategory c) { var key = c.ToString(); q = q.Where(f => f.Category == key); }
        if (!string.IsNullOrWhiteSpace(summaryPrefix)) q = q.Where(f => f.Summary.StartsWith(summaryPrefix));
        if (!string.IsNullOrWhiteSpace(filePathPrefix)) q = q.Where(f => f.FilePath.StartsWith(filePathPrefix));

        var rows = await q.ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = status.ToString();
            row.ResolvedAt = (status == FindingStatus.Applied || status == FindingStatus.Dismissed)
                ? DateTime.UtcNow : null;
        }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// All findings whose FilePath starts with a given prefix (e.g. <c>"beat:{guid}"</c>).
    /// Used by SceneContextAssembler to read persisted narrative-science results for a beat.
    /// </summary>
    public IReadOnlyList<Finding> ListByFilePathPrefix(string prefix, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return Array.Empty<Finding>();
        using var db = dbFactory.CreateDbContext();
        var rows = db.Findings.AsNoTracking()
            .Where(f => f.FilePath.StartsWith(prefix))
            .OrderBy(f => f.Severity == "High" ? 0 : f.Severity == "Medium" ? 1 : f.Severity == "Low" ? 2 : 3)
            .ThenByDescending(f => f.DetectedAt)
            .Take(limit)
            .ToList();
        return rows.Select(ToFinding).ToList();
    }

    /// <summary>
    /// Delete the OPEN (New/Triaged) findings for a given file-path prefix whose Summary starts
    /// with a given text prefix (e.g. <c>"NARRATIVE-SCIENCE [dramatic-question]:"</c>). Used to
    /// supersede stale results before an instrument writes fresh ones.
    ///
    /// <b>Applied/Dismissed rows are kept on purpose (2026-09-05).</b> Every instrument that
    /// calls this then re-files through <see cref="Upsert"/>, which dedups on
    /// <c>filePath|category|summary</c> and never touches <c>Status</c>. Keeping the resolved row
    /// means an unchanged finding a human already ruled on lands back on its own row and stays
    /// ruled on. The prior behaviour deleted the ruled rows too, so every re-run re-filed the
    /// same finding as New — on BCODA, twelve <c>LINT ALT-SCENE</c> pairs that had each been
    /// read in full and dismissed came back as twelve open High findings on the very next
    /// <c>--lint-prose</c>, re-blocking the publish-readiness gate they had just cleared. If the
    /// underlying text changes, the summary (beat numbers, counts, evidence) changes with it, so
    /// a genuinely new finding still files as New.
    /// </summary>
    public int DeleteBySummaryPrefix(string filePathPrefix, string summaryPrefix)
    {
        using var db = dbFactory.CreateDbContext();
        var rows = db.Findings
            .Where(f => f.FilePath.StartsWith(filePathPrefix)
                     && f.Summary.StartsWith(summaryPrefix)
                     && (f.Status == "New" || f.Status == "Triaged"))
            .ToList();
        if (rows.Count == 0) return 0;
        db.Findings.RemoveRange(rows);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another caller (the on-demand --audit-book/book_health path racing the
            // periodic SanityScanBackgroundService sweep, both clearing the same
            // "node:{slug}"+"SANITY " prefix — see SanityScanService.FileFindings) already
            // deleted these same rows between our SELECT and this SaveChanges. The desired
            // end state — these stale rows gone — is already true, so this is success, not
            // failure; a plain retry would just throw again against the same phantom rows.
        }
        return rows.Count;
    }

    private static Finding ToFinding(FindingRow r) => new(
        Id:           r.Id,
        DetectedAt:   r.DetectedAt,
        FilePath:     r.FilePath,
        ChapterId:    r.ChapterId,
        Category:     Enum.TryParse<FindingCategory>(r.Category, out var c) ? c : FindingCategory.Other,
        Severity:     Enum.TryParse<FindingSeverity>(r.Severity, out var s) ? s : FindingSeverity.Low,
        Summary:      r.Summary,
        Snippet:      r.Snippet,
        SuggestedFix: r.SuggestedFix,
        Status:       Enum.TryParse<FindingStatus>(r.Status, out var st) ? st : FindingStatus.New,
        ResolvedAt:   r.ResolvedAt);
}
