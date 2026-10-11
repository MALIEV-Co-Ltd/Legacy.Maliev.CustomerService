# CNC tax-only company compatibility

Tracking: CustomerService issue #16. This is a bounded producer dependency, not completion of the CNC submission workflow.

## Source and implementation mapping

Read-only source checkpoint: `5ac7d045c51194edd9e64d8564f1b726b001be34`.

- `Maliev.Web/Pages/InstantQuotation/CNC-Machining.AuthenticatedProfile.cs`, `PersistMissingProfileValuesAsync`: creates a company when either company name or tax number is present; updates missing company fields without replacing existing values. Its `CreateCompanyAsync` helper is in `CNC-Machining.ProfilePersistence.cs`.
- `Maliev.CustomerService.Api/Controllers/CompaniesController.cs`, create/update: persists supplied company details without rejecting blank names. POST returns 201 through `GetCompany`; PUT returns 204 or 404.
- `Maliev.CustomerService.Data/Database/CustomerContext/Company.cs` and `CustomerContext.cs`: `Name` is non-nullable/required, maximum 256 UTF-16 units. Required database strings still permit an empty string. Null is not a valid source database value.
- Migrated `CompaniesController`: permits a blank name only when tax number is present. Null name and records with neither name nor tax number remain rejected. Registrar alone is not sufficient. Existing nonblank company-name requests remain valid.

The existing PostgreSQL `Company.Name` NOT NULL column permits empty strings. Company create/update now preserve Name literally, including padding; null is not normalized into an invented valid name. Both ordinary and scoped routes reject names over 256 UTF-16 units, malformed UTF-16 and NUL before writes. Tax-only ordinary admission remains intact, while scoped edits still require a nonblank Name. TaxNumber/Registrar semantics, routes, permissions, serialization, schema and cache scope remain unchanged. The focused PostgreSQL regression runs actual migrations, then verifies create/update/read, Thai customer relation, linked customer lookup, NOT NULL metadata, and no pending EF model changes.

## Boundaries preserved

- `/customers/companies` POST and `/customers/companies/{id}` PUT, PascalCase response properties, null omission and named 201 Location remain unchanged.
- Company create permission and update `/companies/{id}` resource-scoped permission metadata are unchanged and frozen by regression tests.
- The HTTP harness executes the real MVC controller, JSON model binding and result serialization with isolated test authentication/policies. Its 401/403 assertions prove short-circuiting at that configured boundary, **not** live IAM or production granular permission evaluation. Existing service authorization suites remain the production-policy regression evidence.
- Null or omitted name still fails MVC validation; empty/whitespace name with a tax number reaches the application service.
- Successful updates invalidate all linked customer cache entries. Existing failed-update behavior is unchanged.
- No tax-number syntax policy, identity endpoint, source SQL runtime, database migration, deployment, data import or production write is introduced.

## Regression evidence

The new HTTP suite first produced four expected failures (tax-only create/update returned 400), with eight other cases passing. After the controller fix, Release build completed with zero warnings/errors and all 17 focused HTTP, direct null-guard, permission-metadata, cache and PostgreSQL cases passed. The first complete suite passed 67 tests without skips. A first fresh-worktree build without project references lacked local dependency outputs; building local projects in dependency order resolved this setup issue without changing shared dependencies.

The initial owned-service coverage measurement was 71.71%; an unchanged baseline was not independently measured. Additional test-only coverage exercises actual customer/address/company CRUD, UTC audit timestamps, PostgreSQL foreign-key protection, existing sort options, missing records, cache invalidation, and real MVC wire/status contracts. The expanded focused run passed 25 tests. Production changes remain confined to company admission; no coverage exclusions or generated-code removal were introduced.

Final expanded Release build: zero warnings/errors. Focused suite: 25 passed. Full suite with `--collect:"XPlat Code Coverage"`: 75 passed, zero skipped, 41 seconds. Owned-service line coverage: 1,511/1,838 (82.21%), counting all CustomerService API/Application/Data/Domain files including generated OpenAPI output; shared dependencies are not presented as owned-service coverage. `CompaniesController`, its admission predicate and all action state machines have 100% line and branch coverage.

