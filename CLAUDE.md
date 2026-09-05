# Prose Project Rules

## Conversation
- A bare "do" / "do it" / "yes" from the user means "continue", "keep going", "proceed". Resume the current task without asking for clarification.

## Rate Limit & Context Protection

### Rate Limit (billing — HARD STOP at 96%)
See global rules in ~/.claude/CLAUDE.md. The rate-limit-monitor skill enforces:
- Warn every 5% starting at 80%, every 1% starting at 91%
- **Hard stop at 96%** — queue pending tasks to ~/.claude/rl-queue.json, write handoff to ~/.claude/rl-handoff.md
- Every exceeded limit = ~$30 charge on the credit card

### Context Window (conversation — HARD STOP at 96%)
- When approaching 96% context usage, STOP immediately
- Create a task list of all pending/in-progress work
- Write a handoff summary to memory so the next session can resume seamlessly
- Tell the user to take a break and come back after cooldown

## Structural Hierarchy (HARD RULE, no exceptions)

**Book → Chapter → Beat. That is the entire hierarchy.** Every beat belongs to exactly one
chapter (via `BeatNodes`); every chapter belongs directly to exactly one book (`Nodes.ParentNodeId`
points at a `Kind='book'` node). **No chapter may ever have another chapter as a child.** A
"chapter containing chapters" is always a bug, not an intentional container/wrapper/anthology
structure — there is no such thing as a section, part, or act node in this schema. If content
needs sub-grouping (an anthology arc, a multi-job sequence, a "Ghost Period"-style era), split it
into more top-level chapters under the same book, in sequence, never nest one chapter under
another.

**Verification query (run this after any structural edit, and periodically as a corpus health
check):**
```sql
SELECT p.Title AS ParentChapter, bk.Title AS BookTitle, COUNT(c.Id) AS NumChildChapters
FROM Nodes c JOIN Nodes p ON c.ParentNodeId = p.Id
LEFT JOIN Nodes bk ON p.ParentNodeId = bk.Id
WHERE p.Kind = 'chapter' AND c.Kind = 'chapter'
GROUP BY p.Title, p.Id, bk.Title
```
Any row returned is a violation and must be fixed by reparenting the child chapters directly to
the book (giving them proper sequential SortKeys/titles in the live reading order), never by
leaving them nested. **Discovered corpus-wide 2026-08-14** — BCODA had 15 chapters improperly
nested under "Chapter 22 — Ghost Period" (155 orphaned beats, ~30% of the book, never read or
audited in any prior sweep); Ballast, It Came From Iowa, and Read the Room each had an entire
book's real chapter list nested under a redundant "Chapter 1" wrapper sharing the book's own
title (an import artifact). All four were reparented as part of this fix. Always run a full
recursive descendant walk (`WITH descendants AS (...)`) rather than a single-level
`ParentNodeId = <book>` query when counting/reading a book's chapters — a flat query silently
misses anything nested deeper, exactly as it did here.

## Database Access

**HARD RULE, ABSOLUTE (author ruling 2026-08-22): nothing reaches the database except through
Prose.Hub — reads AND writes, no exceptions.** No raw `sqlcmd`, not even a read-only `SELECT` to
"just check a value." No direct EF/DbContext access outside the Hub process. Every interaction with
the database — a one-line lookup, a data correction, a prose edit, an entity/alias change, a schema
migration — goes through Prose.Hub, via `Prose.Cli.exe` (which forwards to the running Hub process)
or an MCP tool that routes through it. The Hub exists so that every change is calibrated, tested,
verified, and weighted through one gatekeeper; a raw SQL statement — even read-only — bypasses that
entirely, and other subsystems (Trinity Reconciliation, self-heal, findings, hash-gated
re-extraction) have no record it happened. This is what keeps the prose from drifting out from
under itself. An earlier version of this section told readers to query the DB directly via `sqlcmd`
for read-only lookups — that guidance was wrong and caused a real incident; do not resurrect it.

For an ad hoc lookup, use the `/show` skill (looks up anything in the Prose database by loose
natural-language description and renders it as a private Artifact) or an existing CLI `--flag` /
MCP tool. **If no command exists for something you need to read or write, stop and tell the user
the gap exists — do not self-authorize a raw SQL workaround, no matter how small, reversible, or
well-reasoned it seems.** The user decides whether to build a proper Hub-routed tool first, do it
another way, or grant a one-time documented exception.

**Key schema facts** (for understanding query patterns the CLI/MCP tools use — not for you to run
directly):
- **Beats → Nodes relationship:** `BeatNodes` table (fields: `NodeId`, `BeatId`, `SortKey`, `IsEnabled`)
  joins `Beats` to `Nodes` via `Beats b JOIN BeatNodes bn ON b.Id = bn.BeatId JOIN Nodes n ON bn.NodeId = n.Id`
- **Beat scoring:** Column is `Score` (not `MeanScore`), type `float`; NULL if unscored
- **HARD RULE — Book→Chapter→Beat hierarchy:** Always verify all three levels before assessing a book. Books with chapters ARE books, even if ChapterBeats is empty. Never say a book is "empty" or "planning stage only"—say "chapters structured, prose not yet written."

**HARD RULE — no SQL deletes of any kind (SS-A37, tables renamed by SS-A43):** `Nodes`, `Beats`, and
`BeatNodes` are system-versioned temporal tables — deleting via raw SQL bypasses all application
guards and is unrecoverable without a point-in-time restore. Any book/beat removal must go through
the CLI (`prose --beat delete`). If a book genuinely needs to be deleted, get explicit user
confirmation naming the book by title and slug before touching the DB.

**Node deletion cascade order (system rule):** When deleting a book node via `--delete-node`, the required order is beats → chapters → book node. The CLI handles this automatically (cascade was added 2026-07-18). Never try to delete a parent node before its chapter children — `FK_Nodes_ParentNode` will block it. Delete order for manual confirmation: (1) BeatNodes memberships + exclusive beats for the child chapter, (2) structural blueprints for the child, (3) the child chapter node, (4) then the same sequence for the parent book node.

## Code Style
- Do NOT use underscore-prefixed variables (e.g., `_myField`). Use `camelCase` for private fields without the underscore prefix.
- JSON only for all data files. No Python scripts, no YAML, no Markdown files except README.
- Web-only project (Blazor Server). No MAUI host.
- The null-conditional operator `?.` (and `?[]`) is **not allowed inside an EF Core expression-tree lambda** (anything that becomes a SQL query — `Select`/`Where`/`GroupBy` projections, etc.). It fails to compile (`CS8072`). Project the scalar **before** the terminal operator instead: write `g.OrderByDescending(h => h.CreatedAt).Select(h => h.Score).FirstOrDefault()`, not `g.OrderByDescending(...).FirstOrDefault()?.Score ?? 0`.

## Prose Content — Graphic Adult Content

