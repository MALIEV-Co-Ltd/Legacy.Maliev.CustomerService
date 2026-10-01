# Customer ordinary-detail authority — issue #35

## Scope and gate

Initial TEST/DESIGN phase at `dc090542c02d675f54c3be59e33654dc24ecca9e` on
`codex/customer-detail-authority-20261001` owned only the two new CustomerDetailAuthority
fixture/test files and this document. After the terminal full RED and root review,
root authorized only the ordinary detail method and two precisely identified old
cache expectations. This approval supersedes the initial test/design-only scope;
the implementation is bounded below. Schema, Auth, Intranet, ledgers and every
other worktree remain unchanged. No commit/push or
persistent data/schema/provider operations are authorized. All PG/Redis effects
are disposable Testcontainers effects. Issue #20 remains a distinct cross-service
customer profile/identity replay gate. This slice does not close a source owner.

## Exact source and current path

Source mirror checkpoint `bed10c7d15e0698e0b75f1329d0f312937f5d77f`:

| Original committed source | Relevant contract | Current implementation |
| --- | --- | --- |
| `5fac706a7983a6d359b39acbd670e6800afe020e`, `Maliev.CustomerService.Api/Controllers/CustomersController.cs` | Detail GET performs `AsNoTracking` database query including Company, BillingAddress and ShippingAddress | `CustomerRepository.GetCustomerAsync` supplies the equivalent bounded projection, but `CustomerApplicationService.GetCustomerAsync` returns cached detail before querying PG |
| `72eb9f1949176392141951d35e6e06f7c30af4c2`, same controller at latest source checkpoint | Ordinary detail still reads current database graph; no detail cache authority introduced | Ordinary `/customers/{id}` still has cache-first authority |
| `5fac706a7983a6d359b39acbd670e6800afe020e`, CompaniesController/AddressesController | Owned company/address changes persist through the source database | Current application writes PG then removes affected customer-detail keys |

`f32adc66af8d7ba66fc7b803a7f5291349c11932` credential validation is excluded:
credentials/identity authority belongs to AuthService. Completed versioned
customer revisions and DC090 profile transaction repair are not changed.
Lists/email/versioned reads already query PG. The Intranet
`Customers/LegacyCustomerClient.GetCustomerAsync` consumes `/customers/{id}`.

The current AGENTS cache convention explicitly prescribes short-TTL cache hits,
invalidation and PG fallback. Replacing ordinary detail cache authority is an
intentional policy supersession requiring root approval, not a silent convention
rewrite. Root explicitly approved superseding that AGENTS cache-authority
convention for ordinary detail only, with no change to original source semantics.
The source-authority rationale is acknowledged PG writes must be visible
in ordinary detail even if cache transport fails or a delayed old fill arrives.

## Real boundary fixture

- Actual Production `Program` via WebApplicationFactory, registered application,
  repository, retry-enabled Npgsql and real Redis distributed cache. No fake auth
  scheme, fake permission handler, fake repository or fake cache response.
- Normal RS256 signature validation uses ephemeral fixture RSA, fixture-specific
  issuer/audience and exact signed permissions through the unchanged handler.
  These standard noncritical routes need no live-check opt-in. No delete scope,
  wildcard service shortcut, invented IAM grants or human ownership guarantee.
- Disposable PG18 and Redis7.4; migrations apply only to owned disposable PG.
  One class fixture, sequential rows reset independently, two separate app hosts
  sharing actual PG/Redis for the paired-instance tests.
- Independent literal old-writer JSON is seeded through the registered real
  distributed Redis transport. Expected names/values are independent literals,
  not computed through production serialization.
- Invalidation failures use Redis ACL `-del/-unlink`; an explicit direct transport
  `NOPERM` probe demonstrates real command denial before the actual HTTP mutation.
- Late-fill tests inject those old bytes AFTER successful mutation/invalidation.
  Both hosts' real HTTP GETs complete before the paired value assertion. This
  models the legal late arrival independently of a production Set barrier that
  a repair might remove; it does not claim distributed cache fencing.
- Caller cancellation uses a SaveChanges interceptor barrier: wait for real save
  entry, cancel, require actual OperationCanceledException, await server release
  and verify the original PG graph and absent cache. No sleep-based gate.

## Test inventory and failure reach

22 new cases: three failed-invalidation, three paired late-fill, three ordinary
successful-invalidation, four invalid-authority (anonymous/wrong RSA/expired/no
permission), three invalid payload, three missing entity and three caller-abort.
Every authority-denial row covers detail GET and all three mutation routes.
Snapshot controls assert unchanged PG. Wire controls assert PascalCase, omitted
null Mobile, nested Company/Billing/Shipping references, country scalar 764, and
absence of InternalRemark/PasswordHash. No Auth token is placed in DTO/cache data.