Validation commands (worktree requires `MalievWorkspaceRoot=B:/maliev-legacy`; use that environment variable for format/package commands):

```powershell
dotnet build Legacy.Maliev.CustomerService.slnx -c Release --no-restore -p:MalievWorkspaceRoot=B:/maliev-legacy -p:BuildProjectReferences=false
dotnet test Legacy.Maliev.CustomerService.Tests -c Release --no-build --no-restore -p:MalievWorkspaceRoot=B:/maliev-legacy --filter 'FullyQualifiedName~CustomerCrud|FullyQualifiedName~CompanyTaxOnlyHttpTests|FullyQualifiedName~TaxOnlyCompany|FullyQualifiedName~CompanyMutations'
dotnet test Legacy.Maliev.CustomerService.Tests -c Release --no-build --no-restore -p:MalievWorkspaceRoot=B:/maliev-legacy --collect:'XPlat Code Coverage'
dotnet format Legacy.Maliev.CustomerService.slnx --verify-no-changes --no-restore
dotnet list Legacy.Maliev.CustomerService.slnx package --vulnerable --include-transitive --no-restore
gitleaks git . --redact=100 --exit-code 1 --no-banner --no-color
gitleaks dir Legacy.Maliev.CustomerService.Api --redact=100 --exit-code 1 --no-banner --no-color
gitleaks dir Legacy.Maliev.CustomerService.Tests --redact=100 --exit-code 1 --no-banner --no-color
gitleaks dir docs --redact=100 --exit-code 1 --no-banner --no-color
git diff --check
```

CNC authenticated profile completion, protected submission coordination, end-to-end browser verification and production-derived data verification remain separate pending gates.

Company literal-name successor: source candidate only. The new HTTP regressions and changed Name257 expectations have not run build, focused/full tests, format, package audit or raw coverage. Earlier counts above describe prior revisions, not this candidate. Exact Web536 already requires a literal Company acknowledgement; its previously executed producer348 lacked this literal write/UTF-16 guard. A jointly bound hosted producer/consumer validation remains required before end-to-end acceptance.

V2 boundary correction: original SQLServer Unicode MaxLength256 maps to nvarchar(256), counting UTF16 units. Supplementary128 (256 units) fits;129 (258 units) fails. Mixed254Thai+one supplementary fits256 units;255Thai+one fails257. V1 scalar-count acceptance was an unapproved boundary expansion; prior scalar source-clear conclusion is withdrawn. Rune decoding still rejects malformed UTF16/NUL. PostgreSQL schema/capacity is unchanged and does not define migration input authority. Original SQL runtime/collation remains unverified.

Current-main reconciliation (2026-10-11): this source candidate starts at accepted Customer main 5ea2b8c07122613369d87527b1a0b1b85fd7d7cb, whose actual hosted baseline is 548 passed. All original case identities are retained; ten added cases make 558 a forecast only. Build, focused/full tests, formatting, audit and raw coverage for this reconciled tree are NOT RUN. No native or CNC/financial consumer execution is authorized. Exact Web536 head 5fb9870e27e837c11b06975997af22ecccd6da5b previously executed producer34800c5f28df7c0ff2970e90c89e1209ed25fe77: padded/NBSP/address258 cases failed at Company confirmation, while company258 directly returned Completed instead of refusal. Address258 behavior was masked and remains unqualified. Original SQL Server runtime/collation equivalence remains unrun.

Company JSON source correction: ordinary and scoped Name properties use a string-only converter preserving GetString exactly. It translates malformed-Unicode InvalidOperationException into JsonException before model construction, matching the accepted Address exception-translation pattern without copying its primitive coercion. Null admission and blank/tax-only rules remain controller-owned. The existing four-route malformed-surrogate HTTP rows still require actual hosted400 validation; this source finding is not an executed behavioral RED.