Adult content in this project is **graphic adult content**: real adult situations rendered with real adult reactions. This is NOT ultraviolence (gratuitous gore for spectacle's sake) and NOT pornography (arousal-focused titillation).

What it means in practice:
- **Violence**: describe what the body actually experiences — the broken bottle dragged through a face, the weight of a corpse, bone under pressure. Physical consequence, not spectacle.
- **Sex**: the body's involuntary responses — the shaking, the orgasmic contractions, the specific physical sensation. Not euphemism, not fade-to-black unless the scene demands it.
- **Extreme affect**: the body does what it does under adrenaline and extremity — the vibrating rat-tat-tat of a heavy machine gun that gives someone a hard-on. Write it.
- The frame is always **literary authenticity**: what a person actually feels, grounded in physical reality.

**Hard limits — absolutely non-negotiable:**
- No sexual content involving minors. No exceptions.
- No sexual content involving animals. No exceptions.

## World Rules
- The symbol Φ is the QUANTA currency symbol. It is NEVER the Greek letter phi. Φ PRECEDES numbers like a dollar sign: Φ100, not 100Φ. Terminology: "quanta", "Q", or "Qs". Physical medium: credstick only — no coins, no bills.
- Iowan Behemoths are autonomous machines, NOT synthetic life. They are not alive.
- Default to mixed heritage from unexpected global combinations (Ubiquitous Diaspora).

## Per-Node Documentation (SS-A11 + SS-A45)

Every book node with active prose has a **unified Book Context Document** stored in
`Nodes.NodeOutline` (DB) and mirrored to `docs/nodes/<CODE>.md` (generated read-only file).

**SS-A45 (shipped 2026-07-15): ALL generated `.md` files are gitignored. They do not exist
in the repo between sessions.** The DB is the heap (permanent, authoritative). `.md` files
are ephemeral stack variables — materialized on demand, never committed, GC'd when done.

**NEVER hand-edit `docs/nodes/<CODE>.md`.** It is a generated artifact — edits are overwritten
the next time `generate_node_doc` runs. **Never assume these files exist — always regenerate first.**

| Location | Contains | Source of truth / how to edit |
|---|---|---|
| `CanonDocumentSections` (DB) | Every canon doc's actual content, keyed by `CanonDocumentType` — a DB-backed lookup table (`Prose.Core\Data\Entities\CanonDocumentType.cs`, resolved via `CanonDocumentTypeRegistry`), not a compiled C# `enum`, despite the dot-notation below (`WorldBible`, `WorldMaster`, `Franchise`, `UniverseCanon`, `CraftGuide`, `UniverseCraft`, `DelightGuide`, `EngineGuide`, `CharacterDoctrine` — confirmed 2026-08-30, all 9 exist, no gap either direction) | MCP `set_canon_section` — this is the ONLY sanctioned edit path for every generated doc in the row below, with zero exceptions |
| `Nodes.NodeOutline` (DB) | **The single source of truth for that book's Outline** — the sequenced, entity-tagged, intent-charged plan: arc, characters, voice, structural notes, blueprint, Event Sequence | `set_book_outline` MCP (hand-authored sections) |
| `docs/nodes/<CODE>.md` | Generated mirror of `Nodes.NodeOutline` — ephemeral, gitignored | Re-run `generate_node_doc` to materialize |
| `docs/BIBLE.md`, `docs/WORLD.md`, `docs/FRANCHISE.md`, `docs/universes/ENTOS.md`, `docs/CRAFT.md`, `docs/GLMZ.md`, `docs/SCRY.md`, `docs/DELIGHT.md`, `docs/ENGINE.md`, `docs/CHARACTER.md` | Generated canon docs — **ALL ten are DB-backed via `CanonDocumentSections`, ephemeral, gitignored** (verified live 2026-08-23: every one of these carries a `<!-- GENERATED — do not hand-edit -->` banner and real section counts from the DB — this table previously and wrongly told readers to "hand-edit directly" for 5 of these 10 files; that instruction was stale from before they were migrated into `CanonDocumentSections`, and following it risked a silent data-loss bug: a hand edit destroyed by the next `--generate-canon-md --all`) | Edit content via `set_canon_section` MCP, then re-run `prose --generate-canon-md --type <type>` (or `--all`) to materialize the `.md` mirror |
| Character record `Speech*`/`Psychology*` fields (DB) | Per-narrator voice — **Register layer** of DCM static hierarchy (SS-A46; no `docs/registers/` files) | `create_character` with the id + `speechPatternsJson`; loaded per-beat by DCM |
| `docs/books/<name>.md` | Legacy long-form book spines (BCODA; maintained in place) | Hand-edit directly |
| `docs/USER_STORIES.md` | Epic index + acceptance criteria | Hand-edit directly |

### The Three Altitudes — the named examination model

Every book is examined at three magnifications (canonical definition: `docs/LOGIC.md` §8):

| Altitude | What you see | Instrument |
|---|---|---|
| **10,000 ft — book** | Arc, structure-as-designed | `Nodes.NodeOutline` + structural blueprint |
| **100 ft — chapter** | What actually happens, in order | `NodeChapterSummaries` / `story-synopsis.txt` |
| **10 ft — beat** | The prose; who's in the room | Beat text + `BeatEntityPresence` + verifications |

The altitudes must tell the same story — defects ARE altitude disagreements. **Arbitration
(author ruling 2026-08-29, replaces the old "prose wins on facts / bible wins on locks" fixed
rule): no altitude is automatically authoritative.** Outline ⇄ Book ⇄ Entities is a three-way
symbiosis — each corner is verified by the other two, and a divergence is resolved case-by-case
on evidence (which side is actually stale, and why), never by a blanket rule. Trinity
reconciliation is the canonical arbiter: every ruling is recorded as a revertible
`ReconciliationDecision` row, not a silent auto-win. `prose --altitude-audit --slug <slug>`
compares 10,000↔100 ft (findings filed as OutlineDrift, evidence-based recommendations); the
logic sweep's `outline_agreement` dimension owns 100↔10 ft the same way. **Planning and review
start at chapter altitude** — read `story-synopsis.txt` before deep beat reads; drop to beat
altitude only where a finding points.

### Dynamic Context Memory (Dynamic Context Memory) — the named protocol

**"Dynamic Context Memory" (Dynamic Context Memory)** is the canonical name for the beat-scoped, drift-free
context loading protocol used in all Prose prose generation (new books AND edits
to existing books). Use this name when referring to the system in code comments, docs,
and conversation.

**Three phases:**
1. **Materialize** — for the current beat, pull from DB exactly what is relevant: the beat's
   node outline section, blueprint slice, referenced entities (characters, places, factions,
   weapons), recent beat window, and applicable canon fragments. Generate these as .md files.
2. **Inject** — DocContextService includes only the materialized .md files in the LLM prompt
   for this beat. Nothing outside the current scope enters the context.
3. **Release (GC)** — after X beats without reference, the .md file is garbage-collected from
   the LRU working set. Only a small, current-beat-relevant subset persists at any moment.

### The DCM Static Hierarchy — Tier 0 + four layers

**Corrected 2026-08-23** (was documented as only 4 layers — `docs/ENGINE.md` exists, is real,
DB-backed, and loads on every single beat above everything else, but had never been added here).
These five resources are **always** loaded at the correct scope — they are the only static
resources in DCM. Everything else (entities, topics, relational cascade) is dynamic.

| Layer | File | Scope | How loaded |
|---|---|---|---|
| **0 — Engine** | `docs/ENGINE.md` | Every universe, every story, unconditionally | `tier: always` in its own frontmatter — same generic "always" mechanism `DocContextService.PrepareContextAsync` step 1 uses for the pinned universal core; loads above CRAFT.md per its own text ("This is DCM tier 0 — it loads above CRAFT.md and above every universe document, on every beat, always") |
| **1 — Base** | `docs/CRAFT.md` | All universes, all stories | Globally pinned via `add_context_doc` (24h; renew each session) |
| **2 — Universe** | `docs/GLMZ.md` (GLMZ) or `docs/SCRY.md` (Fantasy) | One universe | Globally pinned; keyword triggers activate per-book |
| **3 — BookOutline** | `docs/nodes/<CODE>.md` | One book | `node` tier — auto-loaded by DocContextService; evicts on book change |
| **4 — Register (SS-A46)** | The **POV character's Character record** (`SpeechVocabulary` / `SpeechCadence` / `SpeechSubtext` / `SpeechUnderPressure` / `SpeechIntimacyRegister` / `PsychologySecret`) | One narrating character (**per beat** — POV can change within a book) | The beat's narrator (from the outline **POV Map** → `BeatEntityPresence` `PresenceType='pov'` row) is materialized and **pinned dominant** (score 999) by `DocContextService.PrepareForNodeAsync(povEntityId:)`; other present characters' registers still load via clue-gathering (step 0) but don't override the narrator's |

`docs/CHARACTER.md` (the Character Doctrine, `CanonDocumentType.CharacterDoctrine`) is a **topic**
tier doc, not a static layer — it loads via keyword triggers (character/cast/protagonist/
antagonist/relationship/dialogue/motive/arc/pov, etc.), same as any other topic doc, not
unconditionally on every beat.

**Hierarchy resolution (reworded 2026-08-30 — the prior wording stated a priority order ranking
Engine last/weakest, then reversed it with a blanket exception, which read as self-contradictory):**
Engine tier is supreme and out-of-band — SS-ENGINE-0's own text says "none may contradict this
one," full stop, no exception. Among the other four tiers, when they conflict, the narrowest
scope wins for its own book: Register > BookOutline > Universe > Base.

**Voice is the character, not a file (SS-A46, 2026-07-20).** There are no `docs/registers/<NAME>.md` files and no imposed tonal/flagship registers (JOY, SORROW, Kyle/CODA are retired and deleted). A narrator's voice lives in their **Character record's speech/psychology fields** — so it is loaded automatically by the existing DCM entity-doc path when that character is on the page, and it **evolves as the record evolves**: update the character (via `create_character` with the id, or a wound/continuity claim) and the next beat's prose tracks the change. The clear base voice (CRAFT.md §0–§2) is the floor; the character's own diction and attention are the only "register." A Pixel chapter reads in Pixel's voice; a Bear chapter in Bear's.

**Why Dynamic Context Memory prevents drift:** The LLM sees only what is pertinent to the current beat's world.
Unrelated canon, stale entity states, and out-of-scope book data never enter the prompt.
Drift happens when context is too wide; Dynamic Context Memory keeps it narrow by construction.

**Applies to:** all new books (including M101), all beat-by-beat edits to existing books.
The prose engine (ProseWriterRouter) implements Dynamic Context Memory automatically; Claude Code triggers it
by calling prose generation tools at beat scope, not book scope.

**How the system works:**

During prose generation (via ProseWriterRouter / CLI / MCP), DocContextService handles
context injection automatically. For the current beat it pulls exactly the relevant entities,
beats, blueprint slice, node outline section, and canon fragments — materializes them as .md
files — injects them into the LLM prompt — then GCs them after a sliding window of
non-reference. **The engine manages the scope; you don't have to.** Only a small, relevant
subset of .md files is present at any moment — never a full dump of all data.

**Context assembly (DocContextService.PrepareForNodeAsync) — corrected 2026-08-30, was
documented as five steps, is really seven** (a holistic-cleanup audit found this section had
been written from a stale internal code comment rather than the actual `PrepareContextAsync`
method body — it silently dropped the entire "series" tier and the pinned-doc override):

0. **Clue-gathering inference** (EntityDocService.InferFromTextAsync): scans the beat goal
   text via SceneContextAssembler (name scan + embedding + graph expansion). For every entity
   found, calls EnsureEntityDocAsync — hash-gated, so unchanged entities are a no-op. Freshly
   materialized entity docs land in MarkdownFiles as DB-only rows (category "entity-doc",
   SyncedBy "inferred", no disk file) with keyword triggers from name/slug/aliases. Because
   this runs BEFORE the candidate query, the new docs participate in the passes below immediately.

Then `PrepareContextAsync` runs, in order:
0. **pinned** (score 999) — caller-supplied `pinnedDocIds` override every other tier; this is
   how the POV register gets pinned dominant (see the DCM static hierarchy table below).
1. **always** — pinned universal core (BIBLE.digest.md)
2. **node** — the active book's one outline + one register + book docs (evicted on book change)
2.5. **series** — cross-book arc docs (`docs/series/*.md`) whose declared scope matches this
   book's universe/series identifier (not its own NodeCode — see the code's own bug-fix note on
   `ScopeMatches`). Survives ~8 beats, long enough to carry a plant/payoff callback across a book
   boundary. This tier is discussed elsewhere in this file (the "book coordination board") but
   its DCM loading mechanism was never previously connected to that discussion.