Initial genuine RED (retained): 19 total, 6 stale-value assertion failures,
13 passing controls, no skips. All six reached HTTP204 and a separate fresh PG
query proving the committed new value before the old detail value assertion.
The first paired version stopped after its first failed host assertion; it was
strengthened to read both hosts before asserting, without changing expectations.
Three successful-invalidation controls were added, not a weakened RED.

## Validation chronology and exact graph

Private dependency root:
`B:/maliev-legacy/.worktrees/customer-detail-authority-20261001/TestResults/.private`.
Clean local clones are detached at exact CI pins:
Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`;
Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.
All dependency build outputs are under that private root, never sibling outputs.

```powershell
dotnet build Legacy.Maliev.CustomerService.Tests/Legacy.Maliev.CustomerService.Tests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/customer-detail-authority-20261001/TestResults/.private -bl:TestResults/detail-final-red-build.binlog
dotnet test Legacy.Maliev.CustomerService.Tests/Legacy.Maliev.CustomerService.Tests.csproj -c Release --no-build --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/customer-detail-authority-20261001/TestResults/.private --filter FullyQualifiedName~CustomerDetailAuthorityHttpTests --results-directory TestResults/detail-final-red --logger trx
dotnet test Legacy.Maliev.CustomerService.Tests/Legacy.Maliev.CustomerService.Tests.csproj -c Release --no-build --no-restore -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/customer-detail-authority-20261001/TestResults/.private --results-directory TestResults/detail-full-red --logger trx
```

Unchanged baseline Release: 0 warnings, 0 errors; unchanged baseline full: 137
passed, 0 failed/skipped. TRX:
`Legacy.Maliev.CustomerService.Tests/TestResults/natth_MALIEV-31USFIV_2026-10-01_10_19_10_net10.0.trx`.
The shell command had an unrelated trailing nonexistent read-path diagnostic;
the actual test result is independently confirmed in that terminal/TRX, not
inferred from its composite shell exit code.

Initial new focus TRX:
`TestResults/detail-red/natth_MALIEV-31USFIV_2026-10-01_10_24_28_net10.0.trx`.
Final strengthened Release: 0 warnings/errors. A rejected dotnet-format `-p` invocation made
no edits; valid format uses environment MSBuild properties, scoped to new files.

Final strengthened focus: 22 executed, 16 passed, 6 failed, 0 errors/skips/timeouts.
TRX `TestResults/detail-final-red/natth_MALIEV-31USFIV_2026-10-01_10_27_40_net10.0.trx`.
The paired actual values are `[Before, Before]`, `[Before company, Before company]`
and `[Before road, Before road]`; both instance reads executed before assertion.
All six REDs are assertion failures after authenticated HTTP204/committed-PG proof,
not configuration, authentication, database migration or fixture errors.

Final unfiltered full: 159 executed, 153 passed, the same 6 failed, 0 skips.
TRX `TestResults/detail-full-red/natth_MALIEV-31USFIV_2026-10-01_10_28_01_net10.0.trx`.
All 137 original baseline cases are matched by test name and still pass; the
remaining 16 passes are the new controls. This is frozen RED design evidence,
not a passing implementation candidate and not ready for commit.

Static terminal evidence: whole solution format verification exit0; five projects
Api/Application/Data/Domain/Tests have no vulnerable packages; gitleaks dir
controllers (89.70KB) and docs (29.13KB before this evidence append) find no leaks;
tracked `git diff --exit-code` and `git diff --check` exit0. Only the three new
owned files are untracked. Both private dependency clones are clean at the exact
pins. Documentation append receives a final scoped scan/readback before freeze.

```powershell
$env:UseLocalMalievDependencies='true'
$env:MalievWorkspaceRoot='B:/maliev-legacy/.worktrees/customer-detail-authority-20261001/TestResults/.private'
dotnet format Legacy.Maliev.CustomerService.slnx --verify-no-changes --no-restore
foreach($project in @('Api','Application','Data','Domain','Tests')) { dotnet list "Legacy.Maliev.CustomerService.$project/Legacy.Maliev.CustomerService.$project.csproj" package --vulnerable --include-transitive }
gitleaks dir Legacy.Maliev.CustomerService.Tests/Controllers --redact=100 --no-banner --no-color --report-path TestResults/detail-secrets.json
gitleaks dir docs --redact=100 --no-banner --no-color --report-path TestResults/detail-doc-secrets.json
git diff --check
git diff --exit-code
```

## Root-approved bounded runtime repair

Implemented only ordinary `CustomerApplicationService.GetCustomerAsync` delegating
directly to the existing PG projection, without detail-cache reads/fills. Kept all mutation
invalidations/cache adapters for old writers and all existing routes, DTOs, paging,
authorization, versioned/replay/profile transactions unchanged. No schema,
generation/CAS, retries or outbox required for this read-only authority decision.

Root individually approved exactly two historical policy expectation supersessions:
`GetCustomerAsync_CacheHit_DoesNotQueryPostgreSql` now supplies distinct stale cache
and current repository objects, asserts the current PG result/repository call and
zero cache Get/Set; and
`CustomerCrudPostgresTests.CustomerGraph_CRUD_PreservesRelationshipsTimestampsAndCacheBoundaries`
line 38's cache Set verification becomes no Get/Set. Every real
DTO/graph/persistence/timestamp/validation/error assertion remains intact.
No other old tests are edited.

Exact six-file candidate ownership: Application/Services/CustomerApplicationService.cs;
Tests/Application/CustomerApplicationServiceTests.cs;
Tests/Data/CustomerCrudPostgresTests.cs; the two new Tests/Controllers files;
and this document. Retained RED artifacts are not overwritten. After approval,
fresh Release `TestResults/detail-green-build.binlog` is 0 warnings/errors.
Final focused run: 33 passed, 0 failed/skipped. TRX
`TestResults/detail-green-focus/natth_MALIEV-31USFIV_2026-10-01_10_32_00_net10.0.trx`.
The filter combines new CustomerDetailAuthorityHttpTests, all existing
CustomerApplicationServiceTests and the real CustomerCrudPostgresTests graph case.
Final unfiltered run with `--collect:'XPlat Code Coverage'`: 159 executed/passed,
0 failures/errors/skips/timeouts. TRX
`TestResults/detail-green-full/natth_MALIEV-31USFIV_2026-10-01_10_33_05_net10.0.trx`.
All original baseline cases remain present, with only the one reviewed unit name
change; all existing real graph/validation assertions remain.

Unexcluded coverage:
`TestResults/detail-green-full/86ad3cd6-07b0-4411-a6f1-09dd1a2f5693/coverage.cobertura.xml`.

| Assembly | Line % | Branch % |
| --- | ---: | ---: |
| CustomerService.Api | 41.41 | 41.25 |
| CustomerService.Application | 98.91 | 95.23 |
| CustomerService.Data | 97.84 | 90.96 |
| CustomerService.Domain | 100 | 100 |
| pinned ServiceDefaults dependency | 22.08 | 16.36 |
| pinned CompatibilityContracts dependency | 0 | 100 |

No exclusions/threshold changes were introduced. Raw API80 and dependency-wide
coverage are not satisfied/waived by this bounded slice; uncovered code bodies
are not automatically defects. Root independent acceptance remains pending.

Final whole format and diff checks are clean; all five vulnerability audits report
no vulnerable packages. Final gitleaks: requested `-30` scans all 26 available
public commits (461.83KB), Application220.52KB, Tests1.84MB and docs32.04KB with
zero findings, followed by final documentation readback/scan after this evidence
append. Reports are `TestResults/detail-final-{history,application,tests,doc}-secrets.json`.
Normal Production RS256 controls pass (missing bearer, wrong signature, expired
token, missing permissions); no auth middleware/producer/signing policy changed.

Candidate outputs are frozen/released to root after terminal final doc/static
readback. No live build/test processes or further writer work remain. No commit,
push, persistent schema activation, production-derived data, Aspire acceptance,
issue20 closure or broad source-owner completion follows from this lane.

## Root independent acceptance

Root re-read the new fixtures, retained six actual stale-value RED results,
source-authority design and exact three-file runtime/historical-test diff.
Independent Release build passed with zero warnings/errors; focused33 and full159
passed with zero skips. Root artifacts: `TestResults/root-detail-focus/root-detail-focus.trx`,
`TestResults/root-detail-full/root-detail-full.trx`, and unexcluded coverage
`TestResults/root-detail-full/5a3eb644-92e7-43cc-9d57-f9fdf74d2294/coverage.cobertura.xml`.
Root independently parsed API41.41%, Application98.91%, Data97.84%, Domain100%.
Whole formatting, five package audits, whitespace, signing-resource and tracked
current-tree scans passed; the six-file scan examined57,226bytes with zero leaks.

[Customer #36](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.CustomerService/issues/36)
retains the API80/current-route/production-derived Aspire acceptance gap separately.
Issue35 covers only this detail-consistency slice;20 remains the cross-service
identity/profile gate. Protected-main PR and exact-main CI are still required.
No deployment, persistent data/schema writes or full source-owner closure applies.
