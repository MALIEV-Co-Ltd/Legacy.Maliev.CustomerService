# Customer dormant publisher validation adoption — 2026-10-01

Candidate for independent root review under bounded Customer issue [#38](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.CustomerService/issues/38); no commit or publication performed.

## Bounded contract

Base/main/live-origin observed clean at `3053be58b9e6be4721683af96c5813f418f6099a`; open PR list was empty. This exclusive workspace is `B:/maliev-legacy/.worktrees/customer-publisher-validation-20261001`, branch `codex/customer-publisher-validation-20261001`. Other Customer workspaces and original repositories remain untouched.

The publisher calls accepted Workflows `503e8846390a597c267d2889b33a9c26863389b3` (root verified exact-main CI `36823157482` successful), replacing `73dd7304ffe85ec504389fd7664cc39070b9f148`. Only the publisher job additionally grants `actions: read`, the ceiling needed for reusable exact-SHA main-validation observation. Workflow-level permissions remain exclusively `contents: read`; publisher job permissions are exactly `contents: read`, `actions: read`, `id-token: write`.

`LEGACY_DEPLOY_ENABLED == 'true'` still gates publication; its complementary planned-only job remains unchanged. Nothing sets the variable or activates WIF, registry access, environment approval, deployment, or infrastructure. All eight image/Dockerfile/context/dependency/environment/WIF/account inputs, concurrency, events, Dockerfile and runtime remain unchanged. The CI validation action stays at its existing immutable pin; no CI dependency upgrade occurs.

The accepted producer enforces successful trusted main validation for the exact caller SHA before identity/registry actions, with repeated observations. This caller adoption does not claim atomicity against a GitHub run changing after observation, nor successful real OIDC, registry publication, digest consumption, GitOps, deployment, or Aspire acceptance.

## Independent source records (partial delivery-safety mapping)

Read-only committed source mirror: `B:/maliev-legacy/.artifacts/source-commit-mirror-20260930.git`, checkpoint `bed10c7d15e0698e0b75f1329d0f312937f5d77f`.

| Source full SHA | Customer source paths and behavior | Current bounded disposition |
| --- | --- | --- |
| `00ec830615c15b5e4e227046712247b11df0100f` | `Maliev.CustomerService.Api/deploy.ps1`: fail on external command errors; cleanup/finally and manifest restoration | Accepted producer validation prerequisite plus dormant caller adoption strengthens delivery fail-closed behavior; does not reproduce or execute historical gcloud/kubectl script and is not whole-record closure. |
| `72163e9ae11f39f6579423841a2e20529b986fab` | `Maliev.CustomerService.Api/deploy.ps1`, `deploy-service.ps1`: reliable nonzero status propagation | Shared publisher failure propagation is consumed through its immutable workflow call; no historical wrapper/deployment execution acceptance. Record remains separately scoped. |
| `f8921b1b1d5846eeaff999af10b640011655d1d4` | `Maliev.CustomerService.Api/deploy.ps1`: throwaway rendered manifest, untouched template, cleanup in finally | Current immutable-image publisher does not mutate/render source deployment manifests. Adoption preserves that absence, but does not prove historical manifest deployment parity. Record remains partial. |

No ledger mutation, blanket source-owner closure, source history copying or external deployment occurs.

## Test-first evidence

Private clean detached dependencies, uniquely owned below `TestResults/.private`:

- Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`.
- Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.

Both clones were created with `git clone --no-hardlinks` from canonical dependency repositories; their tracked status remained clean. All dependency and service outputs are inside this workspace and built in Release.

The new `Legacy.Maliev.CustomerService.Tests/Workflows/PublicationDependencyTests.cs` uses the existing YamlDotNet dependency to parse the actual caller workflow, not comments or substring presence. It verifies exact full producer pin, exact minimum permission mappings, both dormant gates, job count and all eight unchanged inputs. Existing tests are unchanged, including the OIDC-scope control and parsed build/CI contract controls.

| Phase | Actual result | Retained evidence |
| --- | --- | --- |
| Unchanged baseline after Release build | 159 passed, 0 failed/skipped | `TestResults/publisher-baseline/baseline.trx` |
| New contract against unchanged publisher | 1 genuine assertion failure on obsolete parsed producer pin, existing OIDC control passed; 0 skips | `TestResults/publisher-red/red.trx` |
| Final fresh Release build | 0 warnings, 0 errors | Terminal command output |
| Focused publisher + existing CI workflow contracts | 14 passed, 0 failed/skipped | `TestResults/publisher-focus/focus.trx` |
| Final unfiltered suite | 160 passed, 0 failed/skipped | `TestResults/publisher-full/full.trx` |

Commands, run from the owned workspace:

```powershell
dotnet build Legacy.Maliev.CustomerService.Tests/Legacy.Maliev.CustomerService.Tests.csproj -c Release -p:UseLocalMalievDependencies=true -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/customer-publisher-validation-20261001/TestResults/.private
dotnet test Legacy.Maliev.CustomerService.Tests/Legacy.Maliev.CustomerService.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~PublicationDependencyTests|FullyQualifiedName~PublishWorkflowPermissionContractTests|FullyQualifiedName~WorkflowContractTests' --logger 'trx;LogFileName=focus.trx' --results-directory TestResults/publisher-focus
dotnet test Legacy.Maliev.CustomerService.Tests/Legacy.Maliev.CustomerService.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=full.trx' --results-directory TestResults/publisher-full
$env:UseLocalMalievDependencies='true'
$env:MalievWorkspaceRoot='B:/maliev-legacy/.worktrees/customer-publisher-validation-20261001/TestResults/.private'
dotnet format Legacy.Maliev.CustomerService.slnx --verify-no-changes --no-restore
& C:/Users/natth/go/bin/actionlint.exe
dotnet list Legacy.Maliev.CustomerService.slnx package --vulnerable --include-transitive
```

Whole formatting and actionlint passed with no diagnostics; all five service projects reported no vulnerable packages. Accepted Workflows signing-resource scanner passed, whole-current-tree credential scanner reported zero findings. Gitleaks redacted scans of `.github`, tests/Workflows, documentation and all 45 reachable history commits passed. `git diff --check` passed. Baseline/final TRX test-name comparison found zero missing original tests. No raw coverage threshold waiver or runtime coverage claim is introduced by this workflow-only slice.

## Five bounded audit dispositions and handoff

- Actions: parsed executable caller contract plus actionlint; CI-main/develop/staging validation workflows still present and unchanged. The skill's modern deployment scaffolding is not applied: explicit legacy/default-off scope supersedes that suggestion.
- API: no routes, DTOs, permissions, auth or wire changes; existing whole suite retained.
- Messaging: no producer/consumer/contracts changed.
- Migrations: no model, migration, readiness or schema changes; no DDL run.
- Performance: no runtime/cache/query/transaction changes or benchmark claims.

Changed files are only the publisher workflow, the new parsed contract test and this evidence document. Root owns tracking, independent acceptance and any future integration. No commits, pushes, GitHub writes, provider requests, persistent-data changes or deployment were authorized/performed.

## Independent root integration review

Root read the complete three-file diff and repository boundaries, then independently
built the exact private test-project graph in Release with zero warnings/errors.
Focus14 and full160 passed without skips; all shared dependencies also built in
Release. TRXs are in `TestResults/root-publisher-focus` and
`TestResults/root-publisher-full`. Whole formatting, actionlint, five transitive
vulnerability audits, scoped secret scanning and whitespace checks passed.
Protected PR and exact-main CI remain required before closing bounded issue38.
