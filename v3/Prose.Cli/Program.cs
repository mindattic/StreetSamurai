using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MindAttic.Authentication;
using MindAttic.Authentication.Web;
using Prose.Cli;
using Prose.Core.Data;
using Prose.Core.Extensions;
using Prose.Core.Services;
using MindAttic.Vault.Configuration;

// Force UTF-8 console I/O so piped prose (em-dashes, curly quotes, etc.) round-trips
// correctly through `Get-Content <file> | dotnet run -- --beat update --text -`.
// Without this, Windows defaults to OEM 437, which maps E2 80 94 (UTF-8 em dash) to
// the mojibake sequence "ΓÇö" and corrupts every non-ASCII character in stored beats.
Console.InputEncoding  = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;

// QuestPDF Community license — required call before the first Document.Create.
// This project is the non-commercial indie use case the Community tier exists for.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Documentation generator, deliberately ABOVE the Hub gate: it reads this file's own dispatch
// chain and writes a markdown reference. It touches no database and needs no running Hub, and
// requiring one would make the CLI reference unbuildable in exactly the situation where you most
// want to read it. Mirrors `Prose.Mcp -- --export-tools`, which closes the asymmetry
// docs/ARCHITECTURE.md §8 recorded as an open gap.
//   prose --export-commands [<output-path>] [--source <path-to-Program.cs>]
if (args.Contains("--export-commands"))
{
    var ci = Array.IndexOf(args, "--export-commands");
    var outPath = ci + 1 < args.Length && !args[ci + 1].StartsWith("--") ? args[ci + 1] : "docs/CLI_COMMANDS.md";
    var si = Array.IndexOf(args, "--source");
    var srcPath = si >= 0 && si + 1 < args.Length ? args[si + 1] : "v3/Prose.Cli/Program.cs";
    if (!File.Exists(srcPath))
    {
        // Fail loudly rather than emitting an empty reference: a silently-blank doc is worse
        // than no doc, because it reads as "the CLI has no commands".
        Console.Error.WriteLine($"[export-commands] Source not found: {srcPath}\n" +
                                "  Run from the repo root, or pass --source <path-to-Program.cs>.");
        Environment.ExitCode = 2;
        return;
    }
    var count = CommandDocGenerator.Generate(srcPath, outPath);
    Console.WriteLine($"[export-commands] Wrote {count} command(s) to {outPath}");
    return;
}

// Fail-closed Hub dependency (Phase 2, explicit user decision): "the hub is running, Prose is
// working; hub goes down, Prose is down." Every command gates on the Hub being reachable and
// healthy before anything else runs — no silent fallback to the old direct-in-process behavior.
// The one exception: --worker-mode runs on a rented remote GPU pod talking to a separate
// Azure-hosted coordinator and its own local LLM endpoint — it has no access to this machine's
// loopback-only Hub by construction, not by choice, and gets its own equivalent fail-closed
// check against ITS hard dependency (see WorkerModeCli) instead of this one.
if (!args.Contains("--worker-mode"))
    HubGate.EnsureReachableOrExit();

// Multi-universe: a global `--universe <slug>` flag selects which universe this
// process targets (SS-LAW-15). UniverseContext also honors the PROSE_UNIVERSE env
// var (per terminal), so two CLIs can write different universes at once. Parsed
// here before the dispatch chain so every CLI block + the web host inherit it.
// `prose --universe <verb>` is universe MANAGEMENT, not universe SCOPING. The token after
// --universe is then a verb (list/current/use), not a slug, so it must not be parsed as one:
// doing so made `prose --universe current` die with "Unknown universe slug 'current'" during
// service construction, before UniverseCli ever ran. Detected once and reused by the dispatch
// block further down, so the two cannot disagree about what counts as a management command.
var isUniverseManagementCommand =
    args.Length > 0 && args[0] == "--universe"
    && (args.Length == 1 || UniverseCli.IsSubcommand(args[1]));

if (!isUniverseManagementCommand)
    UniverseBootstrap.RequestedSlug ??= UniverseBootstrap.ParseSlug(args);

