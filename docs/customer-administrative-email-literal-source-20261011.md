# Literal Customer Email with one frozen canonical comparison algorithm

V3 retains literal administrative POST/keyed-POST/PUT/revision-guarded PUT storage/response, private bounded two-match staff lookup ambiguity refusal, and deterministic lowest-ID quotation provisioning for imported duplicates. It replaces the inconsistent .NET invariant-lower/locale-dependent SQL lower pair with ONE frozen algorithm: trim the existing explicit25 whitespace characters, then apply Unicode16.0.0 default SIMPLE case folding using exactly1484 C+S single-scalar mappings from the authoritative CaseFolding.txt. F expansions and Turkic T overrides are excluded; no NFC/NFKC/IDNA or identity policy is applied. Invalid UTF16 comparison input is privately refused rather than aliased to replacement characters. Stored values and response bytes are never rewritten by comparison.

SQL applies parameterized btrim and translate with the same frozen from/to scalar strings; equality is explicitly COLLATE C. Caller comparison parameters and provisioning advisory keys are generated from that same table, not platform ICU/.NET casing tables. The advisory hash also explicitly uses C collation. Normalized-equivalent provisioning requests therefore use the same canonical bytes and transaction-scoped lock key; staff lookup collisions refuse before graph writes, while provisioning selects the lowest matching ID without graph writes. Existing no-match quotation creation and its trimmed literal storage remain unchanged. Administrative writers remain nonunique and do not acquire the provisioning lock: this slice does NOT promise uniqueness or exclusion against concurrent administrative writes, introduce a unique index or reconcile existing data.

Examples of deliberately selected policy: É/é and Σ/ς compare alike, supplementary Deseret uppercase/lowercase compare alike, Cherokee upper/lower and capitalẞ/ß collide. Dottedİ is NOT mapped to i under the excluded Turkic/full policies, dotlessı remains distinct, ß is NOT expanded to ss, and composed É is NOT NFC-equated with E+combining acute. These are explicit PostgreSQL compatibility adaptations, NOT original exact Email equality parity or Auth canonical identity acceptance.

Twenty-seven additional normal Production HTTP/RS256/PostgreSQL18/Redis regressions are authored:16 non-ASCII own-email/equivalent-caller roundtrips across all four writers;5 non-ASCII lookup refusals with deterministic provisioning selections;4 excluded-policy distinctions;2 concurrently ABSENT equivalent provisioning cases (Greek sigma/final sigma and accented Latin). For the latter, an independent fixed canonical oracle holds the exact PostgreSQL advisory key. Both real HTTP requests must be observed waiting on advisory locks in pg_stat_activity before ANY graph mutation; deleted/wrong-key locks fail that assertion. After release, exactly one request creates a graph and the other selects it. Owned transactions/requests are released in finally. Existing46 still cover ASCII/NBSP/tab literals,256/257-boundary failure atomicity, graph/xmin/cache/replay/stale revisions/authority and whitespace selection. Focused73/full494 are forecasts, not executed counts.

Original initial5fac706a7983a6d359b39acbd670e6800afe020e/no parent, f32adc66af8d7ba66fc7b803a7f5291349c11932/parent0a5fdd30094604c72518b849adb7b59f1c98077c and72eb9f1949176392141951d35e6e06f7c30af4c2/parent023aa2f143fa5a8f557d72c1302b9add64792d6a Customer controller/entity/email-lookup path/blob witnesses stay separately retained at checkpoint135e526d0dab85c415b3afdcefd7b70fe2c82e2f. Original lookup exact equality and SingleOrDefault are still distinguished from this deliberate comparison adaptation. No whole-source SHA closure is claimed.

Read-only provider constraints retain accepted main61988b97/run38091250364, actual four baseline UTF8 boundary results and committed Npgsql10.0.3/PG18 fixture/model/source assertions. Historical default database locale/collation is UNAVAILABLE; no native probe was launched. Candidate SQL requires UTF8 and explicit built-in C for comparison/hash and does not call locale-dependent lower. Auth committed read-only consumer witnesses remain separate; its profile-by-ID/raw validation/trimmed identity/own canonical key policies are unchanged and no genuine Auth consumer adoption is claimed. The correction preserves all421 original test identities and all73 authored identities. Of167 original source files,163 remain byte unchanged; CustomerRepository, CustomersController, the single CRUD Email assertion, and the retry test class are explicitly changed. Schema, DTOs, routes, permissions, replay/version semantics, production cache policy, frozen dependencies and directory drafts remain unchanged. Customer RS256 and scaffold proofs remain distinct from the held external Employee/Auth lane.

Unicode data provenance: https://www.unicode.org/Public/16.0.0/ucd/CaseFolding.txt, SHA2566f1f9c588eb4a5c718d9e8f93b782685e5c7fec872cf05e8e6878053599e09bb; redistributed Unicode license is in docs/licenses/Unicode-3.0.txt. Parameterization/composition reference: https://learn.microsoft.com/en-us/ef/core/querying/sql-queries. PostgreSQL translate/btrim and C collation references: https://www.postgresql.org/docs/18/functions-string.html and https://www.postgresql.org/docs/18/collation.html. These references and source parsing are not candidate SQL/runtime validation.

This correction has not run restore/build/focused/full/format/audit/security/scaffold/coverage. The prior head9969bf9 passed strict Release and formatting, then failed49 of494 actual tests. Original raw evidence is retained. No local native allocation exists; independently reviewed coherent source acceptance is required before updating draftPR73. This correction is uncommitted/unpushed pending that review. No issue closure, deployment, persistent-data operation, Auth/IAM change, foreign cleanup or shared Tracking modification occurred. Customer69 and source/consumer acceptance remain held.

## Actual 49-failure reconciliation at head 9969bf9

Hosted run38096941230 passed Release0W0E and formatting, then executed494 cases:
445 passed,49 failed,0 skipped. All421 baseline identities remained, with419
passing and2 failing. The sealed source diagnosis records each49 identity/error.

Original source135e526d0dab85c415b3afdcefd7b70fe2c82e2f stores administrative
Email literally. The baseline CRUD assertion alone now expects request.Email;
all its other graph/cache/timestamp assertions and identity remain. Accepted
provisioning commit04a0179fde70e6a18169ed7266ebc1aec81dffde selects the lowest
ID for imported duplicates; its original regression remains unchanged. Lookup
ambiguity refusal is a distinct consumer policy, not a retirement of provisioning.

Actual raw logs show NpgsqlRetryingExecutionStrategy rejecting the transaction
opened outside the strategy. Complete provisioning now runs inside the strategy
with a fresh disposable context per attempt, including the same canonical lock,
lookup and graph commit. Two authored pre-commit/lost-acknowledgement regressions
require one graph and selection on replay. The independent lock oracle and
both-request wait/drain/finally checks remain unchanged.

Normal accepted GET bypasses Redis. Tests seed independent Redis sentinel bytes
before checking cache preservation. PostgreSQL accepted a257-character email
ending with space and silently truncated it; explicit raw256 UTF16/scalar
validation now rejects it with400 before graph, cache or replay mutation, matching
the existing literal-name boundary. No shared exception mapping was changed.

Focused authored75/full496 are forecasts until exact-head hosted raw validation.
No local native slot is allocated. Original hosted failures remain immutable.
Customer69/consumer adoption/main acceptance/source closure remain held.

The two authored collision method names are retained for exact raw identity continuity; their provisioning assertions now follow the accepted lowest-ID contract, while their lookup assertions still require private ambiguity refusal. The old/current assertion mapping is retained in the correction patch.