3. **keyword** — topic docs whose Triggers match the beat-goal text (includes newly-created
   entity docs from step 0, since they carry name/slug triggers)
4. **embedding** — topic docs semantically near the beat goal (markdown embedding scope)
5. **relational cascade** — for every resident doc with RelatedIds, load its linked docs
   one level deep (from the `related:` frontmatter field, resolved to GUIDs on sync)

**Entity .md scoping — the Lyra vs Vega rule:**

Character and entity .md files persist in the LRU working set exactly as long as they are
relevant to the current prose:

- A character present on every page (e.g. Lyra in VIGL) keeps their `LastTouchedAction`
  refreshed on every beat through `RecordMentions()`. They never evict.
- A character who leaves the book for many beats (e.g. Vega between Part 1 and Port
  Gadriket reunion) evicts automatically after `EvictAfterActions = 4` beats without a
  reference. Their `.md` vanishes from the working set until the prose references them again.
- **Worst case:** a character's `.md` evicts before it is needed again. The engine re-fetches
  from DB on the next `PrepareForNodeAsync()` call. No state is lost — the DB is the heap.

The tuning knob is `EvictAfterActions` in `DocContextStack`. Topic docs evict after 4 beats;
`node`-tier docs evict on book change (not time). Do not change `EvictAfterActions` without
understanding the Lyra/Vega tradeoff: lower = tighter context, higher = warmer cache.

**Dynamic Context Memory relational graph — the `related:` frontmatter field:**

Any `.md` file can declare related documents in its YAML frontmatter:

```yaml
---
related: docs/nodes/VIGL.md, docs/universes/ENTOS.md
---
```

When a doc is loaded into the working set, `DocContextService` (step 5 of `PrepareContextAsync`)
cascades its related docs into the set automatically — one level deep, no recursive fan-out.
Cascaded docs land as `topic` tier with reason `related:<parent-path>`.

**How related IDs are resolved:** `MarkdownFileService.SyncAllAsync` runs a two-phase process:
1. Upsert all files, collecting raw `related:` paths in memory
2. After all files are saved, resolve each relative path to its `MarkdownFile.Id` GUID and write
   to `MarkdownFiles.RelatedIds` (the resolved CSV of GUIDs)

The `related:` field contains project-relative paths (e.g., `docs/nodes/M101.md`, not slugs or
GUIDs). Paths that don't resolve to a known `MarkdownFile` are silently dropped.

**When to use `related:`:**
- A node outline that references specific canon docs heavily (e.g., `docs/universes/ENTOS.md`)
- A universe doc that depends on a companion canon doc
- Entity docs (future) linking to their place or faction docs