// HARD RULE: no silent GLMZ default. Before this check, an omitted --universe/PROSE_UNIVERSE
// fell through to UniverseContext's persisted "current_universe" default (in practice, GLMZ)
// — every content-touching command would silently scope to the wrong universe with no error,
// the exact failure mode "Universe division absolute" exists to prevent (a `--slug <SCRY node>`
// lookup against a GLMZ-scoped query filter just returns "not found", never "wrong universe").
// UNIVERSE_AGNOSTIC_COMMANDS is a short, explicit allowlist of the few flags that are genuinely
// cross-universe utilities (each resolves its own per-row UniverseId, not the ambient scope) or
// touch no universe-scoped data at all (auth). Everything else must name its universe explicitly.
if (UniverseBootstrap.RequestedSlug == null
    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROSE_UNIVERSE")))
{
    string[] UniverseAgnosticCommands =
    [
        "--reset-password", "--sync-markdown", "--generate-canon-md", "--migrate-canon-docs",
        "--schema", "--universe", "--help", "-h", "--sql-export", "--gpu", "--runpod",
        "--kdp-status", "--kdp-manifest", "--kdp-mark-published", "--audit-consistency",
        // 2026-08-09: --seed applies raw SqlSeedService.Seeds scripts, each of which either
        // touches no universe-scoped row (schema ALTER/CREATE) or targets its own explicit,
        // hardcoded row (e.g. add_universe_*.sql inserts a specific new Universe row) — there
        // is no ambient universe scope for a seed script to need or default. Found missing
        // while registering the universe_nonfiction/horror/erotica seeds (fresh-machine
        // reproducibility audit): the guard blocked exactly the fresh-DB-bootstrap use case
        // this flag exists for.
        "--seed", "--migrate-sql",
        // Command/Decision Ledger (2026-08-20): system-level audit trail, not scoped to any
        // one universe — CommandLedgerEntry.Universe is a plain informational column, not a
        // filter, and DecisionLedgerEntry has no universe concept at all.
        "--log-decision", "--command-log", "--decision-log",
        // Durable log search (2026-08-21, observability Part E): Serilog files on disk,
        // not universe-scoped data at all.
        "--log-search",
        // Corpus-wide staleness reports: resolve each row's own book, not an ambient scope —
        // same shape as --sync-markdown/--generate-canon-md above.
        "--verification-staleness", "--findings-staleness",
        // Provider health/config touch no universe-scoped data at all.
        "--provider-status", "--set-llm-provider",
        // Corpus-wide relationship backfill: resolves each row against its OWNER's own universe,
        // not an ambient scope (see BackfillCharacterRelationshipsCli).
        "--backfill-character-relationships",
        // Corpus-wide cross-universe contamination scan/cleanup: by definition compares each row's
        // entity universe against its own book's universe — an ambient scope would defeat the point.
        "--fix-cross-universe-contamination",
        // Corpus-wide stale name-match cleanup: checks each row's own beat text directly, no
        // ambient scope needed (see FixBadNameMatchesCli).
        "--fix-bad-name-matches",
        // Pure arithmetic against BookHealthService's tier shapes — touches no DB row at all
        // (see EstimateCostCli).
        "--estimate-cost",
        // Cost accounting is universe-agnostic: the in-process TokenLedger has no universe
        // concept, and CommandCostHistories (which --cost --history reads) has no universe
        // column at all — the cost gate calibrates per COMMAND, across every book.
        "--cost",
        // A beat's place in its own book's reading order is structural, not universe-scoped, and
        // BeatStoryPositionService walks every book via IgnoreQueryFilters. Requiring a scope here
        // would mean complete coverage depends on the caller naming every universe slug correctly
        // — which failed on the first attempt (78.3%, ~2,600 beats missed in a universe whose slug
        // this very error message was stale about).
        "--beat-positions",
        // Corpus-wide corruption scan: TextIntegrityService.ScanAsync uses IgnoreQueryFilters
        // deliberately — a data-integrity scan must see every universe in one pass, never scoped
        // to whatever --universe happened to be passed (see TextIntegrityService's doc comment).
        "--check-text-integrity",
        // Schema-level hygiene check: sys.columns + a source-tree grep, neither of which is
        // scoped to any universe's rows at all (see TemporalHygieneCli).
        "--check-temporal-hygiene",
        // Direct-id lookup against Entities_History via IgnoreQueryFilters() — resolves the
        // row's own universe from the historical data itself, not an ambient scope.
        "--restore-entity",
        // Takes two explicit GUIDs and operates on those rows directly — no ambient scope to
        // resolve (see MergeEntityCli).
        "--merge-entity",
        // Takes two explicit numeric Edge ids and operates on those rows directly — same shape
        // as --merge-entity above (see MergeEdgeCli).
        "--merge-edge",
        // Takes an explicit numeric Edge id directly; --slug (when passed, for beat-number
        // resolution) is looked up via IgnoreQueryFilters() same as MoveBeatToNodeCli — no
        // ambient scope needed (see SetEdgeValidityCli).
        "--set-edge-validity",
        // RelationTypeAliases (added alongside --merge-edge): genuinely global, no UniverseId
        // column at all — relation-type wording ("owns"/"has") isn't story-specific, same
        // rationale as --banned-names above.
        "--relation-aliases",
        // Explicit --id/--slug/--all targeting via IgnoreQueryFilters(), never an ambient
        // universe default — see ArchiveBookCli/TagEntitiesCli's own doc comments.
        "--archive-book", "--tag-entities",
        // Explicit --slug (or corpus-wide) targeting via IgnoreQueryFilters(), same exemption
        // shape as --tag-entities above — see RetireLockedMarkersCli's own doc comment.
        "--retire-locked-markers",
        // Same exemption shape as --retire-locked-markers above — see
        // RetireBibleTitleHeaderCli's own doc comment.
        "--retire-bible-title-header",
        // Corpus-wide data-repair backfill: finds orphaned character/place rows via
        // IgnoreQueryFilters() across every universe by design — see BackfillMissingSubtypeRowsCli.
        "--backfill-missing-subtype-rows",
        // Beat Context Archive (2026-08-21, observability Part F5): takes an explicit --beat-id
        // and resolves the beat's own NodeId from BeatNodes directly — no ambient scope to
        // resolve, same shape as --merge-entity above.
        "--beat-archive",
        // Strand Progress Dashboard: every non-archived book across every universe, by design
        // (IgnoreQueryFilters() — see ProgressCli's own doc comment).
        "--progress",
        // /show lookup: subject could be in any universe; searches every universe by design
        // (IgnoreQueryFilters() — see ShowCli's own doc comment).
        "--show",
        // Corpus-wide self-alias repair: scans every universe's Character/Place/Faction/Weapon
        // aliases by design — see FixSelfAliasesCli's own doc comment.
        "--fix-self-aliases",
        // RFC 0007 "Universe Interchange": each subcommand resolves its own explicit universe
        // (the file's own universe.id for import, a required positional slug for export/sync) —
        // an ambient scope would defeat the point of a cross-app interchange format.
        "--universe-import", "--universe-export", "--universe-sync",
        // Portable-writing-service plan, Phase 4: takes an explicit positional universe slug
        // (BarksExportService.ExportAsync), same shape as --universe-export above.
        "--barks-export",
        // BannedNames (2026-08-26): genuinely global, no UniverseId column at all — the whole
        // point of a Prose-wide ban is that it has no per-universe scope to resolve.
        "--banned-names",
        // Entity beat mentions (2026-08-26): resolves an explicit --entity id/slug directly,
        // same shape as --merge-entity/--restore-entity above — no ambient scope needed.
        "--entity-mentions",
        // CreateUniverseCli (2026-08-30): inserts a brand-new Universe row keyed by its own
        // --slug; there is no existing universe to scope to yet.
        "--create-universe",
        // GrepBeatsCli (2026-08-31): plain substring scan over every Beat.Text corpus-wide.
        // Beats itself carries no UniverseId column at all — the whole point of a corpus-wide
        // defect check (e.g. "did this leaked-metadata bug hit other books too") is to see every
        // universe in one pass, same rationale as --check-text-integrity above.
        "--grep-beats",
        // MoveNodeUniverseCli (2026-08-30): resolves its source node via NodeRefResolver
        // (IgnoreQueryFilters, same shape as --merge-entity/--reparent-node) and its destination
        // via an explicit --to-universe slug — same "resolves its own per-row UniverseId" shape
        // as the other entries in this list, not the ambient scope.
        "--move-node-universe",
        // CloseAllSessionsCli: genuinely cross-universe by design (its own doc comment says
        // "across all nodes") — called automatically by the /commit skill before every commit,
        // where the caller has no reason to know or care which universe(s) have open sessions.
        // EditSessions/EditSessionBeats carry no UniverseId column and no query filter at all
        // (confirmed in ProseDbContext — not in the scoped-entity list), so this was blocked by
        // this outer CLI gate alone, not by anything at the data layer. Found live 2026-08-30
        // while running the /commit skill's mandatory pre-commit step.
        "--close-all-sessions",
        // ArchitectureScanCli (2026-09-01): pure source-tree/filesystem scan (services, DI
        // registrations, CLI verbs, MCP tools, scripts) — touches no DB row and no
        // universe-scoped data at all, same rationale as --estimate-cost above.
        "--architecture-scan",
    ];
    var isAgnostic = args.Length == 0 || UniverseAgnosticCommands.Any(args.Contains);
    if (!isAgnostic)
    {
        Console.Error.WriteLine(
            "[universe] No universe scope given. Pass --universe glmz|scry|gspl (or set PROSE_UNIVERSE) — " +
            "this command touches universe-scoped data and no longer silently defaults to GLMZ.");
        Environment.ExitCode = 2;
        return;
    }
}

// CLI mode: dotnet run --project ... -- --rebuild-graph [--universe <slug>]
// Rebuilds the scoped universe's <slug>_universe_graph.json cache from source data
// without starting the web server. One universe per invocation (scope is pinned below).
if (args.Contains("--rebuild-graph"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RebuildGraphCli", args);
    return;
}

// CLI mode: prose --reset-password --email <e> --password <p> [--require-change]
// Operator password reset over the MindAttic.Authentication store, no web server.
if (args.Contains("--reset-password"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ResetPasswordCli", args);
    return;
}

// --write-story / --refine-story (StoryDirectorService's "Surprise Me" pipeline) removed
// 2026-08-13 (plan "Prose, objectively...") — confirmed unused workflow, deleted outright per
// explicit instruction, not quarantined. The live book-writing path is --auto-run (AutoRunCli).

// CLI mode: book operations — list / new / show / chapters / absorb / review / apply / export / delete.
// Run `dotnet run --project Prose.Blazor -- --book` (no subcommand) to see full usage.
if (args.Contains("--book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BookCli", args);
    return;
}

// CLI mode: unified continuity store — migrate / stats / contradictions / resolve / entity.
// Run `dotnet run --project Prose.Blazor -- --continuity` (no subcommand) to see full usage.
if (args.Contains("--continuity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ContinuityCli", args);
    return;
}

// CLI mode: SQL Server migration — apply EF migrations and import JSON entities.
//   prose --migrate-sql --schema           apply EF migrations
//   prose --migrate-sql --import people    import character JSON files
//   prose --migrate-sql --all              schema + import all supported types
if (args.Contains("--migrate-sql"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MigrateSqlCli", args);
    return;
}

// CLI mode: prose → entities + edges. LLM-driven.
//   prose --interpret --text "..."  | --file path.txt
//   add --commit to apply, --auto-create to stub missing entities, --tag <source>
if (args.Contains("--interpret"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("InterpretCli", args);
    return;
}

// CLI mode: insert a worldbuilding Document directly into canon.
//   prose --add-doc --title "…" --body-file path.md [--category essay] [--tags "a,b,c"] [--filename slug.md]
if (args.Contains("--add-doc"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddDocCli", args);
    return;
}

// CLI mode: insert a Character from a CharacterData JSON file.
//   prose --add-character --file path.json
if (args.Contains("--add-character"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddCharacterCli", args);
    return;
}

// CLI mode: insert OR update a Place/District from a DistrictData JSON file.
// Upsert: include "id" to update, omit to create. Safe service-layer path
// (DistrictRepository.Save) — no hand-SQL, collision-safe slugs.
//   prose --add-place --file path.json [--print]
if (args.Contains("--add-place"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddPlaceCli", args);
    return;
}

// CLI mode: read a Place/District's full DistrictData record by exact name — the read-side
// counterpart to --add-place, so a rename/edit can round-trip the existing record instead of
// upserting blind and clobbering fields the caller didn't know about.
//   prose --get-place --name "<exact name>" [--print-raw]
if (args.Contains("--get-place"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GetPlaceCli", args);
    return;
}

// CLI mode: plain substring search over every Beat.Text corpus-wide (all universes) —
// read-only, no LLM cost. Built to check whether a defect found in one book (e.g. leaked
// LLM repair-pass scaffolding) also hit others.
//   prose --grep-beats --pattern "<text>" [--case-sensitive]
if (args.Contains("--grep-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GrepBeatsCli", args);
    return;
}

// CLI mode: insert a CorpoNation from a CorponationData JSON file.
//   prose --add-corponation --file path.json
if (args.Contains("--add-corponation"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddCorponationCli", args);
    return;
}

// CLI mode: insert a Weapon from a WeaponryData JSON file.
//   prose --add-weapon --file path.json
if (args.Contains("--add-weapon"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddWeaponryCli", args);
    return;
}

// CLI mode: insert an Apparel item from an ApparelData JSON file.
//   prose --add-apparel --file path.json
if (args.Contains("--add-apparel"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddApparelCli", args);
    return;
}

// CLI mode: generate a resource-tracked combat sequence via CombatSceneWriter.
//   prose --combat --file scene.json [--out prose.txt]
//   prose --combat --location "Hegewisch" --objective "..." --exchanges 6 --tone Cinematic
if (args.Contains("--combat"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CombatCli", args);
    return;
}

// CLI mode: insert OR update a Faction from a FactionData JSON file.
// Upsert: include "id" to update, omit to create. Safe service-layer path
// (FactionRepository.Save) — no hand-SQL, collision-safe slugs.
//   prose --add-faction --file path.json
if (args.Contains("--add-faction"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddFactionCli", args);
    return;
}

// CLI mode: insert a News article from a NewsData JSON file.
//   prose --add-news --file path.json
if (args.Contains("--add-news"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddNewsCli", args);
    return;
}

// CLI mode: per-table schema operations (snapshot + safe column-reorder rebuild).
//   prose --schema snapshot --table NAME [--out path.sql]
//   prose --schema rebuild  --table NAME --order "col1,col2,col3,…"
if (args.Contains("--schema"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SchemaCli", args);
    return;
}

// CLI mode: dump the entire Prose DB to a re-runnable .sql script.
//   prose --sql-export --schema             schema-only DDL
//   prose --sql-export --data               schema + INSERT data
//   prose --sql-export --schema --out path  override output path
if (args.Contains("--sql-export"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SqlExportCli", args);
    return;
}

// prose --swain-audit [--slug <slug> | --code <code> | --all] [--repair] [--blockers]
// Classifies every enabled beat as Scene / Sequel / Ambiguous / Deficient against
// Dwight Swain's Scene/Sequel doctrine. Deficient = BLOCKER; Ambiguous = MODERATE.
// Add --repair to auto-splice the missing structural element (disaster turn, decision, etc.)
// into BLOCKER beats via Haiku (classify) + Sonnet (splice). Exit 0 = success.
// MUST appear before the bare --repair handler below.
if (args.Contains("--swain-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SwainAuditCli", args);
    return;
}

// CLI mode: dossier-driven story repair — walks every chapter, augments character
// records with timeline entries and (optionally) LLM-extracted continuity claims.
//   prose --repair                # cheap timeline-only pass
//   prose --repair --continuity   # also run continuity extraction (LLM-heavy)
if (args.Contains("--repair"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RepairCli", args);
    return;
}

// CLI mode: cloud RAG over the canon corpus. Replaces the retired Ollama path.
//   prose --ask "Question" [--k 8] [--type character]
if (args.Contains("--ask"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AskCli", args);
    return;
}

// CLI mode: report Character columns that disagree with their latest
// matching EntityStateEvents row. Lights up the static-vs-dynamic recipe
// only for columns that actually drifted.
//   prose --audit-drift           pretty-printed report
//   prose --audit-drift --json    JSON dump
if (args.Contains("--audit-drift"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AuditDriftCli", args);
    return;
}

// CLI mode: backfill EntityStateEvents from the dynamic columns currently
// sitting on Characters (Location, LifeStatus, Role, Affiliation, Belongings*,
// Territory*, DailyLife). One-shot, idempotent.
if (args.Contains("--backfill-character-state"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("StateBackfillCli", args);
    return;
}

// CLI mode: rewrite ethnicity-keyed visual descriptors in image prompts to
// match a character's current genetic_ancestry. Cost-aware via stored hash.
//   prose --image-prompts regen --id <id|slug> [--force]
//   prose --image-prompts regen --all-changed
if (args.Contains("--image-prompts"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ImagePromptsCli", args);
    return;
}

// CLI mode: propose a plausible immediate family for one character.
//   prose --family-gen propose --of <id|slug>           dry run
//   prose --family-gen propose --of <id|slug> --commit  write characters + edges + propagate genetics
//   --seed N for reproducible RNG
if (args.Contains("--family-gen"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FamilyGenCli", args);
    return;
}

// CLI mode: propagate genetic_ancestry from parents to children via the
// family graph (with ±5% recombination noise). Currently a no-op until family
// ties are seeded.
//   prose --genetics propagate                     full graph
//   prose --genetics propagate --id <id|slug>      single character
//   prose --genetics propagate --seed 42           reproducible RNG
if (args.Contains("--genetics"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GeneticsCli", args);
    return;
}

// CLI mode: family ties — hand-seed parent/sibling/spouse links between characters.
//   prose --family parent  --parent <id|slug> --child <id|slug>
//   prose --family sibling --a <id|slug> --b <id|slug>
//   prose --family spouse  --a <id|slug> --b <id|slug>
//   prose --family show    --of <id|slug>
if (args.Contains("--family"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FamilyCli", args);
    return;
}

// CLI mode: scan beats for deprecated/renamed noun references.
//   prose --validate-nouns --slug <slug>
if (args.Contains("--validate-nouns"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ValidateNounsCli", args);
    return;
}

// CLI mode: CRUD for DeprecatedEntityNames (list/add/remove).
//   prose --deprecated-names --list [--universe <slug>]
//   prose --deprecated-names --add --universe <slug> --name <deprecatedName> --canonical <canonicalName> [--notes <notes>]
//   prose --deprecated-names --remove --id <id>
if (args.Contains("--deprecated-names"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DeprecatedNameCli", args);
    return;
}

// CLI mode: CRUD for BannedNames — Prose-wide hard name ban, enforced at write time.
//   prose --banned-names --list
//   prose --banned-names --add --name <name> [--notes <notes>]
//   prose --banned-names --remove --id <id>
if (args.Contains("--banned-names"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BannedNameCli", args);
    return;
}

// CLI mode: CRUD for RelationTypeAliases — normalizes link_entities free-text RelationType
// wording (e.g. "has" -> "owns") so the same relationship doesn't fork into multiple Edge rows.
//   prose --relation-aliases --list
//   prose --relation-aliases --add --alias <wording> --canonical <standardizedRelationType> [--notes <notes>]
//   prose --relation-aliases --remove --id <id>
if (args.Contains("--relation-aliases"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RelationAliasCli", args);
    return;
}

// CLI mode: surgical CRUD over a character's CharacterRelationships rows. Added 2026-09-02 —
// there was previously NO sanctioned way to remove a single relationship row (see
// EntityRelationshipCli's doc comment and the Seo Jisun cross-book contamination).
// Deliberately universe-scoped: --character resolves through db.Characters, which the Entity
// query filter scopes, so this stays out of UniverseAgnosticCommands.
//   prose --entity-relationships --character <name-or-id> [--json] [--orphans]
//   prose --entity-relationships --character <name-or-id> --remove --id <rowId>
//   prose --entity-relationships --character <name-or-id> --add --target <name> --type <type>
if (args.Contains("--entity-relationships"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EntityRelationshipCli", args);
    return;
}

// CLI mode: an entity's tags — list / add / REMOVE. Added 2026-09-03: tags could be added and
// never taken away (create_character's `tags` MERGES, like aliases), so a wrong tag was permanent,
// and a stale book tag can pull a character into that book's context loads. NOT the same as
// --tag-entities, which rewrites inline <entity guid="…"> markup inside beat text.
//   prose --entity-tags --entity <guid-or-name> [--json]
//   prose --entity-tags --entity <guid-or-name> --remove "tag1,tag2"
//   prose --entity-tags --entity <guid-or-name> --add "tag1,tag2"
if (args.Contains("--entity-tags"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EntityTagsCli", args);
    return;
}

// CLI mode: surgical CRUD over a character's signature-gear / pharmaceuticals list. Added
// 2026-09-03 — there was no sanctioned way to remove ONE gear entry; create_character
// round-trips the whole record through the delete-all-and-reinsert mapper, so correcting a
// single invented item risked every other field. Logic lives in CharacterGearService, shared
// with the *_character_gear MCP tools. Universe-scoped (--character resolves through
// db.Characters), so it stays out of UniverseAgnosticCommands.
//   prose --character-gear --character <name-or-id> [--bucket <b>] [--json]
//   prose --character-gear --character <name-or-id> --remove --id <rowId>
//   prose --character-gear --search "<text>" [--json]
if (args.Contains("--character-gear"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CharacterGearCli", args);
    return;
}

// CLI mode: list a book's chapter units in reading order — the 100 ft rung of the Three
// Altitudes, previously unreachable from the CLI (every read path was a flat beat list, which
// is why a full-book read fell back to the one-line Description spine). Story Ledger Phase 1.
// Reuses SynopsisExportService's segmentation, so the listing matches story-synopsis.txt.
//   prose --chapters --slug <slug-or-code-or-id> [--json]
if (args.Contains("--chapters"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ChaptersCli", args);
    return;
}

// CLI mode: report beats whose Beat.Description was verified against prose that has since
// changed (DescriptionHash != TextHash). Deterministic — no LLM, no embeddings, no cost.
// Report-only per docs/LOGIC.md §4. Story Ledger Phase 1.
//   prose --description-drift --slug <slug-or-code-or-id> [--json]
//   prose --description-drift --all --universe <slug>
if (args.Contains("--description-drift"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DescriptionDriftCli", args);
    return;
}

// CLI mode: the Story Ledger's Tuned Read (Phase 2) — walks a book in reading order, keeps its
// fact ledger fresh, pairs claims an exclusion axiom says cannot both be true, adjudicates only
// those pairs, and files a finding for each contradiction whose quote survives the mechanical
// grounding gate. Report-only (docs/LOGIC.md §4). Cost-gated: a real run spends one Sonnet call
// per uncached candidate. --dry runs the whole deterministic half for free.
//   prose --tuned-read --slug <slug> [--dry] [--no-extract] [--max-candidates N] [--json]
if (args.Contains("--tuned-read"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("TunedReadCli", "--tuned-read", args);
    return;
}

// CLI mode: judges the fact ledger's SAME-PREDICATE contradiction groups against the prose they
// came from. The deterministic exemptions (volatile / set-valued / paraphrase) already removed
// everything that was never a contradiction; what reaches here is dominated by complementary
// facets and temporal states, which need the prose to tell apart from a real conflict. Writes
// claim STATUS only, never prose (docs/LOGIC.md §4). Cost-gated: one Sonnet call per uncached
// group, cached on the claim uids plus every anchor beat's current TextHash.
//   prose --ledger-adjudicate --slug <slug> [--dry] [--max N] [--entity <t>] [--predicate <t>] [--json]
if (args.Contains("--ledger-adjudicate"))
{
    // An --entity/--predicate run judges ONE group (~$0.03); a whole-book run is $6-8. The cost
    // estimator keys purely on this command name, so letting both shapes calibrate together
    // teaches it that --ledger-adjudicate is cheap and the $0.10 gate then stops warning before a
    // real book run. Separate names, separate histories.
    var filtered = args.Contains("--entity") || args.Contains("--predicate");
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync(
        "LedgerAdjudicateCli", filtered ? "--ledger-adjudicate --filtered" : "--ledger-adjudicate", args);
    return;
}

// CLI mode: the Story Ledger's provenance surface (Phase 3) — "what is in canon that no human
// ever approved?", plus the explicit human act that promotes one candidate row to authored.
// Deterministic and free; report-only for the audit (docs/LOGIC.md §4). Universe-scoped
// deliberately: the entity/relationship counts come through the ambient query filter.
//   prose --provenance-audit [--slug <slug-or-code-or-id>] [--samples N] [--json]
//   prose --provenance --grade <grade> --entity <id> | --relationship <rowId> | --claim <uid>
//   prose --provenance --grades
if (args.Contains("--provenance-audit") || args.Contains("--provenance"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProvenanceCli", args);
    return;
}

// CLI mode: manage the PredicateExclusion ontology the Tuned Read runs on — list, propose,
// approve/reject, and --test a rule against a hypothetical claim pair before approving it.
// Deterministic and free; no LLM call anywhere in this command.
//   prose --exclusion-rules [--all] [--json]
//   prose --exclusion-rules --propose --predicate-a <p> --predicate-b <p> --why "..." [--universal]
//   prose --exclusion-rules --approve|--reject --id <n>
//   prose --exclusion-rules --test --predicate-a <p> --object-a "..." --predicate-b <p> --object-b "..."
if (args.Contains("--exclusion-rules"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExclusionRulesCli", args);
    return;
}

// CLI mode: list every beat that mentions a given entity (node, beat number, excerpt).
//   prose --entity-mentions --entity <id|slug> [--limit <n>]
if (args.Contains("--entity-mentions"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EntityMentionsCli", args);
    return;
}

// CLI mode: DataConsistencyService SSOT-drift audit (SQL-only, no LLM calls).
//   prose --audit-consistency [--json]
if (args.Contains("--audit-consistency"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DataConsistencyCli", args);
    return;
}

// CLI mode: GraphHealthService — orphaned/weakly-connected/malformed world-graph node audit.
//   prose --graph-health --universe <slug> [--json]
if (args.Contains("--graph-health"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GraphHealthCli", args);
    return;
}

// CLI mode: DataScanUtility family (fix-phi/fix-identity/tag-lethality/tag-normalize/
// assign-tiers/cross-reference) -- mass canon-entity maintenance tools. Defaults to a dry-run
// preview; pass --apply to actually write.
//   prose --data-scan --tool <name> [--apply] [--overwrite] --universe <slug>
if (args.Contains("--data-scan"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DataScanCli", args);
    return;
}

// prose --repair-slugs [--apply] [--family entities|nodes|books|series|episodes] [--json]
// Regenerate every slug from its Name/Title metadata and update slug-carrying
// references (beat audio paths, publication paths, on-disk dirs, alt_slug).
// DRY-RUN by default; --apply writes. Slugs are loose keys — guid is the key.
if (args.Contains("--repair-slugs"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SlugRepairCli", args);
    return;
}

// CLI mode: survey management.
//   prose --list-surveys [--status Open|Completed]
//   prose --get-survey --slug <slug>
if (args.Contains("--list-surveys") || args.Contains("--get-survey"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SurveyCli", args);
    return;
}

// CLI mode: backfill BeatEntityMentions — index which entity names appear in
// each beat so entity-update staleness propagation works.
//   prose --scan-entity-mentions
if (args.Contains("--scan-entity-mentions"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ScanEntityMentionsCli", args);
    return;
}

// CLI mode: retroactive inline entity-GUID tagging backfill (corpus-trust-recovery Phase 1a/1b).
//   prose --tag-entities (--id <guid> | --slug <slug> | --all) [--dry-run]
if (args.Contains("--tag-entities"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("TagEntitiesCli", args);
    return;
}

// CLI mode: Bible->Outline refactor Phase 6a -- retire "LOCKED" markers (author ruling
// 2026-08-29, decision #3: the LOCK concept is retired, no corner auto-wins). Dry-run first.
//   prose --retire-locked-markers --dry-run [--slug <slug>]
//   prose --retire-locked-markers --apply [--slug <slug>]
if (args.Contains("--retire-locked-markers"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RetireLockedMarkersCli", args);
    return;
}

// CLI mode: retire the stale "# NODE BIBLE: [Title]" header baked into pre-fix generated
// outlines (NodeOutlineService's LLM prompt template). Dry-run first, same shape as
// --retire-locked-markers above.
//   prose --retire-bible-title-header --dry-run [--slug <slug>]
//   prose --retire-bible-title-header --apply [--slug <slug>]
if (args.Contains("--retire-bible-title-header"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RetireBibleTitleHeaderCli", args);
    return;
}

// CLI mode: backfill Entities.Status = 'stub' / 'canon' based on BeatEntityMentions.
//   prose --backfill-stubs
// Entities with no BeatEntityMentions row → Status='stub' (excluded from universe graph).
// Entities that ARE mentioned → Status='canon'. Re-run after --scan-entity-mentions.
if (args.Contains("--backfill-stubs"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillStubsCli", args);
    return;
}

// CLI mode: dump canon JSON to the user's Downloads folder.
//   prose --export global                every repo, zipped + timestamped
//   prose --export <repoName>            one repo, zipped (e.g. "people", "weaponry")
//   prose --export <entityId>            one entity, plain .json
if (args.Contains("--export"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExportCli", args);
    return;
}

// CLI mode: rebuild the entity-embedding cache via cloud OpenAI.
//   prose --reembed              drift-skipped corpus pass (only changed entities re-embed)
//   prose --reembed --force      clear the table first, re-embed everything
if (args.Contains("--reembed"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReembedCli", args);
    return;
}

// CLI mode: query the Legion / LLMVoting cloud-LLM panel directly.
//   prose --legion ask "Q" --options "A,B,C"  → forced-choice Quorum decision (JSON on stdout)
//   prose --legion vote "Q" [--context "…"]    → open-ended vote with synthesized narrative
if (args.Contains("--legion"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LegionCli", args);
    return;
}

// --archive-json retired 2026-05-08 with JsonArchivalService — engine/data/*.json
// no longer exists, so legacy-file verification is moot.

// CLI mode: apply canonical SQL seeds via C# (replaces sqlcmd-by-hand workflow).
//   prose --seed                     list known seeds
//   prose --seed <name>              apply one
//   prose --seed --all [--force]     apply every known seed in order
// NOTE: --seed is also the prompt flag of --write-node / --write-story /
// --create-book — those commands must win the dispatch or their calls get
// hijacked by the SQL seeder.
if (args.Contains("--seed") && !args.Contains("--write-node")
    && !args.Contains("--write-story") && !args.Contains("--create-book")
    && !args.Contains("--run-corpus"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SeedCli", args);
    return;
}

// CLI mode: (re)generate the node bible for an existing node.
// Renamed from --book-outline (2026-08-30) — too easily confused with the read-only
// --get-book-outline; this one calls an LLM and can destructively regenerate the bible.
//   prose --generate-book-outline --slug <slug> [--beats N] [--replace-beats]
if (args.Contains("--generate-book-outline"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("NodeOutlineCli", args);
    return;
}

// CLI mode: hand-write the node bible verbatim (CLI mirror of MCP SetBookOutline).
//   prose --set-book-outline --slug <slug> --file <path-to-bible.md>
if (args.Contains("--set-book-outline"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetBookOutlineCli", args);
    return;
}

// CLI mode: remove a bad entity alias row (dry-run unless --apply). The sanctioned fix for
// alias pollution — an ordinary phrase registered as an alias, which makes EntityMentionScanner
// tag that phrase as the entity corpus-wide.
//   prose --delete-alias --value "<alias>" [--type <character|place|…>] [--apply]
if (args.Contains("--delete-alias"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DeleteAliasCli", args);
    return;
}

// CLI mode: add one alias row to one entity (dry-run unless --apply). The other half of
// --delete-alias — re-binding a name the prose actually uses to the entity that owns it, which
// until now had no path outside create_character's `aliases` parameter.
//   prose --add-alias --value "<alias>" --entity <id-or-name> [--apply] [--force]
if (args.Contains("--add-alias"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AddAliasCli", args);
    return;
}

// CLI mode: dump the node bible VERBATIM (the read half of --set-book-outline's round trip).
// NOT --generate-book-outline (renamed from --book-outline 2026-08-30), which generates a
// fresh bible via an LLM instead of reading the existing one.
//   prose --get-book-outline --slug <slug|code|guid> [--out <path>]
if (args.Contains("--get-book-outline"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GetBookOutlineCli", args);
    return;
}

// CLI mode: regenerate canon document .md files from DB (CanonDocuments + CanonDocumentSections).
// The disk files are generated read-only mirrors; source of truth is the DB.
//   prose --generate-canon-md --type <WorldBible|WorldMaster|Franchise|UniverseCanon>
//   prose --generate-canon-md --all
if (args.Contains("--generate-canon-md"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CanonDocumentCli", args);
    return;
}

// CLI mode: read/edit CanonDocumentSections directly — the CLI equivalent of the MCP tools
// list_canon_sections / set_canon_section, built 2026-08-23 to close the gap where canon
// editing was MCP-only and unreachable from a CLI-only session.
//   prose --list-canon-sections --type <DocumentType> [--universe <slug>]
//   prose --set-canon-section --type <DocumentType> --key <sectionKey> --file <path.md> [--title <t>] [--universe <slug>]
if (args.Contains("--list-canon-sections"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ListCanonSectionsCli", args);
    return;
}
if (args.Contains("--get-canon-section"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GetCanonSectionCli", args);
    return;
}

// CLI mode: search seeded entities by name or alias — the read-side counterpart to
// --add-character, so authoring can check for an existing entity before creating a duplicate.
//   prose --find-entity --name "<text>" [--type character] [--universe <slug>] [--limit N]
if (args.Contains("--find-entity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FindEntityCli", args);
    return;
}
if (args.Contains("--set-canon-section"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetCanonSectionCli", args);
    return;
}

// CLI mode: assemble the unified Book Context Document for a node.
// Merges hand-authored NodeOutline + Structural Blueprint + Beat Spine into one document,
// writes the merged view to docs/nodes/{CODE}.md (read-only disk mirror) only. Nodes.NodeOutline
// itself stays pure hand-authored content (fixed 2026-08-14 — it used to get the merged blob
// written back, so the column named "the bible" stopped meaning only the bible).
//   prose --generate-node-doc --slug <slug>
//   prose --generate-node-doc --all
if (args.Contains("--generate-node-doc"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("NodeDocCli", args);
    return;
}

// prose --set-narrative-mode --slug <slug> --mode original|retelling|historical
// Gates BookHealthService.SacredFlawAsync — see SetNarrativeModeCli.
if (args.Contains("--set-narrative-mode"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetNarrativeModeCli", args);
    return;
}

// --seed-gospel-cast, --seed-glmz-gap-fill, --seed-gap-fill-round2, --seed-gap-fill-round3,
// --fix-david-mistag: five one-time, non-repeatable corpus data-migration commands, all
// completed, removed 2026-08-30 (a holistic-cleanup pass flagged them as permanent CLI surface
// for finished campaigns — the exact "unmanaged pile of one-off tools" pattern being cleaned
// up). Their handler classes (SeedGospelCastCli, SeedGlmzGapFillCli, SeedGapFillRound2Cli,
// SeedGapFillRound3Cli, FixDavidMistagCli) are preserved in git history if ever needed again.

// prose --reconcile-trinity --extract|--survey --slug <slug>|--all
// prose --reconcile-trinity --slug <slug>|--all --allow-votes --confirm-auto-edit [--dry-run]
// prose --reconcile-trinity --undo --decision-id <guid>
// Autonomous-but-reversible Bible/Book/Entity divergence resolution for GLMZ/SCRY/FICTION books —
// see ReconcileTrinityCli / TrinityReconciliationService.
if (args.Contains("--reconcile-trinity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReconcileTrinityCli", args);
    return;
}

// CLI mode: regenerate a universe's Master Glossary (Glossary.htm/.json/.txt under
// docs/universes/{SLUG}/) from the GlossaryTerms table.
//   prose --generate-glossary --universe <slug>   (omit --universe for all)
if (args.Contains("--generate-glossary"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GlossaryCli", args, method: "RunMasterAsync");
    return;
}

// CLI mode: regenerate a book's Glossary (docs/nodes/{CODE}-Glossary.htm/.json/.txt) — the
// subset of its universe's Master Glossary whose terms appear in the book's live prose.
//   prose --generate-book-glossary --slug <slug>
//   prose --generate-book-glossary --all
if (args.Contains("--generate-book-glossary"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GlossaryCli", args, method: "RunBookAsync");
    return;
}

// CLI mode: generate Node.CoverPrompt (image-model cover description) from the book's
// own Title/Summary/Description/universe.
//   prose --generate-cover-prompt --slug <slug>
//   prose --generate-cover-prompt --all
if (args.Contains("--generate-cover-prompt"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("GenerateCoverPromptCli", "--generate-cover-prompt", args);
    return;
}

// CLI mode: render Node.CoverPrompt through an image provider (openai/stability/google)
// and save the cover under the media dir. Costs real money — requires an API key.
//   prose --generate-cover-image --slug <slug> --provider openai|stability|google
if (args.Contains("--generate-cover-image"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GenerateCoverImageCli", args);
    return;
}

// CLI mode: composite a book's cover onto a 3D mockup template, generate a short AI
// image-to-video clip (hand shows the cover, opens it, flips pages) via a chosen video
// provider (kling/runway/sora), and assemble a vertical 1080x1920 #booktok MP4. Costs
// real money per call unless --dry-run, which stops after the local ImageMagick mockup.
//   prose --booktok --slug <slug> --provider kling|runway|sora [--duration 8] [--dry-run] [--yes]
//   prose --booktok --standalone --cover-path <path> --title "<title>" --provider kling|runway|sora
if (args.Contains("--booktok"))
{
    if (args.Contains("--dry-run"))
    {
        // No paid API call happens in dry-run — skip the cost gate entirely.
        Environment.ExitCode = await HubCliClient.ForwardAsync("BookTokCli", args);
        return;
    }
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("BookTokCli", "--booktok", args);
    return;
}

// CLI mode: redraw the title onto an already-saved cover image without calling an
// image-generation API again.
//   prose --composite-cover-title --slug <slug>
if (args.Contains("--composite-cover-title"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CompositeCoverTitleCli", args);
    return;
}

// CLI mode: generate a new node (bible-first: plan → planned beats → expand in UI).
// CLI mode: autonomous corpus loop — generate N nodes end-to-end and review them.
//   prose --run-corpus --count N [--seed "..."] [--kind episode] [--beats 12] [--ballots 20] [--resume] [--dry-run]
if (args.Contains("--run-corpus"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RunCorpusCli", args);
    return;
}

// CLI mode: expand planned beats in a node to prose (headless ✨ for each beat).
//   prose --edit-beat --slug <slug> (--beat-number N | --insert-after N) --file <path>
if (args.Contains("--edit-beat"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EditBeatCli", args);
    return;
}

// CLI mode: re-slot a beat within its node's reading order (wraps NodeWorkbenchService
// .MoveBeatAsync, previously reachable only from the Blazor drag-and-drop UI).
//   prose --move-beat --slug <slug> --beat-number N --after M   (M=0 moves to the top)
if (args.Contains("--move-beat"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MoveBeatCli", args);
    return;
}

// CLI mode: relocate a beat OUT of one chapter and INTO another (--move-beat only re-slots
// within a single node's existing siblings). Wraps NodeWorkbenchService.MoveBeatToNodeAsync.
//   prose --move-beat-to-node --slug <from-slug> --beat-number N --to-slug <to-slug> --after M
if (args.Contains("--move-beat-to-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MoveBeatToNodeCli", args);
    return;
}

// CLI mode: enable/disable a beat's membership in a node's reading order without touching the
// Beat row itself (wraps NodeWorkbenchService.SetBeatMembershipEnabledAsync).
//   prose --set-beat-enabled --slug <slug> (--beat-number N | --beat-id <guid>) [--enable]
if (args.Contains("--set-beat-enabled"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetBeatEnabledCli", args);
    return;
}

// CLI mode: create a new empty root node (bible-first; no beats yet).
//   prose --create-book --title "..." [--code SRZR] [--kind book] [--description "..."] [--seed "..."] [--previous <slug|id>] [--parent <slug|id>]
if (args.Contains("--create-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CreateNodeCli", args);
    return;
}

//   prose --expand-beat (--slug <slug> | --id <guid>) [--beat <beatId>] [--force]
if (args.Contains("--expand-beat"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExpandBeatCli", args);
    return;
}

//   prose --auto-run (--slug <slug> | --id <guid>) [--effort draft|standard] [--dry-run] [--force]
if (args.Contains("--auto-run"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("AutoRunCli", "--auto-run", args);
    return;
}

//   prose --write-node --seed "..." [--title "..."] [--kind episode] [--beats 12] [--outline-only]
if (args.Contains("--write-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("WriteNodeCli", args);
    return;
}

// CLI mode: delete the 44 legacy book/chapter Entity+Records blobs whose
// content already lives in the Nodes/Beats model. Classifies each as JUNK,
// REDUNDANT, or ORPHAN (converts orphans to Nodes before deleting).
//   prose --migrate-legacy-book-chapter
if (args.Contains("--migrate-legacy-book-chapter"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MigrateLegacyBookChapterCli", args);
    return;
}

// Truth-First Architecture — Step A2: migrate hand-editable canon .md files
// (BIBLE.md, WORLD.md, FRANCHISE.md, universes/CAUL.md) into CanonDocument +
// CanonDocumentSection DB rows. Idempotent; skips already-migrated documents.
//   prose --migrate-canon-docs [--dry-run]
if (args.Contains("--migrate-canon-docs"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MigrateCanonDocsCli", args);
    return;
}

// Truth-First Architecture — Step B2: decompose EscalationCurveJson /
// EventTypePaletteJson blobs and BeatTags into per-beat BeatBlueprintDecision rows.
// Idempotent; skips beats that already have a decision row.
//   prose --migrate-blueprint-rows [--slug <slug>] [--dry-run]
if (args.Contains("--migrate-blueprint-rows"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MigrateBlueprintRowsCli", args);
    return;
}

//   prose --verify-beat --id <beatId> [--json]
//   prose --verify-book --slug <slug> [--json]
//   prose --verify-quote --id <beatId> --quote "<claimed text>" [--claimed-by <name>] [--json]
//   prose --verify-quotes-batch --json-file <path> [--json]
//   prose --verification-staleness [--json]
// Beat Verification Engine (Track C): checks prose against declared BeatBlueprintDecision
// contract. Results upserted to BeatVerification table. BLOCKER findings block --export-node.
// QuoteGrounding checks: confirm a logic-sweep audit agent's claimed quote actually appears
// in the beat it's attributed to, before that finding is trusted for triage/fix (SS-LOGIC-4a).
// --verification-staleness: which books have BeatVerification rows computed under an older
// CurrentRuleVersion and need a --verify-book/--audit-book re-run (2026-08-10 — added after the
// same "book never re-run after a check-logic fix" gap was found and manually re-diffed twice
// in one session; see BeatVerification.RuleVersion's doc comment).
if (args.Contains("--verify-beat") || args.Contains("--verify-book")
    || args.Contains("--verify-quote") || args.Contains("--verify-quotes-batch")
    || args.Contains("--verification-staleness"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("VerifyBeatCli", args);
    return;
}

// prose --findings-staleness [--json]
// RFC 0011 Brick 2: generic staleness report across every Findings category that stamps
// SourceRuleVersion on write (currently CraftChecklist + StructuralFailure).
if (args.Contains("--findings-staleness"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FindingsStalenessCli", args);
    return;
}

// prose --provider-status [--live] [--json]
// RFC 0011 Brick 3: degraded-services status on demand. See docs/PROVIDERS.md.
if (args.Contains("--provider-status"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProviderStatusCli", args);
    return;
}

// prose --set-llm-provider claude-api|claude-team [--dry-run]
// Switches every Settings.json field governing which Claude credential path is active in one
// command (ActiveLlmProvider always; ReviewJudgeProvider/ReviewAllowedProviders/
// ReaderQaJuryProviders only where they currently hold the other Claude variant).
if (args.Contains("--set-llm-provider"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetLlmProviderCli", args);
    return;
}

// prose --backfill-character-relationships [--dry-run] [--json]
// One-time repair for CharacterRelationships.TargetEntityId never being resolved at save time.
if (args.Contains("--backfill-character-relationships"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillCharacterRelationshipsCli", args);
    return;
}

// prose --backfill-entity-presence [--slug <slug>] [--dry-run]
// Re-runs SceneContextAssembler's name/alias/embedding scan (no LLM call) against already-written
// beats with no BeatEntities roster yet — lets a missing-alias fix take effect without a live
// generation pass.
if (args.Contains("--backfill-entity-presence"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillEntityPresenceCli", args);
    return;
}

// prose --fix-cross-universe-contamination [--dry-run]
// Deletes BeatEntities/BeatEntityPresence rows whose entity belongs to a different universe than
// the beat's own book (a hard "Universe division absolute" violation) — historical bad data, not
// a live matching-pipeline bug (see FixCrossUniverseContaminationCli for root-cause detail).
if (args.Contains("--fix-cross-universe-contamination"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FixCrossUniverseContaminationCli", args);
    return;
}

// prose --fix-bad-name-matches [--dry-run]
// Deletes BeatEntities rows where MatchSource='name' but the entity's Name no longer appears in
// the beat's current Text (a checkable, unambiguous staleness signal name-matches carry that
// embedding/graph matches don't). See FixBadNameMatchesCli for root-cause detail.
if (args.Contains("--fix-bad-name-matches"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FixBadNameMatchesCli", args);
    return;
}

// prose --backfill-pov [--slug <slug>] [--dry-run]
// Heuristically tags each beat's highest-scoring character-type BeatEntities row as
// BeatEntityPresence PresenceType='pov' — closes the gap where DocContextService's per-beat
// voice-pinning and the SACRED-FLAW/VOICE-DRIFT audits had no POV data for most books. No LLM
// call. See BackfillPovCli.cs.
if (args.Contains("--backfill-pov"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillPovCli", args);
    return;
}

// prose --backfill-short-name-alias [--universe glmz|scry|...] [--dry-run]
// Registers each multi-word-named character's first name as a CharacterAlias when missing —
// the root cause behind --backfill-entity-presence's low yield (prose refers to characters by
// first name; ScanNames only matches full Name or a registered alias). No LLM call.
if (args.Contains("--backfill-short-name-alias"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillShortNameAliasCli", args);
    return;
}

// CLI mode: migrate legacy Books/Chapters/ChapterBeats/Episodes/EpisodeBeats
// data into the unified Beat/Node schema. Idempotent — safe to re-run.
//   prose --migrate-nodes
if (args.Contains("--migrate-nodes"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MigrateNodesCli", args);
    return;
}

// CLI mode: reconcile audio bytes between local disk and Azure Blob storage.
// Companion to DualWriteAudioStore — repairs drift from offline recordings
// and failed background uploads. Default (no --push/--pull args) is full
// bidirectional repair. See SyncAudioCli class doc for the full arg list.
//   prose --sync-audio [--push] [--pull] [--node SLUG] [--dry-run] [--verbose]
if (args.Contains("--sync-audio"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SyncAudioCli", args);
    return;
}

// CLI mode: (re)narrate an EXISTING node by id (full or prefix) or slug.
// Runs the same NarrateAsync path the Record button uses. Use to re-record a
// node whose beats failed (e.g. a TTS 400) without regenerating prose.
//   prose --narrate-book (--id <guid|prefix> | --slug <slug>)
if (args.Contains("--narrate-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("NarrateNodeCli", args);
    return;
}

// CLI mode: create a fixed, named reviewer panel of N personas, disjoint from
// every existing focus group (no persona on two panels). No LLM calls.
//   prose --make-group --name "Group B" [--size 128]
if (args.Contains("--make-group"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MakeGroupCli", args);
    return;
}

// CLI mode: run Legion persona quality voting across canon entity repos.
// Replaces the old LlmVoting (10 GLMZ residents) with the full 1000-persona library,
// 1-100 scale, and append-only EntityReview rows (same process as node reviews).
//   prose --review-entity [--type <type>] [--ballots N] [--prose N] [--unrated]
if (args.Contains("--review-entity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReviewEntityCli", args);
    return;
}

//   prose --link-weapon-ammo [--local-url URL] [--local-key KEY] [--local-model TAG] [--dry-run]
if (args.Contains("--link-weapon-ammo"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LinkWeaponAmmoCli", args);
    return;
}

//   prose --populate-queue --entity-review|--story-review|--beat-write|--status [options]
if (args.Contains("--populate-queue"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PopulateQueueCli", args);
    return;
}

//   prose --worker-mode --queue-url URL --worker-key KEY --worker-id ID --local-url LLM_URL [options]
if (args.Contains("--worker-mode"))
{
    var sp = BuildCoreServices(args);
    Environment.ExitCode = await Prose.Cli.WorkerModeCli.RunAsync(args, sp);
    return;
}

// CLI mode: have N Legion personas each read an EXISTING node and write an
// honest, scored reader review (saved to NodeReviews), then synthesize the
// Amazon-style aggregate summary. Round-robins reviewers across the trusted-4.
// --review-book/--run-panel literal aliases retired 2026-08-30 — one canonical name only.
//   prose --review-node (--id <guid|prefix> | --slug <slug>) [--readers N]
if (args.Contains("--review-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("ReviewNodeCli", "--review-node", args);
    return;
}

// CLI mode: manage the rented vast.ai review box (key from the MindAttic vault, provider 'vast').
//   prose --gpu <status|stop|start|destroy> [--instance <id>]
if (args.Contains("--gpu"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("VastGpuCli", args);
    return;
}

// CLI mode: manage the rented RunPod review pod (key from the MindAttic vault, provider 'runpod').
//   prose --runpod <status|stop|start|terminate> [--pod <id>]
if (args.Contains("--runpod"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RunPodGpuCli", args);
    return;
}

// CLI mode: (re)generate the portable per-voter report (JSON + filterable HTM) from
// a node's most recent stored review batch, without re-running the panel.
//   prose --review-report (--slug <slug> | --id <guid> | --code <CODE>) [--provider local|cloud|all]
if (args.Contains("--review-report"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReviewReportCli", args);
    return;
}

// CLI mode: add an author ruling to the prose-lessons memory store.
// Lessons are injected into review ballot prompts so reviewers don't penalise
// beats the author has already ruled are doing their job.
//   prose --lesson-add --scope <scope> --kind <kind> --text "<text>"
//   Scope: global | node:<slug> | beat:<guid>
//   Kind:  score-vs-function | delight | voice | pacing | continuity | other
if (args.Contains("--lesson-add"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProseLessonCli", args, method: "RunAddAsync");
    return;
}

// CLI mode: list prose lessons (all scopes or filtered).
//   prose --lessons-list [--scope <scope>]
if (args.Contains("--lessons-list"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProseLessonCli", args, method: "RunListAsync");
    return;
}

// CLI mode: review-driven auto-editor. Weight the latest reviews, target the
// lowest / most-flagged beats (raise the floor), and emit conservative
// before/after rewrite PROPOSALS (JSON) for an approval survey. Nothing is written.
//   prose --edit-book (--id <guid|prefix> | --slug <slug>) [--top N]
if (args.Contains("--edit-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EditNodeCli", args);
    return;
}

// CLI mode: stitch an existing node's beats into one combined file (WAV →
// MP3), copy it to the publish output dir (Downloads by default), and record
// the publication run + process-event ledger. Headless Publish button.
//   prose --publish-book (--id <guid|prefix> | --slug <slug>)
if (args.Contains("--publish-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PublishNodeCli", args);
    return;
}

// CLI mode: set Amazon KDP backend keywords for one node (no generic default).
//   prose --seed-keywords --slug <slug> --keywords "phrase one|phrase two|..."
if (args.Contains("--seed-keywords"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SeedKeywordsCli", args);
    return;
}

// CLI mode: three-altitudes agreement audit (designed story vs told story).
//   prose --altitude-audit (--slug <slug> | --all) [--force-synopsis]
if (args.Contains("--altitude-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AltitudeAuditCli", args);
    return;
}

// CLI mode: chapter-by-chapter synopsis export (also runs inside --export-node).
//   prose --export-synopsis (--slug <slug> | --all) [--force]
if (args.Contains("--export-synopsis"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExportSynopsisCli", args);
    return;
}

// CLI mode: render a node to .docx + .epub + .pdf + .txt + metadata artifacts
// (description.txt, story-synopsis.txt, <CODE>-dcm-viz.htm). Local file
// rendering only — no KDP API integration, hence "export" not "publish".
//   prose --export-node (--id <guid|prefix> | --slug <slug>) [--author "Name"]
if (args.Contains("--export-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExportNodeCli", args);
    return;
}

// --prune-disabled removed 2026-08-30 — PruneDisabledCli had been a retired no-op stub for
// some time already (BeatNode.IsEnabled no longer exists; disabled beats are hard-deleted for
// real at removal time, so this command always found zero candidates). The stub existed only
// so an old script calling it got an explanation instead of silence; the explanation is this
// comment. Handler class preserved in git history if ever needed again.

// CLI mode: build an Audible AI-narration hand-off package for a node.
// Produces a narration-clean manuscript, pronunciation guide, and README.
//   prose --prepare-audible (--slug <slug> | --id <guid|prefix>) [--no-phonetics]
if (args.Contains("--prepare-audible"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PrepareAudibleCli", args);
    return;
}

// CLI mode: deterministic timeline-consistency check (RFC 0009 §5).
// Detects dead-character-acting and wound-regression violations. No LLM calls.
//   prose --timeline-check (--slug <slug> | --id <guid>)
if (args.Contains("--timeline-check"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("TimelineCheckCli", args);
    return;
}

// CLI mode: set the ParentNodeId on an existing node (move it into a collection).
// X-Ray scene assembly (RFC 0002): print the entity roster + voice context block
// for a beat or raw prose. CLI twin of the MCP tool assemble_scene_context.
//   prose --assemble-scene (--beat <guid> | --text "<prose>") [--budget N]
if (args.Contains("--assemble-scene"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AssembleSceneCli", args);
    return;
}

//   prose --reparent-node (--slug <slug> | --id <id>) (--parent-slug <slug> | --parent-id <id>)
//   prose --reparent-node --slug <slug> --clear   — detach from parent
if (args.Contains("--reparent-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReparentNodeCli", args);
    return;
}


// CLI mode: render the WHOLE node as one continuous audiobook (one TTS pass,
// tiered to ElevenLabs limits — one request, else per-chapter, else split) and
// drop the MP3 in Downloads. The headless twin of the "Export Audio" button.
//   prose --record | --export-audio | --export-mp3 | --publish-audiobook
//      (--id <guid|prefix> | --slug <slug>)
if (args.Contains("--publish-audiobook") || args.Contains("--record") || args.Contains("--export-audio") || args.Contains("--export-mp3"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PublishAudiobookCli", args);
    return;
}

// CLI mode: codify the GLMZ house voice + world rules from the memory rubric into
// the DB stores the generator reads (literary_rules / tone_bible). De-fragilizes
// the rules so they no longer depend on an .md file being parsed. Idempotent.
//   prose --seed-voice-rules
if (args.Contains("--seed-voice-rules"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SeedVoiceRulesCli", args);
    return;
}

// CLI mode: extract a time / elapsed-duration timeline from all beats in a node.
// Flags clock anchors, infers story-relative timestamps, and surfaces conflicts.
//   prose --timeline (--slug <slug> | --id <id>)
if (args.Contains("--timeline"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("TimelineCli", args);
    return;
}

// CLI mode: print beat text WITH its authoritative POV character attached (sourced fresh
// from BeatEntityPresence every call, never inferred from prose content). Use this instead
// of raw sqlcmd/SELECT Text reads whenever a conclusion about character voice, attribution,
// or continuity will be drawn from what's read — see ReadBeatsCli's own doc comment for the
// live mistake (2026-08-10, VIGL multi-POV misattribution) this exists to make structurally
// harder to repeat.
// Reads in true reading order, so it is also the sanctioned bulk-read for audit/logic-sweep
// work — no --publish-md export round-trip required.
//   prose --read-beats (--slug <slug> | --id <guid>) [--from N] [--to N] [--numbers <csv>]
//                      [--format text|json]
if (args.Contains("--read-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReadBeatsCli", args);
    return;
}

// CLI mode: per-entity-type reachability matrix (how much canon is embedded and
// thus pullable into prose). The standing gap-finder.
//   prose --coverage
if (args.Contains("--coverage"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CoverageCli", args);
    return;
}

// CLI mode: (re)build the materialized character read-model projection from the
// relational source of truth. Run after a bulk import / relational migration,
// or whenever ReadModelVersion is bumped. Backfills missing/stale rows, prunes
// orphans. The steady-state path self-heals, so this is a one-time / maintenance op.
// Any character whose relational read is poorer than its current cache is SKIPPED
// and named in the output, not silently overwritten — pass --force only once you've
// reviewed that list (see the 2026-09-05 incident note on RebuildReadModelCli).
//   prose --rebuild-readmodel [--archived] [--force]
if (args.Contains("--rebuild-readmodel"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RebuildReadModelCli", args);
    return;
}

// CLI mode: read-only survey — which characters have zero rows across every relational
// depth bridge (PsychologyTraits/StatScalars/ArchetypeScores/StoryHooks/SpeechPhrases),
// cross-referenced against BeatEntityMentions to separate "actually used in prose" from
// inert stub rows. Built to size the blast radius of a 2026-09-05 incident where
// --rebuild-readmodel overwrote a character's cached read-model (which held rich content)
// with an empty relational projection. Writes nothing.
//   prose --character-depth-audit --universe <slug> [--json]
if (args.Contains("--character-depth-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CharacterDepthAuditCli", args);
    return;
}

// CLI mode: create a runtime-defined repository (custom entity type).
//   prose --create-repository --name "Artifacts" [--category World] [--icon bi-box] [--description "..."]
if (args.Contains("--create-repository"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CreateRepositoryCli", args);
    return;
}

// Table-driven: each --rebuild-*-relational flag maps to its CLI handler class name, forwarded
// generically to the Hub. ADDITIVE — Records.Json is never modified. (RFC 0007)
{
    var rebuildRelational = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["--rebuild-faction-relational"]        = "RebuildFactionRelationalCli",
        ["--rebuild-quote-relational"]          = "RebuildQuoteRelationalCli",
        ["--rebuild-news-relational"]           = "RebuildNewsRelationalCli",
        ["--rebuild-contract-relational"]       = "RebuildContractRelationalCli",
        ["--rebuild-vocabulary-relational"]     = "RebuildVocabularyRelationalCli",
        ["--rebuild-archetype-relational"]      = "RebuildArchetypeRelationalCli",
        ["--rebuild-genemod-relational"]        = "RebuildGenemodRelationalCli",
        ["--rebuild-material-relational"]       = "RebuildMaterialRelationalCli",
        ["--rebuild-psionic-relational"]        = "RebuildPsionicRelationalCli",
        ["--rebuild-motif-relational"]          = "RebuildMotifRelationalCli",
        ["--rebuild-lab-specimen-relational"]   = "RebuildLabSpecimenRelationalCli",
        ["--rebuild-flyover-entity-relational"] = "RebuildFlyoverEntityRelationalCli",
        ["--rebuild-automaton-relational"]      = "RebuildAutomatonRelationalCli",
        ["--rebuild-ammunition-relational"]     = "RebuildAmmunitionRelationalCli",
        ["--rebuild-transportation-relational"] = "RebuildTransportationRelationalCli",
        ["--rebuild-corponation-relational"]    = "RebuildCorponationRelationalCli",
        ["--rebuild-equipment-relational"]      = "RebuildEquipmentRelationalCli",
        ["--rebuild-technology-relational"]     = "RebuildTechnologyRelationalCli",
        ["--rebuild-pharmaceutical-relational"] = "RebuildPharmaceuticalRelationalCli",
        ["--rebuild-cyberware-relational"]      = "RebuildCyberwareRelationalCli",
        ["--rebuild-consumer-good-relational"]  = "RebuildConsumerGoodRelationalCli",
        ["--rebuild-synthetic-relational"]      = "RebuildSyntheticRelationalCli",
        ["--rebuild-place-relational"]          = "RebuildPlaceRelationalCli",
        ["--rebuild-document-relational"]       = "RebuildDocumentRelationalCli",
        ["--rebuild-entertainment-relational"]  = "RebuildEntertainmentRelationalCli",
        ["--rebuild-weapon-relational"]         = "RebuildWeaponRelationalCli",
        ["--rebuild-apparel-relational"]        = "RebuildApparelRelationalCli",
        ["--rebuild-subsidiary-relational"]     = "RebuildSubsidiaryRelationalCli",
    };
    if (Array.Find(args, a => rebuildRelational.ContainsKey(a)) is { } rebuildVerb)
    {
        Environment.ExitCode = await HubCliClient.ForwardAsync(rebuildRelational[rebuildVerb], args);
        return;
    }
}

// CLI mode: materialize relational rows for active characters that are blob-only
// (no Characters row) — the no-data-loss gate before dropping the Character blob. (RFC 0007)
//   prose --backfill-missing-characters
if (args.Contains("--backfill-missing-characters"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillMissingCharactersCli", args);
    return;
}


// CLI mode: RFC 0007 unified blob-retirement gate — backfill all 29 relational types
// from Records.Json, validate, and delete the blobs in a single pass. (RFC 0007)
//   prose --retire-records-blobs [--rebuild] [--validate] [--apply]
if (args.Contains("--retire-records-blobs"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RetireRecordsBlobsCli", args);
    return;
}

// CLI mode: split a monolithic node into a Collection (parent + chapter
// child nodes) at IsChapterStart boundaries. Backs up to markdown first.
//   prose --split-collection (--slug <s> | --id <guid>)
if (args.Contains("--split-collection"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SplitCollectionCli", args);
    return;
}

// CLI mode: print the voice context the generator/re-beater receive — the
// verification that the canon-trained voice is wired into prompts.
//   prose --print-voice
if (args.Contains("--print-voice"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PrintVoiceCli", args);
    return;
}

// CLI mode: print all beats of a node as continuous prose to stdout.
// No headers, no beat numbers, no metadata — just the prose, beats separated by blank lines.
//   prose --sanitize-beats [--slug <slug> | --all] [--dry-run]
if (args.Contains("--sanitize-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SanitizeBeatsCli", args);
    return;
}

//   prose --print-book (--id <guid|prefix> | --slug <slug>)
if (args.Contains("--print-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PrintNodeCli", args);
    return;
}

// CLI mode: rebuild a node's beats to the codified beat doctrine via LLM
// re-segmentation (story beats + dialogue/'?' mechanics + gaps). Dry-run by
// default; --apply backs up to markdown then replaces beats if the word-retention
// guard passes. --all targets every doctrine-violating node.
//   prose --rebeat-book (--slug <s> | --id <guid> | --all) [--apply]
if (args.Contains("--rebeat-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RebeatNodeCli", args);
    return;
}

// CLI mode: sweep a node's prose against canon (all entity types) and queue
// contradictions as approval-gated findings — the self-correction pass.
//   prose --check-canon (--slug <s> | --id <guid> | --all)
if (args.Contains("--check-canon"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CheckCanonCli", args);
    return;
}

// CLI mode: show what the universal canon reach pulls for a query, across ALL
// entity types — verifies the full-interconnect retrieval path.
//   prose --canon-retrieve "<query>" [--k N] [--types t1,t2]
if (args.Contains("--canon-retrieve"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CanonRetrieveCli", args);
    return;
}

// CLI mode: author-only Canon trust gate — mark a node strong enough to draw
// conclusions about its characters/events (the voice-harvest learns from canon).
//   prose --mark-canon (--slug <s> | --id <guid>) [--off]
if (args.Contains("--mark-canon"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MarkCanonCli", args);
    return;
}

// CLI mode: distill voice rules from winning (≥80%) nodes into the codified
// DB-backed rules the generator reads. Propose-then-approve.
//   prose --harvest-voice (--slug <s> | --id <id> | --all-80 | --pending | --apply <guid> | --reject <guid>) [--force]
if (args.Contains("--harvest-voice"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("HarvestVoiceCli", "--harvest-voice", args);
    return;
}

// CLI mode: list every node as a table (or JSON). Headless twin of /nodes.
//   prose --list-books [--status <s>] [--kind <k>] [--search <text>] [--limit <n>] [--json]
if (args.Contains("--list-books"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ListNodesCli", args);
    return;
}

//   prose --kdp-status
//   Show KDP publication status: Published / Outdated / WorkInProgress for all tracked nodes.
//   Outdated = published but beats edited since last KDP push.
if (args.Contains("--kdp-status"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("KdpStatusCli", args);
    return;
}

//   prose --kdp-manifest [--out <path>] [--userscript]
//   Reconciles DB + disk + tools/kdp/title-ids.json into tools/kdp/manifest.json (the ground
//   truth for what needs to go up on KDP). --userscript also regenerates
//   tools/kdp/kdp-panel.user.js from tools/kdp/kdp-panel.template.js.
if (args.Contains("--kdp-manifest"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("KdpManifestCli", args);
    return;
}

//   prose --kdp-mark-published --slug <slug> [--url <amazonUrl>] [--title-id <id>]
//   Closes the loop after a republish actually completes on KDP.
if (args.Contains("--kdp-mark-published"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("KdpMarkPublishedCli", args);
    return;
}

// CLI mode: render a node to Markdown or PDF in Downloads.
// Markdown output embeds <!-- beat:N:id7 --> markers for prose --import-md round-trip.
//   ss (--publish-md | --publish-pdf) (--id <guid|prefix> | --slug <slug>) [--author "Name"]
if (args.Contains("--publish-md") || args.Contains("--publish-pdf"))
{
    var format = args.Contains("--publish-md") ? PublishManuscriptCli.Format.Markdown
               : PublishManuscriptCli.Format.Pdf;
    Environment.ExitCode = await HubCliClient.ForwardAsync("PublishManuscriptCli", args, extraParamValue: format.ToString());
    return;
}

// CLI mode: reimport an edited --publish-md Markdown file back into the DB. Each
// <!-- beat:N:id7 --> marker identifies the beat; prose between markers updates Beat.Text.
//   prose --import-md --file path.md [--dry-run]
if (args.Contains("--import-md"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ImportMarkdownCli", args);
    return;
}

// CLI mode: bounded copy-edit of a node — proper paragraph/dialogue spacing, a
// "?" on questions that lack one, and "asks"/"asked" (not "says") on question
// dialogue. Dry-run by default; --apply commits. Beats edited beyond those bounds
// are rejected (word-token guard) and left untouched.
//   prose --reflow-book (--id <guid|prefix> | --slug <slug>) [--apply]
if (args.Contains("--reflow-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReflowNodeCli", args);
    return;
}

// CLI mode: deep-duplicate a node (and its sub-node tree) into a fresh,
// independent copy — every beat cloned to a new row (prose + metadata kept;
// audio/score/stale reset). Editing the copy never touches the original.
//   prose --duplicate-book (--id <guid|prefix> | --slug <slug>) --title "New Title"
if (args.Contains("--duplicate-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DuplicateNodeCli", args);
    return;
}

// CLI mode: import a hand-authored .node file (beat + gap + beat …) into a
// fresh node. The complement to --write-story (LLM-generated): this is for
// drafts written elsewhere (chat exports, transcripts, paper notes typed up).
// See ImportNodeCli class doc for the file format.
//   prose --import-book --file path.node [--title ...] [--kind ...] [--slug ...] [--parent ...] [--dry-run]
if (args.Contains("--import-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ImportNodeCli", args);
    return;
}

// CLI mode: replace an EXISTING node's beats wholesale from a .node file.
// The other half of the export/edit/reimport loop for edits that no longer
// line up with the old beat boundaries (import-md patches beats in place by
// ID; this swaps the whole set). Old beats are disabled, never deleted.
// See ReimportNodeCli class doc for details and the safety checks.
//   prose --reimport-node (--id ... | --slug ...) --file path.node [--dry-run] [--force]
if (args.Contains("--reimport-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReimportNodeCli", args);
    return;
}

// CLI mode: snapshot a book's entire current live prose into ArchivedBooks — a
// pre-edit backup, read-only against the live content. See ArchiveBookCli class doc.
//   prose --archive-book (--id ... | --slug ...) [--reason "..."] --universe <u>
if (args.Contains("--archive-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ArchiveBookCli", args);
    return;
}

// CLI mode: automated inventory of every service/DI-registration/CLI-verb/MCP-tool/script in
// the tree, plus name-overlap clusters worth a second look. See ArchitectureScanCli class doc.
//   prose --architecture-scan [--json] [--out <file>] [--top <n>] [--force]
if (args.Contains("--architecture-scan"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ArchitectureScanCli", args);
    return;
}

// CLI mode: list every ArchivedBook snapshot for a node, newest first — read-only, use to find
// the archive-id for --restore-node-field. See ListArchivesCli class doc.
//   prose --list-archives (--id ... | --slug ...) --universe <u>
if (args.Contains("--list-archives"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ListArchivesCli", args);
    return;
}

// CLI mode: restore a Node content field (Description/NodeOutline/Summary/Seed/Subtitle) from a
// named ArchivedBook snapshot back onto the live node. Explicit archive-id, never "latest" —
// see RestoreNodeFieldCli class doc.
//   prose --restore-node-field (--id ... | --slug ...) --archive-id <guid>
//       --field description|nodeoutline|summary|seed|subtitle|all --universe <u>
if (args.Contains("--restore-node-field"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RestoreNodeFieldCli", args);
    return;
}

// CLI mode: restore a hard-deleted Entities row from Entities_History (system-versioned
// temporal table) — see RestoreEntityCli class doc.
//   prose --restore-entity --id <guid> --as-of <datetime-utc> [--dry-run]
if (args.Contains("--restore-entity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RestoreEntityCli", args);
    return;
}

// CLI mode: recover Beats.Text from Beats_History (system-versioned temporal table) after a bad
// overwrite — see RestoreBeatTextCli class doc.
//   prose --restore-beat-text --id <beatGuid> --as-of <datetime-utc> [--dry-run]
if (args.Contains("--restore-beat-text"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RestoreBeatTextCli", args);
    return;
}

// CLI mode: rename a Universe row in place (Slug/Name/Theme only) — a seamless cutover, every
// Node/Entity/Book already scoped to its Id keeps working unmodified. See RenameUniverseCli.
//   prose --rename-universe --slug <oldSlug> --new-slug <newSlug> --new-name <newName> [--new-theme <newTheme>]
if (args.Contains("--rename-universe"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RenameUniverseCli", args);
    return;
}

// CLI mode: create a new Universe row. See CreateUniverseCli.
//   prose --create-universe --slug <slug> --name <name> [--theme <theme>] [--description <text>]
if (args.Contains("--create-universe"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CreateUniverseCli", args);
    return;
}

// CLI mode: relocate a book node (and its full descendant chapter subtree) into a different
// universe. See MoveNodeUniverseCli.
//   prose --move-node-universe (--slug <slug> | --id <id>) --to-universe <universeSlug>
if (args.Contains("--move-node-universe"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MoveNodeUniverseCli", args);
    return;
}

// CLI mode: one-off cleanup of a generation-artifact heading/marker leaking into Beats.Text —
// see StripBeatArtifactsCli class doc.
//   prose --strip-beat-artifacts --slug <slug> [--dry-run]
if (args.Contains("--strip-beat-artifacts"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("StripBeatArtifactsCli", args);
    return;
}

// CLI mode: set Node.Author — the pen name exports fall through to instead of "MindAttic".
// See SetNodeAuthorCli class doc.
//   prose --set-node-author --slug <slug|code|guid> --author "<Name>"
if (args.Contains("--set-node-author"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetNodeAuthorCli", args);
    return;
}

// CLI mode: the nightly AutoCorrect pass — pure ML/deterministic, zero LLM calls. Invoked by
// the Windows Task Scheduler task registered by scripts/register-autocorrect-task.ps1 at 2:00 AM
// Central every night. See AutoCorrectOrchestratorService for the pipeline.
//   prose --auto-correct-nightly [--universe <slug>] [--dry-run] [--json]
if (args.Contains("--auto-correct-nightly"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AutoCorrectNightlyCli", args);
    return;
}

// CLI mode: rewind a nightly AutoCorrect run (or the last N logged actions) via the undo ledger.
//   prose --auto-correct-undo (--run-id <guid> | --last-n <N>)
if (args.Contains("--auto-correct-undo"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AutoCorrectUndoCli", args, method: "RunUndoAsync");
    return;
}

// CLI mode: list recent AutoCorrect runs and their undo state.
//   prose --auto-correct-status [--list-runs]
if (args.Contains("--auto-correct-status"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AutoCorrectUndoCli", args, method: "RunStatusAsync");
    return;
}

// CLI mode: import a local image file (png, jpg, webp) into the Media table.
// Optionally links to a node by --book-code and sets the media type.
//   prose --import-cover --file PATH [--book-code CODE] [--type TYPE] [--notes TEXT] [--dry-run]
if (args.Contains("--import-cover"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ImportCoverImageCli", args);
    return;
}


// CLI mode: burst oversized beats (e.g. chapter-as-one-beat from old book
// imports) into paragraph-sized pieces. Idempotent — already-small beats
// are skipped on rerun.
//   prose --burst-beats [--min-chars 800] [--node slug] [--kind book] [--dry-run]
if (args.Contains("--burst-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BurstBeatsCli", args);
    return;
}

// CLI mode: report flat-vs-bridge drift for a denormalised column.
//   prose --audit-denorm Entities.TagsJson
//   prose --audit-denorm Characters.Affiliation
if (args.Contains("--audit-denorm"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AuditDenormCli", args);
    return;
}

// CLI mode: findings inbox — list / show / apply / dismiss / scan.
if (args.Contains("--findings"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FindingsCli", args);
    return;
}

// prose --fact-ledger-refresh --slug <slug-or-code> — zero-LLM-cost re-run of just the
// fact-ledger check (see FactLedgerRefreshCli's own doc comment). Not cost-gated: it is the
// deliberate cheap alternative to the cost-gated --audit-book --deep bundle.
if (args.Contains("--fact-ledger-refresh"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FactLedgerRefreshCli", args);
    return;
}

// prose --orphan-beats [--min-number N] [--max-number N] [--limit N] [--contains "text"] —
// read-only diagnostic: Beats rows with no BeatNodes membership. See OrphanBeatsCli's own doc
// comment for why this exists (VIGL fact-ledger investigation, 2026-09-01).
if (args.Contains("--orphan-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("OrphanBeatsCli", args);
    return;
}

// prose --entity-tree (--id <guid> | --slug <slug>) [--depth N] [--rel-types type1,type2] [--as-of date]
if (args.Contains("--entity-tree"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EntityTreeCli", args);
    return;
}

// prose --prose-check (--slug <nodeSlug> | --id <beatId>) [--all] [--json]
if (args.Contains("--prose-check"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProseCheckCli", args);
    return;
}

// prose --compute-metrics [--slug <slug> | --all]
// CPU-only per-beat prose quality metrics: word count, sentence count, TTR,
// Flesch-Kincaid readability, dialogue proportion.
// Upserts into BeatProseMetrics. Safe to re-run nightly. Exit 0 = success.
if (args.Contains("--compute-metrics"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatProseMetricsCli", args);
    return;
}

// prose --beat-granularity [--slug <slug> | --code <code> | --all] [--beats]
// Analyses beat-size distribution against the 4,000–7,500 char optimal range.
// Labels each beat as OK / SPLIT / MERGE and prints per-story stats.
// CPU-only — no LLM calls. Exit 0 = success.
if (args.Contains("--beat-granularity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatGranularityCli", args);
    return;
}

// prose --cross-book-consistency-audit [--since <hours>]
// Renamed from --consistency-audit (2026-08-30) — collided by word order with the unrelated
// --audit-consistency (DataConsistencyCli's SSOT-drift audit).
// Surfaces factual contradictions that span multiple story nodes by querying
// the existing ContinuityClaims table. CPU-only — no LLM calls.
// Exit 0 = clean, 1 = conflicts found.
if (args.Contains("--cross-book-consistency-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CrossBookConsistencyAuditCli", args);
    return;
}

// prose --morning-report [--since <hours>]
// Aggregates overnight findings: cross-story contradictions, new Findings,
// prose metrics outliers, near-duplicate alerts, score correlation, leaderboard.
// Writes HTML to PublishExportDirectory. Default window: 24h. Exit 0 always.
if (args.Contains("--morning-report"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MorningReportCli", args);
    return;
}

// prose --prose-health [--slug <nodeSlug>] [--json] [--out <dir>]
// Zero-cost overnight health scan: surface stats + kNN score prediction +
// semantic outlier detection using cached ProseEmbeddings. No API calls.
if (args.Contains("--prose-health"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProseHealthCli", args);
    return;
}

// prose --check-fidelity (--slug <nodeSlug> | --id <nodeId>) [--json]
// Detects the Semantic Fidelity Gap — beats scoring high but drifting from the
// story's original meaning (Goodhart's Law in prose). Two checks:
//   Bible alignment: prose vs Seed/Description (north-star drift)
//   Intent alignment: prose vs beat Description (purpose drift)
// Files SEMANTIC-DRIFT findings; also runs automatically after every review.
if (args.Contains("--check-fidelity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CheckFidelityCli", args);
    return;
}

// prose --world-state --beat <beatId> [--story-time "date"] [--json]
if (args.Contains("--world-state"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("WorldStateCli", args);
    return;
}

// prose --sequential-read-status --slug <slug> | --all [--json]
// prose --sequential-read-record --slug <slug> --read-by <name> [--stages N] [--summary "text"]
if (args.Contains("--sequential-read-status") || args.Contains("--sequential-read-record"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SequentialReadCli", args);
    return;
}

// prose --check-text-integrity [--fix] [--json]
if (args.Contains("--check-text-integrity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("TextIntegrityCli", args);
    return;
}

// prose --gear-check --slug <nodeSlug> --character <characterId> [--story-time date]
if (args.Contains("--gear-check"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GearCheckCli", args);
    return;
}

// prose --write-synopsis --slug <nodeSlug> [--json]
// Generates a beat-by-beat narrative synopsis (act-grouped, one sentence per beat) FROM
// the written prose. For a logic check, use --logic-sweep instead.
if (args.Contains("--write-synopsis"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("WriteSynopsisCli", args);
    return;
}

// prose --logic-sweep --slug <nodeSlug> [--json]
// Codifies docs/LOGIC.md's six-dimension sweep (SS-A44) as one LLM call per dimension:
// causality chain, knowledge states, timeline, plant/payoff (two-way), orphan references,
// bible agreement. A single-pass approximation over the whole node's prose — for a large
// book or a thorough pass, prefer the /logic-sweep Claude Code skill (range-scoped
// subagents + quote verification + fix + re-verify). Findings persist to Findings and
// auto-heal on re-run. Exit 0 = clean, 1 = MODERATE/MINOR only, 2 = any BLOCKER.
if (args.Contains("--logic-sweep"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LogicSweepCli", args);
    return;
}

// prose --dcm-backfill --slug <slug> [--dry-run]
// Retroactive DCM footprint for books written OUTSIDE the engine (update_beat_text /
// --edit-beat / --import-md bypass ProseWriterRouter, so step-0 entity inference never
// ran — PURSUED shipped 127 beats with zero entity docs this way). Runs
// EntityDocService.InferFromTextAsync over every enabled beat's prose; hash-gated,
// no prose touched. Run after --generate-node-doc + --sync-markdown.
if (args.Contains("--dcm-backfill"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DcmBackfillCli", args);
    return;
}

// prose --reader-qa (--slug <slug> | --all) [--force] [--json]
// Reader-Proxy QA (docs/READER-QA.md) — the default reader-facing quality instrument.
// Phase 1: comprehension probes — a cheap model reads each chapter cold, diffed against
// the Sonnet synopsis ground truth, Sonnet-arbitrated, filed as ComprehensionDefect
// findings. NO scores (measurement, not vote — SS-A44 exempt). Hash-cached per chapter.
// Exit 0 = clean, 1 = defects found, 2 = error.
if (args.Contains("--reader-qa"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReaderQaCli", args);
    return;
}

// prose --craft-checklist --slug <slug> [--force] [--json]
// Reader-Proxy QA Instrument 2: binary craft/delight checklist per beat, hash-gated on
// Beat.TextHash + rule-set version (unchanged beats never re-bill). CRAFT §8 DON'Ts +
// "≥1 applicable DELIGHT move" + book-level move-monotony counters (DELIGHT §14).
// Findings persist as CraftChecklist. No scores. Exit 0 = clean, 1 = findings, 2 = error.
if (args.Contains("--craft-checklist"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatChecklistCli", args);
    return;
}

// prose --diagnose-book --slug <nodeSlug> [--json]
// Pre-flight structural analysis before running the review panel.
// Runs 12 targeted checks (antagonist cost, protagonist behavior change,
// exposition density, etc.) and reports Pass/Warn/Fail with evidence + fixes.
// Exit 0 = ready, 1 = warnings, 2 = blocking failures.
if (args.Contains("--diagnose-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DiagnoseNodeCli", args);
    return;
}

// prose --unresolved-nouns (--slug <s> | --all) [--min N] [--limit N] [--json]
// Report-only: capitalized phrases in a book's LIVE BEAT PROSE that resolve to no Entity row.
// Deterministic, no LLM, writes nothing. The residue detector already existed but ran against
// outline text only, so "is every named thing an entity?" had no measurable answer for prose.
if (args.Contains("--unresolved-nouns"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("UnresolvedNounsCli", args);
    return;
}

// prose --check-duplicate-beats --slug <nodeSlug> [--threshold 0.90] [--json]
// Corpus-wide near-duplicate-scene detector over prose embeddings (BeatDuplicateService).
// Candidate generator, not a verdict — verify by reading both beats before acting.
if (args.Contains("--check-duplicate-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CheckDuplicateBeatsCli", args);
    return;
}

// prose --check-temporal-hygiene [--json]
// Enforces (not just documents) the two rules that make re-enabling SYSTEM_VERSIONING on
// Nodes/Beats/BeatNodes safe: no IsEnabled/IsActive-style status-flag column on any versioned
// table, and no application query joins a live table to its own _History shadow. Run after any
// schema change touching a versioned table, not just once.
if (args.Contains("--check-temporal-hygiene"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("TemporalHygieneCli", args);
    return;
}

// prose --examine-emotion --slug <nodeSlug> [--effort draft|standard|deep] [--json]
// Emotional Intelligence Examination (SS-A15): 8-dimension 0–4 rubric, per-beat curve,
// character ledger (Want/Need/Wound/Flaw), register-adaptive anchors.
// Exit 0 = none blocking, 1 = advisory issues, 2 = blocking dimensions open.
if (args.Contains("--examine-emotion"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("ExamineEmotionCli", "--examine-emotion", args);
    return;
}

// Scene Collision engine manual test harness (2026-08-10): runs SceneCollisionService against
// one real beat without a full ProseWriterRouter pass. See SimulateCollisionCli for details.
if (args.Contains("--simulate-collision"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("SimulateCollisionCli", "--simulate-collision", args);
    return;
}

// prose --causality-check / --affect-check / --interpersonal-check --slug <slug> [--json]
// "Behave like people" beat lenses: cause-effect (kill "and then"), emotion→action,
// and verbal+non-verbal interpersonal dynamics (the 90+ relational lever).
if (args.Contains("--causality-check") || args.Contains("--affect-check") || args.Contains("--interpersonal-check"))
{
    var lens = args.Contains("--causality-check") ? "causality"
             : args.Contains("--affect-check") ? "affect" : "interpersonal";
    var cmdLens = $"--{lens}-check";
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("BeatLensCli", cmdLens, args, extraParamValue: lens);
    return;
}

// prose --list-species — print the species taxonomy (canonical name, label, sentience).
if (args.Contains("--list-species"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ListSpeciesCli", args);
    return;
}

// prose --behavior-check --slug <nodeSlug> --character <characterId>
if (args.Contains("--behavior-check"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BehaviorCheckCli", args);
    return;
}

// prose --weapon-network (--id <weaponId> | --character <characterId> [--as-of date])
if (args.Contains("--weapon-network"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("WeaponNetworkCli", args);
    return;
}

// prose --ambient-palette --character <characterId> [--as-of date]
if (args.Contains("--ambient-palette"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("AmbientPaletteCli", args);
    return;
}

// prose --seed-sensory-hints [--list] [--weapon "Name" --hints "hint1; hint2"] [--force]
if (args.Contains("--seed-sensory-hints"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SeedSensoryHintsCli", args);
    return;
}

// prose --beat <subcommand> — fine-grained beat manipulation:
//   insert  --node <slug|id> [--after <beatId>] [--text "..."]
//   delete  --id <beatId> [--node <slug|id>]
//   update  --id <beatId> --text "..."  (use '-' for stdin)
//   meta    --id <beatId> [--title "..."] [--kind "..."] [--description "..."] [--tone "..."] ...
//   show    --id <beatId>
//   list    --node <slug|id>
if (args.Contains("--beat"))
{
    var beatArgs = args.SkipWhile(a => a != "--beat").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatCli", beatArgs);
    return;
}

// prose --delete-node --id <guid>   Hard-delete a node and its BeatNode memberships.
// Beats that are exclusively owned by this node are also deleted.
// HARD RULE: never use raw sqlcmd DELETE on Nodes — use this command instead.
if (args.Contains("--delete-node"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DeleteNodeCli", args);
    return;
}

// prose --wound <subcommand> — character wound ledger:
//   list    --character <id|name> [--as-of "date"]
//   log     --character <id|name> --description "..." [--location "chest"] [--severity moderate] ...
//   status  --wound <id> --status active|healed|noted
if (args.Contains("--wound"))
{
    var woundArgs = args.SkipWhile(a => a != "--wound").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("WoundCli", woundArgs);
    return;
}

// CLI mode: harvest entities + edges from open text (design notes, canon briefs).
// Routed BEFORE the bare --universe command: --universe here is the scope flag, not a subcommand.
//   prose --harvest-entities --file <path> [--universe glmz] [--dry-run]
if (args.Contains("--harvest-entities"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("HarvestEntitiesCli", "--harvest-entities", args);
    return;
}

// prose --universe <subcommand> — universe management:
//   list      Print all universes
//   current   Print the active universe
//   use       --slug <slug> | --id <guid>
// Only hijacks dispatch when --universe is the PRIMARY command (args[0]) AND is followed by a
// real universe subcommand. Elsewhere in argv, --universe <slug> is the scoping flag other
// commands accept (parsed at line 28 into UniverseBootstrap.RequestedSlug) —
// args.Contains("--universe") would incorrectly steal dispatch from every command block defined
// after this one (e.g. --coordinate).
//
// The subcommand check matters because --universe is ALSO valid in first position as a scoping
// flag: `prose --universe source --export-node --slug x` is a legitimate export, not a malformed
// universe command. Matching on args[0] alone swallowed those silently — UniverseCli printed its
// usage text and the real command never ran, which looks like a no-op rather than an error.
// Bare `prose --universe` still lands here (args.Length == 1) so it prints usage instead of
// falling through the whole dispatch chain and exiting silently.
if (isUniverseManagementCommand)
{
    var uniArgs = args.Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("UniverseCli", uniArgs);
    return;
}

// RFC 0007 "Universe Interchange" — import/export between an app's
// <app>/universe/<slug>.universe.json contract file and Prose's Entity spine.
// Each subcommand resolves its own explicit universe (file's own id, or a required
// positional slug) — see UniverseAgnosticCommands below.
if (args.Contains("--universe-import"))
{
    var rest = args.SkipWhile(a => a != "--universe-import").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("UniverseInterchangeCli", rest, method: "RunImportAsync");
    return;
}
if (args.Contains("--universe-export"))
{
    var rest = args.SkipWhile(a => a != "--universe-export").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("UniverseInterchangeCli", rest, method: "RunExportAsync");
    return;
}
if (args.Contains("--universe-sync"))
{
    var rest = args.SkipWhile(a => a != "--universe-sync").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("UniverseInterchangeCli", rest, method: "RunSyncAsync");
    return;
}
// Portable-writing-service plan, Phase 2: write a scene/line of dialog without a pre-existing
// Book/Chapter/Beat row — see OneShotGenerateCli's own doc comment.
if (args.Contains("--generate-scene"))
{
    var rest = args.SkipWhile(a => a != "--generate-scene").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("OneShotGenerateCli", rest);
    return;
}
// Portable-writing-service plan, Phase 4: narrow dialog-beat filter/export — see
// BarksExportCli's own doc comment.
if (args.Contains("--barks-export"))
{
    var rest = args.SkipWhile(a => a != "--barks-export").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("BarksExportCli", rest);
    return;
}

// prose --review-settings [--set <key> <value>] — view or update review voting settings.
// Keys: ballots, prose, panel, readers, max-concurrency, judge-provider, allowed-providers
if (args.Contains("--review-settings"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ReviewSettingsCli", args);
    return;
}

// prose --get <type> <name-or-id> — targeted entity lookup.
// Types: character | place | weapon | faction | corponation
if (args.Contains("--get"))
{
    var getArgs = args.SkipWhile(a => a != "--get").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("GetEntityCli", getArgs);
    return;
}

// CLI mode: sync project-rule, Codex, and Claude Code memory .md files to DB.
// Upserts by RelativePath; only changed files (hash diff) produce a history row.
//   prose --sync-markdown [--dry-run]
if (args.Contains("--sync-markdown"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SyncMarkdownCli", args);
    return;
}


// CLI mode: restore .md files from DB back to disk. Supports point-in-time
// recovery from the MarkdownFiles_History temporal table.
//   prose --restore-markdown [--file <relativePath>] [--as-of <datetime-utc>] [--dry-run] [--list]
if (args.Contains("--restore-markdown"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RestoreMarkdownCli", args);
    return;
}

// CLI mode: keyword recall — call up (print) or create (--to-disk) the select few
// tracked .md files relevant to a topic, straight from the DB.
//   prose --recall <keyword> [--content] [--to-disk] [--as-of <datetime-utc>]
if (args.Contains("--recall"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RecallMarkdownCli", args);
    return;
}

// CLI mode: Doc Context Stack dry-run — print the rotating cast of .md docs that WOULD
// load for a node + optional scene text (tier, reason, score, budget). Read-only.
//   prose --doc-context --slug <node> [--goal "<text>"] [--budget <tokens>]
// CLI mode: manage user context overrides for the DocContextStack.
//   prose --context add     --doc <path|guid> [--node <slug>]   Pin doc into prompts
//   prose --context exclude --doc <path|guid> [--node <slug>]   Exclude doc
//   prose --context remove  --doc <path|guid> [--node <slug>]   Remove override
//   prose --context clear   [--node <slug>]                     Clear all overrides
//   prose --context status                                       Show active overrides
if (args.Contains("--context"))
{
    var ctxArgs = args.SkipWhile(a => a != "--context").Skip(1).ToArray();
    Environment.ExitCode = await HubCliClient.ForwardAsync("ContextCli", ctxArgs);
    return;
}

// prose --liberty-report [--beat <guid> | --slug <slug>]
// Show liberty analysis + Rule of Cool findings for a beat or all beats in a story.
if (args.Contains("--liberty-report"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LibertyReportCli", args);
    return;
}

// prose --log-decision --summary "..." [--rationale "..."] [--category ...] [--related id,id]
// Durable "why" record — the Decision Ledger half of the Command/Decision Ledger pair
// (CommandLedgerEntries is written automatically by every dispatch; this is explicit).
if (args.Contains("--log-decision"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LogDecisionCli", args);
    return;
}

// prose --command-log [--since <dt>] [--handler <name>] [--take N] [--json]
// Read back the Command Ledger — every CLI/MCP/cost-gated call Prose.Hub has executed.
if (args.Contains("--command-log"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CommandLogCli", args);
    return;
}

// prose --decision-log [--since <dt>] [--session <id>] [--take N] [--json]
// Read back the Decision Ledger written by --log-decision.
if (args.Contains("--decision-log"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DecisionLogCli", args);
    return;
}

// prose --log-search [--since <dt>] [--severity <lvl>] [--text <q>] [--take N] [--json]
// Durable, searchable log history (Serilog daily files) — not the live in-memory tail.
if (args.Contains("--log-search"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LogSearchCli", args);
    return;
}

// prose --beat-archive --beat-id <guid>
// The Beat Context Archive (observability Part F5): everything that fed one beat, resolved
// as-of that beat's own BeatContextTrace timestamp — prose, per-service trace, full LLM
// prompt/response, entity roster resolved to that moment's canon, DCM doc content as of that
// moment, and the bible section active at that time.
if (args.Contains("--beat-archive"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatArchiveCli", args);
    return;
}

// prose --browse-repository [--type <name>] [--search <text>] [--page N] [--format text|json]
// Browse entities by repository/type (built-in or custom) — no hand-written SQL required.
if (args.Contains("--browse-repository"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BrowseRepositoryCli", args);
    return;
}

if (args.Contains("--doc-context-hook"))
{
    // UserPromptSubmit hook backend — stdout must contain ONLY the hook JSON. The Hub
    // captures stdout/stderr separately and this process only ever writes the JSON it gets
    // back, so no NoLogging DI variant is needed on this side any more.
    Environment.ExitCode = await HubCliClient.ForwardAsync("DocContextHookCli", args);
    return;
}

// REMOVED 2026-06-24: `--refactor-telemetry` (bulk regenerate-beats-from-synopsis runner).
// It rebuilt finished beats from their one-line goals, discarding hand-crafted prose — proven
// to regress finished nodes (dual-read: surgical 80.8 > baseline 79.7 > regen 76.2). Doc/Entity
// context were validated separately and are KEPT; regen-from-synopsis is not a revision tool and
// is gone. New-beat generation lives in ProseWriterRouter.WriteAsync, untouched.

// CLI mode: dual-read comparative review — the SAME pinned panel grades both versions of a story;
// pairs scores per reader (within-reader delta cancels taste bias) → keep/revert/merge verdict.
//   prose --dual-read --old <slug|id> --new <slug|id> [--panel <name>] [--readers N]
if (args.Contains("--dual-read"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DualReadCli", args);
    return;
}

if (args.Contains("--doc-context"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DocContextCli", args);
    return;
}

// CLI mode: DCM lifecycle visualization — dry-run context pass + Gantt .htm export.
//   prose --dcm-viz --slug <slug> [--out <dir>]
if (args.Contains("--dcm-viz"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DcmVizCli", args);
    return;
}

// CLI mode: backfill entity-doc MarkdownFiles rows for a book's characters.
//   prose --backfill-entity-docs --slug <slug> [--text]
// Replays EntityDocService.InferFromTextAsync over every beat goal (+ prose text with
// --text) so future prose generation and the DCM viz see per-character entity docs.
if (args.Contains("--backfill-entity-docs"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillEntityDocsCli", args);
    return;
}

// CLI mode: re-materialize the entity-doc row for EVERY active entity, in every universe.
//   prose --repair-entity-docs [--dry-run]
// Unlike --backfill-entity-docs (per-book, inference-driven, so it only reaches entities a
// given book mentions) this iterates the entity table itself — which is what stamping
// MarkdownFiles.UniverseId on all of them requires.
if (args.Contains("--repair-entity-docs"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("RepairEntityDocsCli", args);
    return;
}

// prose --workflow-status [--slug <slug> | --all] [--json]
// Per-node or global prose service coverage matrix. Shows which services
// (Pacing, StoryMethodology, PlantPayoff, StoryAudit, Combat) were active
// when beats were written, and surfaces gaps where applicable services weren't used.
if (args.Contains("--workflow-status"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("WorkflowMonitorCli", args);
    return;
}

// prose --backfill-coverage --slug <book-or-chapter-slug>
// Populates BeatServiceLog + BeatModeLog for prose written before ProseWriterRouter
// existed, WITHOUT regenerating any beat. Runs the router's coverage-only path over
// each existing beat so --workflow-status has real logs to report.
if (args.Contains("--backfill-coverage"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillCoverageCli", args);
    return;
}

// prose --backfill-synopses --slug <s> [--model <id>] [--force]
// prose --backfill-structure-roles --slug <s> [--force]
// Fill missing beat metadata without touching prose. Synopses via LLM (BeatGoal proxy
// for mode detection); StructureRole deterministically by book-global Save-the-Cat arc.
if (args.Contains("--backfill-synopses") || args.Contains("--backfill-structure-roles"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillBeatMetaCli", args);
    return;
}

// prose --audit-book --slug <book-or-chapter-slug> [--deep] [--full] [--model <id>] [--out <path>] [--json]
// The "Player Piano" — one repeatable command running the full QA battery + the
// Structural Integrity Index (SII), a deterministic Findings rollup (BookHealthService).
// See AuditNodeCli.cs's own header comment for the authoritative, kept-in-sync tier list
// (10 FREE / 16 DEEP / 7 FULL checks as of 2026-08-30 — do not re-duplicate the list here,
// it drifted stale from BookHealthService.RunAsync once already).
// --model retargets the deep/full tier LLM calls (e.g. Haiku) for the run.
if (args.Contains("--audit-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("AuditNodeCli", "--audit-book", args);
    return;
}

// prose --publish-readiness --slug <slug> [--json]
// docs/LOGIC.md §9's five-point publish-readiness convergence gate as a single readout
// (2026-08-30) — see BookHealthService.PublishReadinessAsync and PublishReadinessCli.cs.
// Read-only, no LLM calls, no cost gate needed.
if (args.Contains("--publish-readiness"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PublishReadinessCli", args);
    return;
}

// prose --estimate-cost [--beats <N>] [--pov-characters <M>] [--tier free|deep|full]
// Cost-governance check (RFC 0009 §9.5, 2026-08-13): prints the LLM call count implied by
// BookHealthService's current wiring for a book of N beats — no DB access, pure arithmetic
// against the tier shapes read directly out of the code. Run this before merging a new
// per-beat service so the cost jump is visible before it ships, not discovered by totaling a
// bill months later.
if (args.Contains("--estimate-cost"))
{
    // Pure arithmetic, no DB/universe scope needed — deliberately skips BuildCoreServices so
    // this stays usable as a quick sanity check without an established universe context.
    Environment.ExitCode = await EstimateCostCli.RunAsync(args, null!);
    return;
}

// prose --commandment-audit --slug <nodeSlug> [--json]
// Renamed from --book-audit (2026-08-30) — collided by verb/noun order with the unrelated
// --audit-book (the full QA battery); a typo silently ran the wrong tool.
// Audits a node against 7 commandments — gateway (PreviousNodeId=null) or
// sequel (PreviousNodeId set). Pass/warn/fail per commandment with fix hints.
// Exit 0 = all pass, 1 = advisory warnings, 2 = blocking failures.
if (args.Contains("--commandment-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BookAuditCli", args);
    return;
}

// prose --generate-blueprint --slug <nodeSlug> [--retrofit] [--json]
// Generates the StructuralBlueprint — pre-prose anti-tell commitments (subplot,
// temporal scheme, resolution mode, escalation curve, event palette, ending,
// intertextual anchors). StoryScope countermeasures; bible → blueprint → prose.
// --retrofit infers the blueprint from already-written prose.
if (args.Contains("--generate-blueprint"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("GenerateBlueprintCli", "--generate-blueprint", args);
    return;
}

// prose --set-structural-blueprint --slug <nodeSlug> --file <path.json>
// Hand-author a blueprint with no LLM call, matching GenerateBlueprintCli's response contract —
// for when the generation provider is unavailable but the structural decisions are already made.
if (args.Contains("--set-structural-blueprint"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetStructuralBlueprintCli", args);
    return;
}

// prose --storyscope-audit --slug <nodeSlug> [--json]
// Verifies the book against measurable AI-fiction structural tells (StoryScope):
// flat escalation, event monoculture, moral gloss, emotion ratio, char-intro
// method, resolution mode, subplot execution, consensus clichés, TTCW originality.
// Findings triaged BLOCKER/MODERATE/MINOR; loop back into future beat prompts.
// Exit 0 = clean, 1 = moderate/minor, 2 = any blocker.
if (args.Contains("--storyscope-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("StoryScopeAuditCli", "--storyscope-audit", args);
    return;
}

// prose --chekhov-audit --slug <nodeSlug>
// Chekhov's Gun audit: extract all concrete props/anchors/traits and test whether
// each earns its place. ORPHANED = appears with no payoff; DECORATION = repeated
// without new function; EARNS_IT = each appearance serves a distinct narrative purpose.
// Run before trimming any prose detail.
if (args.Contains("--chekhov-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("ChekhovAuditCli", "--chekhov-audit", args);
    return;
}

// prose --duel --beat-id <guid> --candidate <file> [--goal "..."] [--apply] [--json]
// Blind A/B duel: beat's current prose vs a candidate revision. 3 voters
// (register/goal/reader lenses), three-way ballot; replace needs >=2 better
// with zero dissent; splits escalate to 7 voters with written rationales.
// Verdicts hash-cached by text pair. SS-A44: invoking this IS the explicit ask.
// Exit 0 = replace, 1 = keep, 2 = error.
if (args.Contains("--duel"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatDuelCli", args);
    return;
}


// prose --export-personas-json [--out <path>]
// Exports all 1024 Legion persona details + OCEAN psychometric profiles to JSON
// for consumption by the Python ML package (v3/ml/artifacts/personas.json).
if (args.Contains("--export-personas-json"))
{
    var outPath = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault()
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "ml", "artifacts", "personas.json"));
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    var personas = MindAttic.Legion.PersonaLibrary.AllDetails
        .Select(p =>
        {
            var profile = MindAttic.Legion.PersonaLibrary.Profiles.TryGetValue(p.Id, out var pr) ? pr : null;
            var ocean   = profile?.Ocean;
            return new
            {
                p.Id, p.Archetype, p.Worldview, p.Background, p.Age, p.Quirk,
                Ocean = ocean == null ? null : new
                {
                    ocean.Openness, ocean.Conscientiousness, ocean.Extraversion,
                    ocean.Agreeableness, ocean.Neuroticism,
                },
            };
        });
    var json = System.Text.Json.JsonSerializer.Serialize(
        personas.ToList(),
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(outPath, json);
    Console.WriteLine($"Exported {MindAttic.Legion.PersonaLibrary.AllDetails.Count()} personas to {outPath}");
    return;
}

// prose --sanity-scan (--slug <slug|code> | --all) [--json]
// Deterministic prose checks — no LLM. Catches leaked internal node codes,
// undefined all-caps acronyms, encoding corruption, and heft-floor violations.
// Exit 0 = clean, 1 = warnings only, 2 = any blocks.
if (args.Contains("--sanity-scan"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SanityScanCli", args);
    return;
}

// prose --duplicate-entity-scan --universe <slug> [--json]
// Deterministic scan for duplicate/near-duplicate character Entity names within a universe
// that aren't explained by legitimate cross-book OriginNodeId disambiguation. No LLM.
// Exit 0 = none found, 1 = candidates found (informational — read the prose before merging).
if (args.Contains("--duplicate-entity-scan"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DuplicateEntityScanCli", args);
    return;
}

// prose --duplicate-entity-scan-broad --universe <slug> [--entity-type <type>] [--json]
// LLM-assisted scan for duplicate rows the deterministic scan above cannot catch (a title/rank/
// code suffix or otherwise different name for the same entity, not a 1-character typo). Two-stage
// cost-bounded design — see DuplicateEntityScanService.ScanBroadAsync. Report-only, costs real
// LLM calls — gated like other LLM-calling commands.
if (args.Contains("--duplicate-entity-scan-broad"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("DuplicateEntityScanBroadCli", "--duplicate-entity-scan-broad", args);
    return;
}

// prose --reconcile-book-entities (--id <guid> | --slug <slug>) [--universe <u>]
// Phase 0 (repair) of the corpus-trust-recovery plan: finds Entity rows describing a FORMER
// identity of a character this book's current bible names differently (a full rename, not a
// typo — name-based dedup structurally can't catch this). Report-only. See
// BookEntityReconciliationService for the two-stage cost-bounded design.
if (args.Contains("--reconcile-book-entities"))
{
    Environment.ExitCode = await HubCliClient.ForwardWithCostGateAsync("ReconcileBookEntitiesCli", "--reconcile-book-entities", args);
    return;
}

// prose --merge-entity --winner <guid> --loser <guid>
// The execution half of the report-only duplicate-scan tools — a human, having confirmed two
// rows are the same identity from real book/prose knowledge, executes the merge. No LLM call,
// no fuzzy matching. See MergeEntityCli.
if (args.Contains("--merge-entity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MergeEntityCli", args);
    return;
}

// prose --scan-edge-duplicates --universe <slug> [--json]
// Report-only: flags (Source, Target) pairs with more than one live RelationType wording
// (link_entities free-text drift, e.g. "owns" vs "has"). See ScanEdgeDuplicatesCli.
if (args.Contains("--scan-edge-duplicates"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ScanEdgeDuplicatesCli", args);
    return;
}

// prose --merge-edge --keep <edgeId> --dedupe <edgeId> [--as <canonicalRelationType>] [--register-alias]
// The execution half of --scan-edge-duplicates — collapses two Edge rows describing the same
// relationship under different wording into one. See MergeEdgeCli.
if (args.Contains("--merge-edge"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("MergeEdgeCli", args);
    return;
}

// prose --set-edge-validity --edge <edgeId> [--slug <slug>] [--from-beat-number <N>]
//        [--until-beat-number <N>] [--clear-from] [--clear-until]
// Sets/adjusts/clears an existing edge's beat-scoped validity window (2026-09-02, replaces the
// dead DateTime story-time mechanism). See SetEdgeValidityCli.
if (args.Contains("--set-edge-validity"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SetEdgeValidityCli", args);
    return;
}

// prose --export-entity-cluster --root <entityGuid> --universe <slug> --out <path.md>
// Report-only: walks the full connected component from --root and archives it to Markdown —
// the review step before --delete-entity-cluster. See ExportEntityClusterCli.
if (args.Contains("--export-entity-cluster"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExportEntityClusterCli", args);
    return;
}

// prose --delete-entity-cluster --root <entityGuid> --universe <slug> --confirm <entityCount>
// The execution half of --export-entity-cluster — hard-deletes the reviewed cluster after
// re-verifying the count and checking every entity for outside references. See
// DeleteEntityClusterCli.
if (args.Contains("--delete-entity-cluster"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("DeleteEntityClusterCli", args);
    return;
}

// prose --backfill-missing-subtype-rows [--dry-run] [--exclude-name "<name>"]...
// One-time data repair: inserts a minimal Characters/Places row for any character/place Entities
// row that has none (root cause: raw SQL writes bypassing the app — see BackfillMissingSubtypeRowsCli).
if (args.Contains("--backfill-missing-subtype-rows"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillMissingSubtypeRowsCli", args);
    return;
}

// prose --plant-audit   --slug <node> [--json]   audit plant/payoff pairs
// prose --list-plants   --slug <node> [--json]   list all pairs
// prose --add-plant     --slug <node> --plant "..." --payoff "..." [--cat detail]
if (args.Contains("--plant-audit") || args.Contains("--list-plants") || args.Contains("--add-plant"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PlantPayoffCli", args);
    return;
}

// CLI mode: Will Storr narrative-science frameworks — sacred flaw, dramatic question,
// five-act structure. Three subcommands (scene-anatomy removed 2026-08-13 — redundant
// per-beat cost sink with no automated caller, see NarrativeScienceService.cs):
//   prose --narrative-science sacred-flaw --character <slug|id> [--scaffold]
//   prose --narrative-science dramatic-question (--slug <s> | --id <beatId>) [--character <slug|id>]
//   prose --narrative-science five-act --slug <nodeSlug>
//   (add --json to any subcommand for raw JSON output)
if (args.Contains("--narrative-science"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("NarrativeScienceCli", args);
    return;
}

// prose --clone-book (--id <guid> | --slug <slug>) [--title "New Title"] [--book-code SM1] [--draft] [--status ready]
if (args.Contains("--clone-book"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CloneNodeCli", args);
    return;
}

// ── Edit Sessions ─────────────────────────────────────────────────────────────
// prose --start-session --slug <slug> --label "prose-pass-1" [--type prose-pass|gripes-cleanup|logic-sweep|custom]
if (args.Contains("--start-session"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("StartSessionCli", args);
    return;
}

// prose --close-session (--slug <slug> | --session-id <guid>)
if (args.Contains("--close-session"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CloseSessionCli", args);
    return;
}

// prose --list-sessions --slug <slug> [--limit N]
if (args.Contains("--list-sessions"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ListSessionsCli", args);
    return;
}

// prose --session-beats --session-id <guid>
if (args.Contains("--session-beats"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SessionBeatsCli", args);
    return;
}

// prose --sync-outline-from-session --session-id <guid> [--dry-run]
if (args.Contains("--sync-outline-from-session"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SyncOutlineFromSessionCli", args);
    return;
}

// prose --sync-blueprint-from-session --session-id <guid>
if (args.Contains("--sync-blueprint-from-session"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("SyncBlueprintFromSessionCli", args);
    return;
}

// prose --close-all-sessions
// Called by the /commit skill before every commit to flush open edit sessions,
// run bible + blueprint sync for each, and draw a clean 3B coordination boundary.
if (args.Contains("--close-all-sessions"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CloseAllSessionsCli", args);
    return;
}

// prose --coordinate --slug <slug> [--json <path>] [--no-stamp]
// Full-coverage bible↔blueprint↔beat coordination: correlate every beat's meaning,
// construction, and prose; emit JSON + stamp the "## Beat Coordination Index".
if (args.Contains("--coordinate"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CoordinateCli", args);
    return;
}

// Strand Progress Dashboard: every non-archived book, code/title/kind/status/score/pages,
// sorted by score descending. Cross-universe by design. See .claude/commands/progress.md.
if (args.Contains("--progress"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ProgressCli", args);
    return;
}

// /show lookup: resolve a subject (name/slug/alias) to one Entity or Node and return a
// structured profile. See .claude/commands/show.md.
if (args.Contains("--show"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ShowCli", args);
    return;
}

// Corpus-wide repair: remove CharacterAlias/PlaceAlias/FactionAlias/WeaponAlias rows whose
// Value matches their own owning entity's Name — a redundant self-alias, usually a leftover
// from an entity merge that relinked a loser's alias onto the winner it now duplicates.
if (args.Contains("--fix-self-aliases"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("FixSelfAliasesCli", args);
    return;
}

// prose --ensure-chapter --slug <slug> | --all
// Enforce "every story has >= 1 chapter": wrap a flat story's direct beats into a
// single ChapterNode child (no-op if already chaptered). No LLM.
if (args.Contains("--ensure-chapter"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("EnsureChapterCli", args);
    return;
}

// prose --backfill-meaning --slug <slug> [--limit N] [--dry-run]
// Fill the MEANING coordinate (Beat.Description) for beats with prose but no meaning.
if (args.Contains("--backfill-meaning"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BackfillMeaningCli", args);
    return;
}

// prose --generate-event-list --slug <slug> [--force] [--limit N] [--dry-run] [--model <id>]
// Fill the per-beat plot-EVENT one-liner (Beat.EventSummary) — "what happened".
if (args.Contains("--generate-event-list"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("GenerateEventListCli", args);
    return;
}

// prose --extract-beat-locations --slug <slug> [--force] [--limit N] [--dry-run]
// Backfill the per-beat scene location (Beat.PlaceName / PlaceEntityId) — hash-gated.
if (args.Contains("--extract-beat-locations"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExtractBeatLocationsCli", args);
    return;
}

// CLI mode: stamp Beat.StoryPosition — a book's reading order as a number, which is the engine's
// authoritative story clock (author ruling 2026-09-04: track time in beats; the wall clock is an
// overlay for day/night alignment and short timers that have to add up). Deterministic and FREE:
// reads the order GetOrderedBeatsAsync already defines, writes an integer, touches no prose and
// marks no beat dirty. Re-run after anything that changes reading order.
//   prose --beat-positions [--slug <slug-or-code-or-id>] [--all] [--dry] [--json]
if (args.Contains("--beat-positions"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("BeatPositionsCli", args);
    return;
}

// prose --location-scan [--min-travel-minutes N]
// Character-in-two-places-at-once contradiction scan; conflicts land in Findings.
if (args.Contains("--location-scan"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LocationScanCli", args);
    return;
}

// prose --lint-prose --slug <slug> [--dry-run]
// Deterministic prose linter: echo words, crutch phrases, pet words, dialogue-attribution runs.
if (args.Contains("--lint-prose"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("LintProseCli", args);
    return;
}

// prose --pov-audit --slug <slug> [--dry-run]
// Head-hopping + same-scene voice-sameness audit (batched Haiku; findings loop back).
if (args.Contains("--pov-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("PovVoiceAuditCli", args);
    return;
}

// prose --hook-audit --slug <slug> [--dry-run]
// Chapter-ending hook strength analysis; weak non-final endings file findings.
if (args.Contains("--hook-audit"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("HookAuditCli", args);
    return;
}

// prose --export-event-list --slug <slug>
// Write the current per-beat event list to {CODE}-Events.txt in the publish-export folder (no LLM call).
if (args.Contains("--export-event-list"))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("ExportEventListCli", args);
    return;
}

// CLI mode: show running token cost tally for the current process, or the durable per-command
// calibration data the cost gate estimates from.
//   prose --cost              print session cost table
//   prose --cost --json       emit summary as JSON
//   prose --cost --reset      clear the ledger
//   prose --cost --history [--command <name>] [--take N] [--json]
//                             print CommandCostHistories — what each cost gate calibrates from
// When appended to another command (e.g. prose --write-node --slug foo --cost),
// the cost of that command's LLM calls is printed after the command finishes.
if (args.Contains("--cost") && (args.Contains("--history")
    || args.Length == 1 || (args.Length == 2 && (args.Contains("--json") || args.Contains("--reset")))))
{
    Environment.ExitCode = await HubCliClient.ForwardAsync("CostCli", args);
    return;
}

// ────────────────────────────────────────────────────────────────────────────
// DI bootstrap helpers — replace WebApplication.CreateBuilder in every CLI block.
// Host.CreateDefaultBuilder loads appsettings.json + env vars but starts no
// HTTP server, no Kestrel, no Blazor middleware.
// ────────────────────────────────────────────────────────────────────────────

// Eagerly resolves IUniverseContext so its constructor sets UniverseScope.Current
// immediately (Prose.Core/Services/UniverseContext.cs line ~169), before any CLI
// dispatch block runs. This makes every command's universe scoping fully DB-driven off the
// live Universe table — the same path IUniverseContext already uses — instead of depending
// on whichever dispatch blocks happen to resolve IUniverseContext themselves. Adding a new
// universe (a new Universe row) needs no C# change anywhere after this: UniverseBootstrap.
// ResolveWellKnownId's hardcoded switch only still matters for a process that never calls
// this (there are none left, post-fix), so it's inert now rather than load-bearing.
// Cheap and safe pre-migration: the constructor doesn't touch the DB — catalog load is lazy
// and already has a try/catch fallback to an empty/no-op scope.
static IServiceProvider Finalize(IServiceProvider sp)
{
    var universe = sp.GetRequiredService<IUniverseContext>();

    // HARD RULE: an explicitly-requested --universe/PROSE_UNIVERSE slug that doesn't match any
    // registered universe must NEVER silently fall through to the persisted "current_universe"
    // default. Without this check, a typo (`--universe scyr`) or a slug that hasn't been
    // registered yet resolves to Guid.Empty in UniverseContext.EnsureLoaded's catalog lookup,
    // processOverride stays unset, and the command silently scopes to whatever the last human
    // left as default — the exact cross-universe bleed this rule exists to make impossible.
    // "Unacceptable" per user directive 2026-08-01: fail loud, never fail quiet.
    var requested = UniverseBootstrap.RequestedSlug ?? Environment.GetEnvironmentVariable("PROSE_UNIVERSE");
    if (!string.IsNullOrWhiteSpace(requested)
        && !string.Equals(universe.CurrentSlug, requested, StringComparison.OrdinalIgnoreCase))
    {
        var known = string.Join(", ", universe.ListUniverses().Select(u => u.Slug));
        Console.Error.WriteLine(
            $"[universe] Unknown universe slug '{requested}'. Registered universes: {known}. " +
            "Refusing to fall back to a default — pass a valid --universe slug.");
        Environment.Exit(2);
    }
    return sp;
}

static IServiceProvider BuildCoreServices(string[] args)
    => Finalize(Host.CreateDefaultBuilder(args)
        .ConfigureLogging(lb => lb.AddConsole())
        .ConfigureServices((_, svc) => svc.AddProseServices())
        .Build()
        .Services);

