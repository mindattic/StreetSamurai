using Microsoft.EntityFrameworkCore;
using Prose.Core.Data;
using Prose.Core.Data.Entities;
using Prose.Core.Models.Canon;
using Prose.Core.Services;

namespace Prose.UnitTests;

/// <summary>
/// Materialized read-model (CQRS-lite) round-trip contract. The relational
/// Character row + bridges stay the source of truth; CharacterReadModels caches
/// the expensive projection. These guard the two properties that matter:
/// (1) reads served off the projection equal the relational truth, and
/// (2) the projection is refreshed on every write so it never goes stale —
/// the drift failure mode the user explicitly fears.
/// </summary>
[TestFixture]
public class CharacterReadModelTests
{
    private string tempDir = "";
    private TestPathProviderWithRoot paths = null!;
    private CharacterRepository repo = null!;

    [SetUp]
    public void SetUp()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"ss_rm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        paths = new TestPathProviderWithRoot(tempDir);
        TestDbFactory.Reset(paths);   // clean in-memory DB per fixture
        repo = new CharacterRepository(paths);
    }

    [TearDown]
    public void TearDown()
    {
        TestDbFactory.Reset(paths);
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }

    private static CharacterData MakeChar(string name, string eyeColor, string description, params string[] tags)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = "character",
            Name = name,
            Description = description,
            PhysicalDescription = new PhysicalDescription { EyeColor = eyeColor },
            Tags = tags.ToList(),
        };

    [Test]
    public void Save_Then_GetById_ServesFromReadModel_WithAllFields()
    {
        var c = MakeChar("Test Runner", "amber", "A wary courier.", "freelancer", "courier");
        repo.Save(c);

        var got = repo.GetById(c.Id);

        Assert.That(got, Is.Not.Null);
        Assert.That(got!.Name, Is.EqualTo("Test Runner"));
        Assert.That(got.Description, Is.EqualTo("A wary courier."));
        Assert.That(got.PhysicalDescription?.EyeColor, Is.EqualTo("amber"), "deep bridge field must survive the blob round-trip");
        Assert.That(got.Tags, Does.Contain("freelancer").And.Contain("courier"), "tags are overlaid live, not stored in the blob");
    }

    [Test]
    public void ReadModelRow_IsWritten_AtCurrentVersion()
    {
        var c = MakeChar("Versioned", "grey", "x");
        repo.Save(c);

        var id = Guid.ParseExact(c.Id, "N");
        using var db = TestDbFactory.For(paths, "character").CreateDbContext();
        var row = db.CharacterReadModels.AsNoTracking().FirstOrDefault(r => r.CharacterId == id);

        Assert.That(row, Is.Not.Null, "Save must materialize a read-model row");
        Assert.That(row!.Version, Is.EqualTo(CharacterMapper.ReadModelVersion));
        Assert.That(row.Json, Does.Not.Contain("\"eye_color\":\"\"").Or.Contains("grey").IgnoreCase);
    }

    [Test]
    public void Edit_Then_GetById_ReflectsChange_NoStaleProjection()
    {
        var c = MakeChar("Mutable", "blue", "first");
        repo.Save(c);

        // Re-fetch, edit a deep field, re-save.
        var loaded = repo.GetById(c.Id)!;
        loaded.Description = "second";
        loaded.PhysicalDescription!.EyeColor = "green";
        repo.Save(loaded);

        var got = repo.GetById(c.Id)!;
        Assert.That(got.Description, Is.EqualTo("second"), "refresh-on-write must overwrite the cached projection");
        Assert.That(got.PhysicalDescription?.EyeColor, Is.EqualTo("green"));
    }

    [Test]
    public void GetById_Then_NoOpSave_PreservesAliases_AddedOutOfBandAfterReadModelCache()
    {
        // Reproduces the confirmed bug: aliases inserted directly into the
        // CharacterAliases bridge table (a manual repair, bypassing
        // CharacterRepository.Save) never touch Entities.ModifiedAt, so
        // CharacterMapper.LoadOneFromReadModel's staleness check ("has
        // ModifiedAt advanced past the cached blob's RefreshedAt?") can't see
        // the change and would serve the pre-insert (alias-less) snapshot.
        // GetById feeds directly into Save's wipe-and-reinsert of every bridge
        // table (CharacterMapper.PersistAsync), so a stale Aliases list there
        // means a subsequent no-op update (exactly what the create_character
        // MCP tool does: GetById -> tweak scalars -> Save) silently deletes
        // the out-of-band aliases. GetById must always read live relational
        // truth (CharacterMapper.LoadOne), never the cached projection.
        var c = MakeChar("Alias Bug Subject", "hazel", "before the bridge insert");
        repo.Save(c);
        var id = Guid.ParseExact(c.Id, "N");

        // Simulate the out-of-band repair: insert directly into the bridge
        // table without going through CharacterRepository.Save, and without
        // touching Entities.ModifiedAt — exactly what a raw SQL INSERT does.
        using (var db = TestDbFactory.For(paths, "character").CreateDbContext())
        {
            db.CharacterAliases.Add(new Prose.Core.Data.Entities.CharacterAlias
            {
                CharacterId = id,
                Position = 0,
                Value = "Manually Repaired Alias",
            });
            db.SaveChanges();
        }

        // The exact round trip create_character performs on an existing id:
        // fetch the current record, mutate an unrelated field, save it back.
        var loaded = repo.GetById(c.Id)!;
        Assert.That(loaded.Aliases, Does.Contain("Manually Repaired Alias"),
            "GetById must see the out-of-band alias, not a stale cached snapshot");

        loaded.Description = "after the bridge insert (unrelated no-op edit)";
        repo.Save(loaded);

        var after = repo.GetById(c.Id)!;
        Assert.That(after.Aliases, Does.Contain("Manually Repaired Alias"),
            "a no-op Save on an unrelated field must not wipe aliases added out-of-band");
    }

    [Test]
    public async Task Rebuild_Repopulates_FromRelationalTruth()
    {
        repo.Save(MakeChar("Alpha", "amber", "a"));
        repo.Save(MakeChar("Beta", "grey", "b"));

        using var db = TestDbFactory.For(paths, "character").CreateDbContext();
        // Wipe the projection to simulate a post-bulk-import empty state.
        await db.CharacterReadModels.ExecuteDeleteAsync();
        Assert.That(db.CharacterReadModels.Count(), Is.EqualTo(0));

        var result = await CharacterMapper.RebuildAllReadModelsAsync(db);

        Assert.That(result.Written, Is.GreaterThanOrEqualTo(2));
        Assert.That(result.Skipped, Is.EqualTo(0));
        Assert.That(db.CharacterReadModels.Count(), Is.EqualTo(result.Written));
    }

    /// <summary>
    /// The 2026-09-05 incident, reproduced and pinned: a character's relational depth bridges
    /// (PsychologyTraits here) go missing out-of-band — a migration gap, not something this
    /// rebuild caused — while the cached read-model still holds the real content. Rebuilding
    /// from the (now-incomplete) relational truth must SKIP that character rather than silently
    /// overwrite the richer cache with an empty one — this is the guarantee that makes it
    /// impossible for a routine <c>--rebuild-readmodel</c> to destroy hand-authored depth again.
    /// </summary>
    [Test]
    public async Task Rebuild_Never_Overwrites_RicherCache_WithPoorerRelationalRead()
    {
        var rich = MakeChar("Nadia Migizi", "brown", "A forger with a shrine to a data E.L.F.");
        rich.Psychology.CoreFears.Add("Losing his equipment");
        rich.Psychology.CoreDesires.Add("Dismantling checkpoint systems");
        repo.Save(rich);
        var id = Guid.ParseExact(rich.Id, "N");

        using var db = TestDbFactory.For(paths, "character").CreateDbContext();

        // Simulate the pre-existing migration gap: the bridge table loses its rows, but the
        // cache (from the Save above) still has the real psychology content.
        await db.Set<CharacterPsychologyTrait>().Where(x => x.CharacterId == id).ExecuteDeleteAsync();

        var before = db.CharacterReadModels.AsNoTracking().First(r => r.CharacterId == id).Json;
        Assert.That(before, Does.Contain("Losing his equipment"), "sanity check: cache still holds the real content pre-rebuild");

        var result = await CharacterMapper.RebuildAllReadModelsAsync(db);

        Assert.That(result.Skipped, Is.EqualTo(1));
        Assert.That(result.SkippedRows.Select(r => r.Id), Does.Contain(id));

        var after = db.CharacterReadModels.AsNoTracking().First(r => r.CharacterId == id).Json;
        Assert.That(after, Does.Contain("Losing his equipment"),
            "the cache must still carry the real content after rebuild — never silently replaced with an empty relational read");

        // Bypass CharacterRepository's own in-process cache (warmed by Save() above, so it
        // would trivially still look right) — read straight from the DB, same as a fresh
        // process would, to prove what actually gets served post-rebuild.
        var served = CharacterMapper.LoadOneFromReadModel(db, id);
        Assert.That(served!.Psychology.CoreFears, Does.Contain("Losing his equipment"),
            "reads must keep serving the richer cache, not a poorer relational re-materialize");
    }

    [Test]
    public void GetAll_BackfillsMissingReadModels_AndSelfHeals()
    {
        repo.Save(MakeChar("Gamma", "violet", "g"));

        // Drop the projection row to force the missing-path backfill on read.
        using (var db = TestDbFactory.For(paths, "character").CreateDbContext())
            db.CharacterReadModels.ExecuteDelete();

        repo.Reload();                       // clear the in-memory mapped cache
        var all = repo.GetAll();

        Assert.That(all.Any(c => c.Name == "Gamma"), Is.True, "GetAll must backfill a missing read-model rather than drop the character");

        using var db2 = TestDbFactory.For(paths, "character").CreateDbContext();
        Assert.That(db2.CharacterReadModels.Count(), Is.GreaterThanOrEqualTo(1), "the backfill must persist so the next read is fast");
    }

    [Test]
    public void GetAll_Backfill_ReconcilesRow_WhenPhysicalRowHasStaleUniverseId()
    {
        // Reproduces the confirmed cross-universe PK-collision bug (fixed 2026-08-08):
        // CharacterMapper.SaveReadModelSafe's existence check used to be universe-scoped, so a
        // read-model row already physically present under a stale/mismatched UniverseId (e.g.
        // Guid.Empty, from before the column existed) was invisible under the current ambient
        // scope. GetAll's backfill would then try to INSERT, collide on the real PK (CharacterId),
        // and its recovery re-query — also scoped — would find nothing either, silently leaving
        // the character's read-model unrefreshed forever. The fix uses IgnoreQueryFilters() on
        // both lookups and always re-stamps UniverseId from the owning Entity.
        var real = new FakeUniverseContext();
        var previous = UniverseScope.Current;
        UniverseScope.Current = real;
        try
        {
            var scopedUniverse = new Guid("0197e9c9-0ccc-7000-8000-00000000000c");
            real.CurrentId = scopedUniverse;

            repo.Save(MakeChar("Delta", "amber", "d"));
            var id = repo.GetAll().Single(c => c.Name == "Delta").Id;
            var guid = Guid.ParseExact(id, "N");

            // Simulate the stale/mismatched stamp a legacy row could carry.
            using (var db = TestDbFactory.For(paths, "character").CreateDbContext())
            {
                var row = db.CharacterReadModels.IgnoreQueryFilters().Single(r => r.CharacterId == guid);
                row.UniverseId = Guid.Empty;
                db.SaveChanges();
            }

            repo.Reload();
            var all = repo.GetAll();   // still scoped to scopedUniverse — must not silently drop Delta

            Assert.That(all.Any(c => c.Name == "Delta"), Is.True,
                "a read-model row with a stale UniverseId stamp must still be found and reconciled, not silently orphaned");

            using var db2 = TestDbFactory.For(paths, "character").CreateDbContext();
            var fixedRow = db2.CharacterReadModels.IgnoreQueryFilters().Single(r => r.CharacterId == guid);
            Assert.That(fixedRow.UniverseId, Is.EqualTo(scopedUniverse),
                "the backfill must re-stamp UniverseId from the owning Entity, correcting the stale value");
        }
        finally
        {
            UniverseScope.Current = previous;
        }
    }

    // ── A minimal IUniverseContext for this one cross-universe regression test. ──
    private sealed class FakeUniverseContext : IUniverseContext
    {
        public Guid CurrentId { get; set; } = Guid.Empty;
        // A fake that pins CurrentId HAS named its universe — that is exactly what an explicit
        // scope means (Story Ledger Phase 3, UnscopedUniverseWriteCheck). Guid.Empty means no
        // universe is wired at all, where scoping is a no-op and nothing gates on this.
        public bool IsExplicitlyScoped => CurrentId != Guid.Empty;

        public string CurrentSlug => "test";
        public UniverseInfo? CurrentUniverse => new(CurrentId, CurrentSlug, "Test", null, "a test world", true, 100);
        public IReadOnlyList<UniverseInfo> ListUniverses() => new List<UniverseInfo>();
        public bool IsGlmz => CurrentId == Guid.Empty;
        public string UniverseGroundingOr(string glmzFallback) => IsGlmz ? glmzFallback : "a self-contained fictional world";
        public void UseUniverse(Guid id) { CurrentId = id; UniverseScope.BumpEpoch(); }
        public bool UseUniverseBySlug(string slug) => false;
        public void SetFlowUniverse(Guid? id) { }
        public void PersistAsDefault(Guid id) { }
        public void Refresh() { }
    }
}