**What `related:` is NOT for:**
- Dynamic entity discovery (that's the clue-gathering inference layer, not yet implemented)
- Replacing keyword `triggers:` for topic-based loading — use `related:` only for structurally
  dependent docs where loading one doc always warrants loading the other

**Empirical truth update pattern — when canon changes in prose:**

When a story event confirms a new empirical fact about an entity (character death, injury,
state change, relationship shift, location exit), update the DB source FIRST, then regenerate:

1. Update the entity row via MCP (`create_character`, `log_wound`, `apply_continuity_claim`, etc.)
2. Re-run `generate_node_doc` (or `generate_canon_md`) to regenerate the `.md` mirror
3. Re-run `prose --sync-markdown` to push the updated content to `MarkdownFiles`
4. The next prose call loads the regenerated `.md` — all future beats see the confirmed fact

A character who dies in Chapter 3 must be updated in DB with the death record (killed by whom,
which beat, in-world date) before Chapter 4's `.md` is generated. Never edit the `.md` directly
(it's read-only on disk by `FileAttributes.ReadOnly`). DB → regenerate → sync is the only path.

**When Claude Code needs to READ content** (for planning, review, or analysis — not prose
generation), trigger generation at the **narrowest possible scope**:

```powershell
prose --generate-node-doc --slug <slug>      # one book's outline + blueprint
prose --generate-canon-md --type <type>      # one canon doc (not --all unless needed)
prose --sync-markdown                        # push to MarkdownFiles so DocContextService sees it
```

Or MCP: `generate_node_doc` (slug required) + `sync_markdown_files`. Do not run
`--generate-canon-md --all` unless you genuinely need every canon doc in scope.

**Workflow:**

1. **Before writing or editing a book** — trust the prose engine to auto-inject context.
   For planning/review where Claude Code needs to read the outline: generate at narrow scope,
   then GC when done (`powershell -File tools/codex.ps1 gc`).
2. **To update arc, characters, voice register, or structural notes** — call `set_book_outline` MCP
   with updated hand-authored markdown. Then re-run `generate_node_doc` so the engine
   picks up the change on the next prose call.
3. **When a story event confirms an empirical fact** — update DB first (entity row, continuity
   claim, wound log), then regenerate and sync. Never edit `.md` directly.
4. **Blueprint and Event Sequence sections are always generated** — edit their sources:
   `prose --generate-blueprint` for the blueprint, MCP beat tools for beat titles/descriptions.

Never Read or Glob for ephemeral .md files without regenerating them first — they don't
exist in the repo and may not exist on disk.

**Existing node outlines:**
- `docs/nodes/PXL.md` — Pixel / PXL (Pixel origin story, GLMZ; formerly PNHL/TDIU; Channeler+Ghost+Splicer; Detroit escape opening)
- `docs/nodes/BCODA.md` — Bushido Coda flagship novel (GLMZ)
- `docs/nodes/ATTE.md` — Attendance / Yemina Fola investigation (GLMZ)
- `docs/nodes/VATD.md` — Vultures at the Door / Thomas & Levin (GLMZ)
- `docs/nodes/DWIACE.md` — Death Whispers in a Cat's Ear / Rennick Investigations (GLMZ)
- `docs/nodes/SPRW.md` — Sparrow / Elias Macias & the orbital mystery (GLMZ)
- `docs/nodes/MNEMO.md` — Mnemosync / Amara & Seto (GLMZ, in progress; formerly ULC, redesigned SS-A14)
- `docs/nodes/TEST.md` — Testament / Bear court-martial (GLMZ)
- `docs/nodes/M101.md` — M-101 / Declan Doyle origin before VIGL (Fantasy; Verlaine Taking → desertion; renamed from "Soren Rowe" at some point — that name appears nowhere in the current outline or prose)
- `docs/nodes/MxG.md` — Magenta & Gunmetal / GLMZ run (GLMZ, planned; Shadowrun-style heist → True Lies finale)
- `docs/nodes/RTR.md` — Read the Room / Faith Larson & Ethan Wolfe (GLMZ; Fenris band; Faith is a Read; Milwaukee dive club)
- `docs/nodes/LLSS.md` — Lieutenant Lyra, Sinterkin Slayer (Fantasy; standalone; COMPLETE; VIGL prose register exemplar; 1 beat; was LSSS/"Lyra, Sinterspawn Slayer")
- `docs/books/bushido-coda-strands-bible.md` — BCODA (legacy long-form; superseded by BCODA.md above)

## Codex (how to work with the canon)

The project follows the **MindAttic Codex** documentation standard. The source of truth lives under
`docs/`:

- **`docs/BIBLE.md`** (L0) — engine invariants (SS-LAW-N) + **GLMZ** universe canon. Authoritative
  for GLMZ world facts. Fantasy/Entos universe facts live in `docs/universes/ENTOS.md`.
  Inherits laws from `D:/Projects/MindAttic/MindAttic.HouseRules.md`.
- **`docs/ENGINE.md`** — DCM **Tier 0** (loads above CRAFT.md, on every beat, unconditionally):
  the Prime Rule ("a defect is fixed in code, never by writing a paragraph about it"), what the
  engine checks automatically (logic sweep dimensions, craft-audit rules), and measured/calibrated
  numeric thresholds. **DB-backed** (`CanonDocumentType.EngineGuide`) — edit via `set_canon_section`
  MCP, then `prose --generate-canon-md --type EngineGuide`. Never hand-edit the `.md` file.
- **`docs/CRAFT.md`** — universal prose rules, Base layer of the DCM static hierarchy. Applies to
  all universes (GLMZ and SCRY/Fantasy). Source: hoisted §5 universals from WORLD.md + LDGR-C/K
  audit (8 DON'Ts, 8 DOs). **DB-backed** (`CanonDocumentType.CraftGuide`, confirmed live 2026-08-23
  — do not hand-edit, a prior version of this doc wrongly said to). Edit via `set_canon_section`
  MCP, then `prose --generate-canon-md --type CraftGuide`. Synced + globally pinned each session.
- **`docs/GLMZ.md`** — GLMZ Universe craft layer (DCM static tier 2): transaction register, world
  texture, the weird, interludes, hard prohibitions. Craft additions on top of CRAFT.md.
  **DB-backed** (`CanonDocumentType.UniverseCraft`, confirmed live 2026-08-23 — do not hand-edit).
  Edit via `set_canon_section` MCP, then `prose --generate-canon-md --type UniverseCraft`. Synced +
  globally pinned each session.
- **`docs/SCRY.md`** — SCRY/Fantasy Universe craft layer (DCM static tier 2): naming canon
  (universe = SCRY; world = The Entos), death permanent, tone/visual, the weird, prohibitions.
  **DB-backed** (`CanonDocumentType.UniverseCraft`, same type as GLMZ.md, scoped by universe;
  confirmed live 2026-08-23 — do not hand-edit). Edit via `set_canon_section` MCP, then
  `prose --generate-canon-md --type UniverseCraft`. Synced + globally pinned each session.
- **`docs/DELIGHT.md`** — positive prose doctrine (13 DOs from the top-decile-beat + praise-ballot
  analysis); craft companion to CRAFT.md, globally pinned, injected per beat-mode by
  `DelightProseGuidance`. **DB-backed** (`CanonDocumentType.DelightGuide`, confirmed live
  2026-08-23 — do not hand-edit). Edit via `set_canon_section` MCP, then
  `prose --generate-canon-md --type DelightGuide`.
- **`docs/CHARACTER.md`** — the Character Doctrine: a binding craft law (every universe) that
  "the Bible governs what is true; this governs who the people are" — cast-as-people, the
  Relational Law (interpersonal interaction as the 90+ lever), etc. Topic-tier (keyword-triggered,
  not always-pinned — see the DCM hierarchy section above). **DB-backed**
  (`CanonDocumentType.CharacterDoctrine`) — previously undocumented in this file entirely. Edit via
  `set_canon_section` MCP, then `prose --generate-canon-md --type CharacterDoctrine`.
- **Per-narrator voice (DCM static tier 4, SS-A46)** — lives in the POV character's **Character
  record** (`Speech*` + `Psychology*` fields), NOT in a file. There are no `docs/registers/<NAME>.md`
  files (the folder is retired). The record is loaded per-beat as that character's entity doc by DCM
  clue-gathering inference, and evolves as the character does. Edit voice via `create_character`
  (pass the id + `speechPatternsJson`), not by hand-editing a register file.
- **`docs/WORLD.md`** — **GLMZ** world master: how the city works, how the cast works, how combat
  works. (Craft/voice rules moved to `docs/CRAFT.md` + `docs/GLMZ.md`.) **DB-backed**
  (`CanonDocumentType.WorldMaster`). Edit via `set_canon_section` MCP.
- **`docs/FRANCHISE.md`** — **GLMZ** franchise & IP bible: commercial positioning, genre, logline.
  **DB-backed** (`CanonDocumentType.Franchise`, confirmed live 2026-08-23 — do not hand-edit; this
  row previously contradicted the Per-Node Documentation table above, which already correctly
  listed FRANCHISE.md as a generated/ephemeral file). Edit via `set_canon_section` MCP.
- **`Nodes.NodeOutline`** (DB, L0 per-book) — book arc, Event Sequence, character rules,
  voice register, structural blueprint. **The single source of truth for that BookNode's Outline.**
  Mirrored to `docs/nodes/<CODE>.md` as a generated read-only file — never hand-edit the file.
- **`docs/USER_STORIES.md`** (L2) — test-cited stories + backlog + audit log. Every `✅` names its
  verifying test or recorded evidence.
- **`docs/series/GLMZ.md`** — GLMZ universe book coordination board: main series chapter
  roster (Books 1–5), standalone book roster, character arc ledger, villain supply chain,
  cross-book plant/payoff registry, world-revelation sequencing, entity seeding roadmap.
  **Update this doc whenever a book is added, a character state is resolved, or a plant/payoff
  is confirmed.** This is a planning instrument, not a canon source.
- **`docs/planning/_TEMPLATE.md`** — mandatory 10-section book brief template. Every new GLMZ
  book fills `docs/planning/<CODE>-brief.md` from this template before a node outline is created
  (see New Story Workflow Step 0 below).
- **`docs/rfc/`** — design notes that graduate into BIBLE.md or book outlines.
- **`docs/data/`** (L5) — canon-as-data: JSON Schemas + the master entity-identity table for the
  `engine_data/*.json` seed corpus. **Live canon is the SQL DB, not files** (SS-LAW-1).
- **`docs/BIBLE.digest.md`** — GENERATED by `tools/codex.ps1 digest`; never hand-edit. The
  SessionStart hook (`.claude/hooks/inject-digest.ps1`) injects it as authoritative context.

**`docs/AMENDMENTS.md` is RETIRED (2026-07-04).** All amendments have been merged into their
canonical destinations. Do not append to it. Do not reference it.

Working rules:
- **Canon changes go DIRECTLY into the authoritative source** — every canon doc in the table above
  (`BIBLE.md`/`WORLD.md`/`FRANCHISE.md`/`ENTOS.md`/`CRAFT.md`/`GLMZ.md`/`SCRY.md`/`DELIGHT.md`/
  `ENGINE.md`/`CHARACTER.md`) is `CanonDocumentSections` (DB) via `set_canon_section` MCP —
  **never hand-edit the `.md` file directly for any of these**, `Nodes.NodeOutline` (via
  `set_book_outline` MCP) for book-specific facts. There is no amendment layer. After updating
  NodeOutline, re-run `generate_node_doc` + `prose --sync-markdown`. After calling
  `set_canon_section` for any canon doc, re-run `prose --generate-canon-md --type <type>` (or
  `--all`) to materialize the `.md` mirror, then `prose --sync-markdown` so DocContextService
  picks up the change.
- A fact lives in **exactly one file**; cite it by its stable `{#SS-...}` id, never by line number.
- Update the Outline/books status in the **same change** that moves a goal; "done" means a test or
  build proves it.
- After editing any `docs/*` canon file, run `powershell -File tools/codex.ps1 digest` then
  `powershell -File tools/codex.ps1 doctor` — doctor must pass. (`pwsh` is not installed; use `powershell`.)
- The repo rule "no Markdown except README" is amended for the Codex `docs/*.md` set (see SS-A1);
  data files stay JSON.

## New Story Workflow — LOCKED PIPELINE (mandatory — see [docs/BIBLE.md §10](docs/BIBLE.md#SS-§10))

**Author ruling (2026-08-10): this sequence is locked and must be followed in order, without
exception, for every new book.** No stage is skipped and no stage is reordered to reach prose
faster. The premise is unnegotiable: if a book doesn't make sense at the outline/synopsis level,
it will not make sense as prose — prose cannot repair a structural or causal defect sitting
underneath it. When something breaks downstream, walk back to the stage that actually owns the
defect and fix it there (a wrong fact → fix Stage 3's outline; a plot hole → fix Stage 3/4's spine;
a never-linked entity → fix Stage 1/2), then re-run forward. **Never paper over a lower-stage
defect by throwing more generation at a later stage** ("no more throw a million tokens at it and
see if that fixes it" — author's words, binding).

0. **Series Brief** — fill `docs/planning/<CODE>-brief.md` using the template at
   `docs/planning/_TEMPLATE.md`. The brief must cover all 10 sections before a node outline is
   created. A book that cannot answer all 10 sections does not belong in the roster yet.
   After filing: update `docs/series/GLMZ.md` Book Roster (§1–2), Character Arc Ledger exit
   states (§3), and Plant/Payoff Registry (§5). Check World-Revelation Sequencing (§6) — this
   book must not reveal anything before its designated book.
1. **Entity Seeding** — seed every named character, CorpoNation/faction, place, and weapon into
   the DB via CLI or MCP **before anything downstream references them**. The cast, locations, and
   factions must exist as real rows before the outline names them or the plot uses them.
2. **Relationship Linking — gate: 100% resolution** — every relationship declared on a seeded
   entity (`CharacterRelationships`, `FactionRelationships`, etc.) must resolve to a real
   `TargetEntityId` pointing at another seeded entity, or be explicitly an intentional off-page
   reference (e.g. "an aunt never otherwise named"). Verify before proceeding — a relationship
   left null because the target was simply never seeded is a Stage 1 defect, not an acceptable
   gap: go back, seed the missing entity, and re-save the relationship so it resolves. Do not
   carry silently-broken edges into the outline.
3. **Book Structure (SS-A43)** — create a **BookNode** (MCP `create_book` / CLI `--create-book`)
   + **ChapterNode** children (MCP `create_chapter`, parent required). Authorial spine (14-beat
   outline) = the book node's `seed` text. This stage is pure infrastructure — an empty shell with
   title/slug/seed — not content decisions; it exists this early only because `SetBookOutline`
   writes onto `Nodes.NodeOutline`, which requires the row to already exist.
4. **Outline & Plot** — if new world facts: GLMZ facts → `docs/BIBLE.md`/`docs/WORLD.md`;
   Fantasy/Entos facts → `docs/universes/ENTOS.md`. Write the book's hand-authored content (arc,
   characters, voice register, structural notes, POV map) via `set_book_outline` MCP into
   `Nodes.NodeOutline`. Every named entity and relationship the outline describes must already
   exist from Stages 1–2 — the outline describes the graph, it does not invent it. Add a story
   entry to
   `docs/USER_STORIES.md`; run `codex doctor`. Do NOT use `docs/AMENDMENTS.md` — it is retired.
5. **Synopsis Coherence Gate — mandatory, prose-free** — before any structural blueprint or prose
   is generated, read the chapter-by-chapter synopsis/outline end-to-end (100 ft altitude —
   `story-synopsis.txt` / `NodeChapterSummaries`, or the authorial spine if chapter summaries don't
   exist yet) and validate it on:
   - **Causal soundness** — every event has an established cause, every decision a motivation;
     nothing happens because the plot needs it to.
   - **Timeline consistency** — the book's internal clock holds together across chapters.
   - **Pacing shape** — escalation is monotonic or deliberately shaped, never flat or randomly spiky.
   - **Thematic delivery** — the stated logline/theme is actually dramatized by the chapter
     sequence, not just asserted in the brief.
   - **Relationship utilization** — every edge linked in Stage 2 is actually used somewhere in the
     synopsis. A linked-but-unused relationship is a planning defect: either the relationship or
     the plot beat that should exercise it is missing.
   Use `prose --altitude-audit --slug <slug>` (existing 10,000↔100 ft drift check) as a starting
   instrument, then read the synopsis directly — findings triage BLOCKER/MODERATE/MINOR exactly
   like a logic sweep. **A book that fails this gate goes back to Stage 3/4 for an outline/spine
   rewrite. It does not proceed to Stage 6 "to see if it works out in the writing."**
6. **Structural blueprint (StoryScope countermeasures)** — only once Stage 5 is clean at BLOCKER,
   run `prose --generate-blueprint --slug <slug>` (MCP `generate_structural_blueprint`). This
   commits the structural anti-tell decisions BEFORE prose: thematically-parallel subplot +
   carrier beats, temporal scheme, resolution mode (never internal-understanding), moral polarity
   (ambivalent default), per-beat escalation curve, event-type + revelation-mode palette, optional
   form device, ending style (avalanche, no epilogue), 3-5 intertextual anchors from the entity DB.
   Then run `prose --generate-node-doc --slug <slug>` to regenerate `docs/nodes/<CODE>.md` with the
   blueprint section auto-populated from the DB. If the LLM provider is unavailable (e.g. the
   standing Anthropic credit outage) but the structural decisions are already authored by hand in
   the brief/outline, use `prose --set-structural-blueprint --slug <slug> --file <path.json>` instead
   — same STRICT JSON contract as the generator's prompt, no LLM call, saved with
   `GeneratedBy="manual"` for honest provenance.
7. **Prose** — Sonnet draft → Opus polish → reflow → logic sweep (see Quality Verification SOP below) → scan entity mentions.
8. **Export** — `--export-node`; flip USER_STORIES to ✅ with evidence.

Never write prose before Stages 0–6 are complete and Stage 5 has passed clean at BLOCKER.

## Prose Engine Services (use all of these — see SS-A16)

The beat-generation pipeline has several layers. Use **`ProseWriterRouter`** as the entry point
for all prose writing — it coordinates all the services below and logs coverage.

### Entry points
| Path | When to use |
|---|---|
| `ProseWriterRouter.WriteAsync(context, beatId, beatIndex, totalBeats)` | All beat writing from UI + CLI |
| `CombatSceneWriter.WriteCombatSceneAsync(request)` | Explicit multi-exchange combat setpiece (numExchanges > 1, full loadout tracking) |
| `BeatGeneratorService.GenerateBeatAsync(context)` | Legacy path — direct generation without coverage logging |

### Context enrichment chain (all wired inside ProseWriterRouter)

**Corrected 2026-08-23, then again 2026-08-30** (a holistic-cleanup audit found this table still
omitted several load-bearing services after the 2026-08-23 pass — most critically
`EntityContextService`, whose `CharactersInScene` auto-populate gates ~8 of the other rows below
— plus two rows whose documented gate condition didn't match the code). "Always" in this table
means "no explicit precondition beyond the router's own ambient state (`NodeId`/`beatId` usually
already set by the time a real beat writes)" — not "literally unconditional."

| Service | What it injects | Activation |
|---|---|---|
| `BeatModeDetector` | Classifies beat as Combat/Narrative/EmotionalClimax/Dialogue/Transition/Revelation | Unconditional, every call |
| `PacingService` | BREATHE/FLOW/TIGHTEN/STRIKE/SETTLE prose rhythm | `totalBeats > 0`; Combat forces STRIKE regardless |
| `StoryMethodologyService` | Save the Cat structural role (Opening Image → Final Image) + Scene-Sequel type | `totalBeats > 0` |
| `DelightProseGuidance` | Positive doctrine (docs/DELIGHT.md): emphasizes the 2–3 reader-loved moves matching the beat mode | Unconditional, all beat modes (mode-keyed) |
| `CombatProseGuidance` (`CombatProseConstants`) | Verbs-first, fragment sentences, no emotion-naming, dissociated observer | `BeatMode.Combat` |
| `EntityContextService` | **(added to this table 2026-08-30 — load-bearing omission)** Loads the LRU entity working-memory stack for this beat, then auto-populates `context.CharactersInScene` from its active character-type entries (up to 3) when the caller left it empty. This single auto-populate is what actually gates `DialogueService`, `ContinuityService`, `ConsequenceService`, and `SceneCollisionService` below on a normal call that doesn't pre-set `CharactersInScene` itself — gracefully empty on a cold stack (fills after the first `ReconcileAsync`) | `context.NodeId != Guid.Empty` |
| `BeatPlaceService` | **(new 2026-08-28)** Per-beat scene location: the nearest prior beat's extracted `Beat.PlaceName` in this chapter becomes `context.Location` (scene-continuity default), ahead of the book-wide `DefaultLocation` ancestor-walk fallback. Populate via the SCENE-LOCATION slice of the consolidated post-write extraction (new beats) or `prose --extract-beat-locations --slug <slug>` (backfill) | `Location` unset by caller + `NodeId`/`beatId` set |
| `SceneContextBuilder` | Ambient sensory grounding **incl. the New Weird anomaly layer absorbed from the deleted `AmbientAnomalyService` (2026-08-28 — the two used to double-inject overlapping anomaly blocks under two labels)** | **`context.Location` non-empty** — scene-granular once a book's beat locations are extracted (row above); before that, only the 14/46 books with `DefaultLocation` |
| `DialogueService` | Multi-character RELATIONSHIP DYNAMICS + DIALOGUE RULES; per-character voice profiles ONLY when no XRay block exists (2026-08-28: when XRay is present it emits the complement — behavioral tells, inferred subtext, heritage/age register — instead of duplicating the six `Speech*` fields XRay already carries) | `Dialogue`/`EmotionalClimax` modes + `CharactersInScene.Count > 0` |
| `SceneContextAssembler` (+ `WoundLedgerService`) | Per-entity XRay: voice/psychology/wound/behavior profile of everyone on-page — since 2026-08-28 also `verbal_tics`/`avoidances` (previously only in DialogueService's duplicate block), making XRay the single canonical voice rendering | `beatId != Guid.Empty` — never fires on a preview/no-beat-id write. **Runs BEFORE DocContextService since 2026-08-28** and awaits `PersistPovAsync`, fixing the POV ordering race: the 'pov' row used to be written fire-and-forget AFTER DocContext read it, so POV register pinning was a no-op on every beat's FIRST generation. `PersistPovAsync` also now defers to any pre-existing pov row (outline POV-map backfills win over the roster heuristic; previously it could write a SECOND pov row for the beat and `SELECT TOP 1` picked nondeterministically) |
| `SceneCollisionService` | **(undocumented until 2026-08-23)** How on-page characters' psychology collides given the beat goal | 2+ `CharactersInScene`, non-Combat mode, XRay context present, **and a non-empty `context.BeatGoal`** (omitted from this table until 2026-08-30) |
| `ContinuityService` | Canonical/confirmed fact constraints for on-page characters | `CharactersInScene.Count > 0` — empty until the entity pre-check/XRay stack has warmed or the caller set it explicitly |
| `VerificationContextService` | **(added to this table 2026-08-30)** Resolves the beat's POV entity id (`BeatEntityPresence` `PresenceType='pov'` row) that `DocContextService.PrepareForNodeAsync` uses to pin the narrator's Register layer dominant (score 999) | Runs whenever `DocContextService` fires, i.e. `beatId != Guid.Empty` |
| `ContinuityEnforcer` | **(new 2026-08-22, undocumented until now)** Post-generation LLM check: does the just-written beat contradict a CANONICAL/CONFIRMED claim it was actually shown? Closes the gap where the canon block above was prompt-side-only with no verification | After generation, when `ContinuityService` produced a non-empty canon block for the scene (2026-08-30 fix: the code used to skip this check, paying for the LLM call even on a beat where the canon block was empty — the code now matches this documented condition) |
| `TensionEscalationService` | Warns when beats have stagnated at low intensity | `beatIndex > 2` |
| `ReaderKnowledgeService` | Dramatic-irony bookkeeping — what the reader currently knows | `NodeId != Guid.Empty` |
| `ConsequenceService` | Gear/cyberware/status constraints for the full on-page cast | `CharactersInScene.Count > 0` |
| ~~`ConsequenceEngine`~~ | **DELETED 2026-08-28** — its KV store (`world_consequences`) lost its only writer when the `--write-story` contract loop was removed, so every beat paid to read a permanently stale blob; the three MCP tools over it (`get_recent_consequences` etc.) went with it. `ConsequenceService` (row above) is the live DB-backed constraint source | — |
| `WorldStateAtBeatService` | Temporal entity-state snapshot (drift from canon) | `beatId != Guid.Empty` |
| `NarrativeSummaryService` | Rolling compressed memory of prior beats | `NodeId != Guid.Empty` |
| `ChapterSummaryService` | DB-backed prior-chapter memory | `NodeId != Guid.Empty` **and `beatIndex > 0`** (intentional — no prior chapter exists for the first beat; documented in the code itself) |
| `OpenThreadsService` | Unresolved promises/plants/questions | `NodeId != Guid.Empty` |
| `BeatExtractionService` | **(added to this table 2026-08-30 — load-bearing omission)** The consolidated post-write extraction orchestrator itself — fans one call out to reader-knowledge facts, scene summary, open-threads, plot-state, the SCENE-LOCATION slice (`BeatPlaceService`), and the MOTIFS slice (`MotifLedgerService`), replacing what used to be five separate Haiku calls each re-reading the beat text | `capturedNodeId != Guid.Empty && !string.IsNullOrWhiteSpace(capturedResult)` — runs after generation, not before |
| `MotifLedgerService` | **(new 2026-08-28)** "MOTIFS IN PLAY" — recurring images from the `BookMotifs` ledger (sighted in 2+ beats): deepen/refract, never reintroduce as new. Written by the MOTIFS slice of the consolidated post-write extraction | `NodeId != Guid.Empty`; empty until a motif recurs |
| `BookStateLedgerService` | Arc-level named state (crises, dramatic questions, alliances) | `NodeId` set — **confirmed 2026-08-23: the "long books" gate this table used to claim never existed... It runs on every book regardless of length. Confirmed again 2026-08-30: the code gates on `NodeId` only, not `beatId` as an earlier version of this row also (separately) claimed.** |
| `StoryScienceService` | King + Storr craft laws: sacred-flaw consistency, status dynamics, curiosity gap, causal chains, sensory specificity | `totalBeats > 0` |
| `StructuralBlueprintService` | Per-beat StoryScope anti-tell slice: subplot carrier, anachrony cut, escalation floor, event type, ending/resolution mode | Router-level gate is just `totalBeats > 0`; the blueprint-existence check happens separately, inside `BuildBeatInjectionAsync` itself, not as a precondition the router evaluates before calling in (clarified 2026-08-30 — this table used to conflate the two into one combined condition) |
| `BeatBlueprintDecision` ("Track B") | A separate structural-decision block (declared purpose + pre-state). **Coverage conflation FIXED 2026-08-28**: the three structural mechanisms are now tracked as independent variables and merged only at prompt assembly — coverage rows `StructuralBlueprint`, `BeatContract`, and the new `StoryScopeLoopback` each report their own mechanism honestly | `beatId != Guid.Empty` + a `BeatBlueprintDecisions` row exists |
| StoryScope audit loop-back | Queries prior STORYSCOPE findings into forward guidance — own coverage row (`StoryScopeLoopback`) since 2026-08-28 | `NodeId != Guid.Empty` |
| `NarrativeChartService` | Offscreen/parallel character activity (world continuity) | `beatIndex > 2` **and** `totalBeats > 0` |
| `UniverseGraphService` | Entity pre-check (soft gate — warns via a "do not invent backstory" prompt block, never blocks/corrects) — flags proper nouns in `BeatGoal` not present in `AllNodes()` | Non-empty `BeatGoal` |
| `HarvestRevealedDetailsAsync` (on `SceneContextAssembler`) | **(undocumented until 2026-08-23)** Opt-in harvesting of newly-revealed entity details from the beat goal back into the entity's own record | `AutoHarvestRevealedDetails` setting (opt-in, off by default) |
| `PlantPayoffService` | Active plant/payoff pairs for the book | `BeatContext.NodeId != Guid.Empty` for the coverage-length metric computed here; **the actual prompt injection happens inside `BeatGeneratorService`, not `ProseWriterRouter`** — this row and the one below describe where the block is logged, not where it's built |
| `BookAuditService` | Gateway or Sequel commandments (7 each, auto-detected from `PreviousNodeId`) | Same caveat as `PlantPayoffService` above |
| Reader-Proxy QA loop-back | Folds prior `ComprehensionDefect`/`CraftChecklist`/`ReaderGripe` findings into forward-looking guidance, same pattern as `EMOTIONAL-DEPTH`/`READABILITY` below. **Since 2026-08-28 also carries the `LINT` block** (mechanical linter findings — `prose --lint-prose`), and the CraftChecklist category now also receives `POV `/`VOICE ` (`prose --pov-audit`) and `HOOK ` (`prose --hook-audit` + automatic chapter-close check) findings | `NodeId != Guid.Empty`, no guidance already supplied by the caller |
| `LibertyReportService` | Rule-of-Cool check | `beatId != Guid.Empty` + non-empty result (findings loop back into later beats) |
| `SemanticFidelityService` | Goodhart intent-drift check | `beatId != Guid.Empty` + non-empty `capturedBeatGoal` |
| `CanonGroundingService` | Canon-grounding scaffold | **Opt-in, `AutoCanonGrounding` setting, off by default — NOT "Always" as this table previously claimed.** Turning this on globally is a per-beat LLM-call cost decision, not a documentation fix; ask before flipping the default. |
| `ContextTelemetryService` | **(added to this table 2026-08-30)** Observability sidecar — records which docs/entities loaded for this beat (DCM Gantt visualization data), not a content-injection stage | `telemetry != null` (ambient DI availability) |
| `WorkflowMonitorService` | **(added to this table 2026-08-30, despite being named in this section's own "Entry points" intro since before 2026-08-23)** Fire-and-forget `BeatServiceLog` coverage write — which of the OTHER rows in this table were actually applicable/active for this beat, the data `prose --workflow-status` reads | Unconditional per beat (background `Task.Run`, non-blocking) |

Note: there is no class named `WorldGraphService` in the live tree — the entity-graph service is
`UniverseGraphService`. `UniverseGraphService.GetEntityBrief`/`GetSceneContext`/`GetContextForNode`
already implement richer relationship-aware formatting (`[RelationType] OtherName — description`)
but are not called anywhere in `ProseWriterRouter` — only `AllNodes()` is, for the name-check above.
Relationship/edge semantics for the roster that IS assembled come from `SceneContextAssembler`
instead (see its `RELATIONSHIPS:` block, wired 2026-08-21 — previously the graph-expansion pass
loaded `Edges` rows only to decide roster membership and discarded `RelationType`/`Description`
before they ever reached the prompt).

`EmotionalDepthService` (8-dim Want/Need/Wound rubric) is **not** called by ProseWriterRouter directly —
it only runs via `--examine-emotion`, `BookHealthService` DEEP tier, or the hash-gated daily
`SanityScanBackgroundService` sweep (draft tier, added 2026-08-21 — skips a book whose beat text is
unchanged since its last examination), and its `EMOTIONAL-DEPTH`-prefixed findings become live
guidance one beat later through the generic findings-loop-back mechanism.

**One other generation entry point exists and does NOT share this enrichment chain.** `StoryDirectorService`
and `Write.razor` (the two entry points originally flagged here 2026-08-09) were both deleted
2026-08-13 along with the entire Blazor UI (commit `ed22bd4f6`, "Command-line only") — the project
is CLI/MCP/Hub-only now, not Blazor Server. The last bypass, `SceneGenerationService`, was
**deleted 2026-08-23 as confirmed dead code** (see `BeatExtractionService.cs`'s header comment) —
there is no live generation entry point outside `ProseWriterRouter` any more.

### New craft instruments (2026-08-28 tooling overhaul)

- `prose --extract-beat-locations --slug <slug>` — backfill per-beat scene location
  (`Beat.PlaceName`/`PlaceEntityId`, hash-gated on `PlaceExtractedFromHash`); new beats get it
  automatically via the SCENE-LOCATION slice of the consolidated post-write extraction.
- `prose --location-scan` — the (previously never-invocable) LocationContradictionService
  character-in-two-places scan; files Contradiction findings. Its travel-time axis still needs
  in-world timestamps on live beats (`Beat.InWorldDate` does not exist — backlog).
- `prose --lint-prose --slug <slug>` — deterministic RepetitionLintService: echo words, crutch
  phrases, pet words, unattributed-dialogue runs, airless-narration runs, floating-heads beats.
  Zero LLM cost; files `LINT ` CraftChecklist findings (loop back into generation). Run
  `prose --compute-metrics --slug` first for the dialogue-proportion checks. **Structure checks
  (2026-09-05):** `ALT-SCENE` (same scene told twice — shared 8-word runs within a chapter window)
  and `OUTLINE-HOOK` (an outline `**ChN - …**` entry names a timestamp/quote/beat no beat in
  chapter N contains) FILE findings; `FIRST-TIME` and `BATCH-OUTLIER` are printed read-flags only
  (>90% false-positive on the first full BCODA read — they must not feed generation guidance).
  The findings themselves are in the Findings table (`prose --findings list --node <slug>`), not
  in the CLI's 60-line report tail.
- `prose --pov-audit --slug <slug>` — PovVoiceAuditService: head-hopping out of the recorded
  POV + same-scene voice sameness (batched Haiku; `POV `/`VOICE ` findings).
- `prose --hook-audit --slug <slug>` — ChapterHookService: chapter-ending hook type + strength
  0-3; weak non-final endings file `HOOK ` findings. Also fires automatically at chapter close
  (`ChapterCloseProcessorService` step 3.5, one Haiku call, not vote-gated).
- Motif ledger: `BookMotifs` table + `MotifLedgerService`; extraction via the MOTIFS slice of
  the consolidated call; recurring motifs (2+ beats) inject as "MOTIFS IN PLAY" guidance,
  automatic, no manual authoring. **Corrected 2026-09-01** (this row previously called the KV
  store below "legacy, do not extend" — wrong, and renamed to stop implying it): the separate
  KV-backed `AuthoredMotifRegistry` (was `MotifService`) is not a legacy predecessor of
  `MotifLedgerService` — it's a still-live, distinct feature (manually/LLM-authored, named +
  described + kind-tagged motifs) backing the `get_motifs`/`plant_motif`/`propose_motifs` MCP
  tools, none of which `MotifLedgerService` has an equivalent for. Do extend
  `AuthoredMotifRegistry` if that manual-authoring surface needs more capability; don't assume
  `MotifLedgerService` should absorb it. `BookReviewService.ReviewAsync` and
  `analyze_writing_quality`'s dependency on `AuthoredMotifRegistry` via the legacy Book/Chapter
  model remains a known, documented limitation (returns `book_not_found` for any book that only
  exists as a live Node) — not yet fixed.
- Two EF migrations pending Hub redeploy: `AddBeatPlace`, `AddBookMotifs` (both plain nullable
  ADD COLUMN / new table; apply at next Hub restart).
- Subplot-thread health was NOT added as a new instrument: `prose --storyscope-audit` already
  covers it (`subplot_not_executed` carrier-beat check + the single-track/interleave check).

### Story Ledger instruments (2026-09-02→04) — full methodology in [docs/LEDGER.md](docs/LEDGER.md)

These shipped across Story Ledger Phases 0–5 and were absent from this file entirely until
2026-09-04. **Everything here is Hub-routed; nothing touches the DB directly.**

- `prose --tuned-read --slug <s> [--dry] [--no-extract] [--max-candidates N] [--json]` — the
  cross-predicate contradiction pass. **`--dry` first, always**: it runs the whole deterministic
  half for free and reports candidate counts plus *why* an axiom was silent. A real run bills one
  Sonnet adjudication per uncached candidate; verdicts cache on
  `(claim pair, axiom, both anchor TextHashes)`. FULL tier of `--audit-book`.
- `prose --continuity anchor-beats [--slug <s>] [--dry]` — **run this before trusting any
  tuned-read result.** Deterministic, zero LLM cost: recovers `SourceBeatId` by matching each
  claim's own snippet against its chapter's beats, using the same containment test the
  quote-grounding gate uses. Fails closed on an ambiguous match. Corpus coverage was **24/24,758
  (0.1%)** before it and 90.7% of prose claims after — an unanchored claim cannot be adjudicated,
  so the instrument was returning "clean" where it meant "could not look".
- `prose --continuity predicates [--slug <s>] [--min N] [--limit N] [--all]` and
  `--co-occur [--family <f>]` — the ledger's real predicate vocabulary, grouped into the families
  an axiom's `stem*` pattern addresses, and which families actually co-occur on one entity.
  **Author every new axiom from this, never from a guess.**
- `prose --continuity stats` — now also reports **beat-anchor coverage** and says outright when
  zero anchors make a clean result meaningless.
- `prose --continuity search --text "<s>" [--entity <id>] [--predicate-prefix <p>] [--live]` —
  free-text over EntityName, Predicate AND Object. The only way to find a fact hidden in an object
  string (`second_sword_possession → "…made by father"` matches no `father_*` prefix).
- `prose --continuity reject --claim <uid> [--note]` / `--entity <id> --predicate-prefix <p> --yes`
  — reject one claim, several, or a whole predicate family. The path for "this fact is fabricated
  and has no counterpart to lose to" (resolve needs a pair; reset-book takes the whole book).
  Reversible — system-versioned.
- `prose --exclusion-rules [--all] [--json] [--test …] [--propose …] [--approve --id <n>]
  [--reject --id <n>]` — the axiom table. A `proposed` axiom generates nothing until approved.
  `--test` checks a candidate pattern against a hypothetical claim pair for free, which is the
  cheapest guard the design has.
- `prose --provenance-audit [--slug <s>] --universe <u>` / `prose --provenance --grade <g>
  --entity <id>` — "everything in canon no human ever approved", and the promotion path.
- `prose --fact-ledger-refresh --slug <s>` — re-run only the same-predicate check (free); the
  alternative was the cost-gated `--audit-book --deep` bundle.
- `prose --chapters --slug <s>` and `read_beats(groupByChapter:)` — chapter-grouped read path
  (Phase 1). Both read payloads now expose `eventSummary`; **`Beat.Description` is authorial
  intent with no binding to the prose and must not be read as a summary.**
- `prose --description-drift` — surfaces descriptions that drifted from the text they describe
  (`DescriptionHash`, Phase 1). Report-only; never auto-rewrites.
- `prose --character-gear --character <x> [--search "<s>"] [--remove --id <n>]`,
  `prose --entity-tags --entity <x> [--add "a,b"] [--remove "a,b"]`,
  `prose --entity-relationships --search "<s>" | --character <x> --remove --id <n>`,
  `prose --orphan-beats [--limit 0] [--delete --confirm <exactCount> --archive <path>]` — surgical
  single-row tools built when a purge had no sanctioned path. `--orphan-beats` and
  `--fix-bad-name-matches` are **corpus-wide, not scoped**.

### Narrative Mode — Original vs Retelling vs Historical (added 2026-08-18)

`Nodes.NarrativeMode` classifies how a book's characters relate to authorial invention —
`"original"` (default), `"retelling"`, or `"historical"`. It gates whether personality/goal-drift
checks apply: `BookHealthService.SacredFlawAsync` (FULL tier, `NarrativeScienceService.
AnalyzeSacredFlawAsync`) only runs for `"original"` books. A retelling (a close/1:1 adaptation of a
pre-existing fixed narrative — e.g. TFAH/Paradise Lost, the four GOSPEL books) or a historical/
nonfiction book (real people/events — 1381, Irish Outlaws, Jeanne d'Arc, Sons of God Daughters of
Men) has motivations already fixed by an external source; the sacred-flaw check's "ground this
character's flaw via create_character" nudge is a category error for them — there is no invented
psychology to ground. `BehavioralInvariantEnforcer`/`BehaviorCheckAsync` (checking prose against a
character's *already-documented* rules) is NOT gated by this — that check is still meaningful for
a retelling/historical book (Milton's Satan or a historical figure should still act consistent
with their documented record).

Set via `prose --set-narrative-mode --slug <slug-or-code> --mode original|retelling|historical`.
This is an authorial classification, not something to infer or change without the author (a book
in the "fiction" universe is not automatically a retelling, and vice versa — universe is the
wrong axis; TFAH lives in the "fiction" universe alongside future original-fiction books).

### Coverage monitoring
```
prose --workflow-status --slug <slug>    # per-book service coverage matrix + gaps
prose --workflow-status --all            # global utilization across all books
```
MCP: `workflow_status`, `workflow_status_global`, `workflow_beat_modes`

### Beat writing workflow
1. Assemble `BeatContext` (XRayContext via SceneContextAssembler, NodeId always set)
2. Call `ProseWriterRouter.WriteAsync(context, beatId, beatIndex, totalBeats)` — NOT BeatGeneratorService directly
3. After writing, run `prose --examine-emotion --slug <slug>` to score emotional dimensions
4. After book complete, run `prose --commandment-audit --slug <slug>` (renamed from --book-audit
   2026-08-30 — it collided with the unrelated `--audit-book` full battery) to audit
   gateway/sequel commandments
5. After book complete, run `prose --plant-audit --slug <slug>` to check for orphaned plants
6. After book complete, run `prose --storyscope-audit --slug <slug>` to verify the structural
   anti-tells held (escalation monotonic, event types varied, no moral gloss, no epilogue,
   subplot executed). BLOCKER findings fix per logic-sweep minimal-splice rules, then re-audit.

## Prose Is Ground Truth — five HARD rules for anyone auditing a book (author ruling 2026-09-05)

Written after a session in which the assistant stated two false conclusions as fact, overwrote a
correct memory with a wrong retraction, and deleted outline-hooked beats without checking the
outline. Root cause of every one: **treating the index as the book.** Tags, `event_summary`,
outline lines, memory notes, and instrument output are all lossy *derivatives* of the prose.

1. **Prose is ground truth.** A missing entity tag, a stale `event_summary`, an absent outline
   line, or a silent instrument is a *metadata defect to file* — never evidence about the story.
   (BCODA beat #521 is Mrs. Chen's apartment; she is never *named* in it, so it carried no tag.
   "No tag" was read as "not her." It is the book's opening scene.)
2. **Read the whole chapter before ruling on anything in it.** Every real BCODA defect on record —
   Ch7's triple fight, Ch1's elevator, Ch12's #5229/#5346, the togishi "first time" line — was
   found by reading in reading order, and missed by every instrument and every sampled slice. A
   chapter that doesn't fit in context is the signal to build a deterministic check, not to
   sample and extrapolate.
3. **Before deleting or rewriting any beat, grep the outline for every distinctive literal in it**
   (timestamps, named objects, quoted lines). Any hit → stop, show the author both sides, wait.
4. **Memory holds rulings and rules only.** Findings go in the `Findings` table, tied to text
   hashes, where they expire when the text changes. No "OPEN" checklists carried across sessions.
5. **Never declare resolved what was not read in full context.** "Two different togishi —
   intentional echo" was a story manufactured to close an item. Say "I don't know" instead.

Corollaries that are code, not doctrine (see the 2026-09-05 plan): every instrument prints
`examined N of M`; zero with M=0 prints **COULD NOT LOOK**, never "clean". `event_summary_state
== stale` means the text changed after the summary was written and nobody re-ran
`--generate-event-list` — until it's current, every synopsis-based instrument is reading fiction.
None of this belongs in `ENGINE.md`/DCM: the Prime Rule there is "a defect is fixed in code, never
by writing a paragraph about it," and DCM doctrine shapes the *writing* model, not the auditor.

## Quality Verification SOP — Logic Sweeps, NOT Votes (LAW: SS-A44)

**Default QA for any book that changes or needs validation is a LOGIC & CONTINUITY SWEEP,
not a review panel and not a Legion vote.** Panels and votes are too expensive — run them
ONLY when the user explicitly asks for a vote/review/score in that conversation. The engine
enforces this (voting gate, default OFF; explicit `--allow-votes` / `allowVotes:true` only).

**Canonical methodology: [docs/LOGIC.md](docs/LOGIC.md). Invocable runbook: `/logic-sweep [slug ...]`.**

**The logic sweep:** agents read the book end-to-end (enabled beats only:
`NodeBeats.IsEnabled=1 ORDER BY NodeBeats.SortKey`) and audit six dimensions:
1. **Causality chain** — every event has an established cause, every decision a motivation,
   every capability an on-page origin.
2. **Knowledge states** — who knows what, when they learned it; nobody acts on knowledge
   they don't have.
3. **Timeline** — reconstruct the book clock; no impossibilities.
4. **Plant/payoff ledger** — two-way: every plant pays, every payoff was planted.
5. **Orphan references** — nothing references removed/disabled/merged content.
6. **Outline agreement** — prose and `Nodes.NodeOutline` (the hand-authored sections) tell the
   same story; no side auto-wins — fix whichever the evidence points to in the same change, then
   re-run `generate_node_doc`.

Reports land in `audit-outlines-<date>/logic/`; findings are triaged
**BLOCKER / MODERATE / MINOR** and fixed with minimal splices. Fix what a finding names;
if you can't name the failure, leave the beat alone.

**Reader-facing craft/comprehension QA is READER-PROXY QA (`prose --reader-qa`), not the
persona panel — canonical methodology: [docs/READER-QA.md](docs/READER-QA.md).** **Five**
instruments (corrected 2026-08-30 — this said "four" and omitted instrument 5, which
docs/READER-QA.md itself has documented since it shipped the same day), all findings-based,
NO scores: (1) Haiku comprehension probes diffed against the Sonnet synopsis,
Sonnet-arbitrated → `ComprehensionDefect` findings; (2) hash-gated binary craft/delight
checklist (`prose --craft-checklist`) → `CraftChecklist` findings; (3) cross-family pairwise
duels for every splice (`prose --duel`, SS-A44-gated); (4) findings-only gripe jury
(`prose --reader-qa --gripe-pass`) → `ReaderGripe` findings; (5) **Full-Order Read**
(`prose --reader-qa --full-order-read`) — 3-5 cross-family readers narrate ONE continuous
read start-to-finish, flagging only where their own engagement died and whether it recovered
(never a craft complaint list) → `ReaderGripe` findings, "ENGAGEMENT " prefix. Instruments
1/2/4/5-report are measurements, not votes — not vote-gated. Everything is hash-cached:
unchanged content re-runs free.

**Correctness/continuity-of-fact QA is the STORY LEDGER — canonical methodology:
[docs/LEDGER.md](docs/LEDGER.md), the third peer of LOGIC.md and READER-QA.md.** Two detectors
over `ContinuityClaims`: (1) same predicate/different object (`ContinuityService.Upsert`,
numeric-safe, volatile predicates exempt) → `FACT-LEDGER ` findings; (2) **different predicate,
incompatible meaning** — the exclusion ontology, `prose --tuned-read` → `TUNEDREAD ` findings.
Detector 2 exists because detector 1 could not *represent* the defect that motivated the whole
programme (a fabricated `father` against a constructed `origin` are different predicates, so they
never collide). **Before reading a clean tuned-read result as clean, check beat-anchor coverage**
(`prose --continuity stats`): an unanchored claim cannot be adjudicated at all, and corpus
coverage was 0.1% until `prose --continuity anchor-beats` (deterministic, free) took prose claims
to 90.7% on 2026-09-04. Author axioms from `prose --continuity predicates [--co-occur]`, never
from a guess — a rule that silently matches nothing looks exactly like no rule.

**The 0–100 score gates are RETIRED (author ruling 2026-08-03: "remove scores; they mean
nothing").** The ≥82/≥85 gates no longer exist; dashboards show open findings instead of
`Node.Score`; nothing writes new scores except an explicitly requested legacy panel run.
**Publish-readiness (docs/LOGIC.md §9, 2026-08-14) is now a five-point convergence gate, not a
single "clean at BLOCKER" snapshot** — a fixed number of sweep rounds was never a real stopping
criterion (five rounds run, a sixth independent round still finding something new was the
observed failure this replaces). A book is complete only when ALL of: (1) zero open
BLOCKER/MODERATE logic-sweep findings, (2) zero open `CONTRADICTED` Story-Ledger claims — widened
2026-09-03 to read the claim rows, the `FACT-LEDGER ` findings AND the `TUNEDREAD ` findings, and
a book whose ledger was **never populated now FAILS** rather than passing silently, (3) two
consecutive independent sweep rounds found zero new
findings — check via `prose --logic-sweep --slug <slug> --until-dry`, not a manual round count,
(4) every fix since the last dry round passed its own automatic blast-radius re-check
(`BlastRadiusService` + `LogicSweepService.RunNarrowAsync`, fires on every beat save), (5) zero
open High/BLOCKER Reader-Proxy QA findings.

The old dual-review machinery (`--review-node` panels, Legion votes, `RunSampledReviewAsync`)
is quarantined behind the SS-A44 gate — **on explicit user request only**. The 1024-persona
library lives in the external MindAttic.Legion package, preserved for other projects.
