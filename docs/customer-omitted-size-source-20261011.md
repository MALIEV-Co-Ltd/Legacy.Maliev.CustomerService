# Customer omitted page size

The original `Maliev.CustomerService.Api/Controllers/CustomersController.cs`
at checkpoint `135e526d0dab85c415b3afdcefd7b70fe2c82e2f` resolves omitted
`size` from the filtered query count. This behavior belongs to root source commit
`5fac706a7983a6d359b39acbd670e6800afe020e`, with no parents. It does not establish
acceptance of that complete original commit or its other paths.

Accepted Customer main `d76e7c79564065aac6f64bbd26ddeab6b4c9ff9e` instead uses
50. This changes the returned cohort and pagination when more than 50 customers
match. The service now preserves omitted size as null until the repository has
counted the filtered query. The repository resolves the count under the existing
1â€“250 bounds. The documented 250-record security adaptation remains distinct
from historical unrestricted default-all behavior. Explicit size and index
normalization, search, ordering, projection, permissions and storage are retained.

Page offsets use long arithmetic and reject an exhausted page before converting
the offset to int. The existing held directory/conditional-update drafts are not
adopted or modified. Empty or exhausted results retain 404.

Thirteen new authored cases reuse the existing Production HTTP, RS256 permission,
PostgreSQL and Redis fixture: three original cohort cases, four authority cases
and six distant-page cases. The cohort cases cover 60/100/300 matches, unrelated
rows, filtered and unfiltered paging, the 250 cap, explicit 25-row consumer pages,
exact pagination keys, joined graph and privacy assertions. Every case preserves
complete stored graph snapshots and independently seeded Redis bytes. Controlled
fixture authority does not establish genuine Auth or joined consumer acceptance.

All original 535 accepted test identities and source test files are retained.
Focused 13 and full 548 are forecasts until real discovery and execution.
Local restore, Release build, focused/full tests, formatting, audit, scaffold,
raw inclusive coverage and causal mutation checks are NOT RUN: no finite isolated
native allocation is available. Source review and the owner's October 10 draft
timing exception waive no validation. Before acceptance require exact-head build
with zero warnings/errors, focused/full execution, strict original identity
retention, unchanged gates and raw Api/Application/Data/Domain coverage floors.
Verify protected main after any authorized merge. No deployment, schema/data,
credential API, source closure or shared tracking change is included.

Hosted gate correction: initial focused run38124644309 passed13/0/0 and strict Release0W/E, but immediate terminal resource observation retained one helper and refused cleanup. Testcontainers4.10 ResourceReaper.cs documents a10-second reconnection grace; terminal observation now waits12seconds inside the unchanged35-second outer expiry, then requires unchanged exact process/container absence. Waiting alone is no cleanup proof; missing/cancelled/remaining receipts still fail. Full run38124644512 stopped on25 whitespace-format findings before548tests/coverage/scaffold; only new snapshot anonymous-object field layout is corrected. Neither original failed run is relabeled accepted.

Refined diagnostic observer: preserve first and optional second exact terminal snapshots, including safe image/explicit reaper labels. At most one12-second observation wait applies only when complete initial/call/census evidence is valid and container candidates remain; native/unknown/invalid evidence fails immediately. The unchanged35-second expiry may cancel a slow observer; no missing or partial receipt passes. Library grace is a hypothesis until actual image/session and second absence receipts corroborate it. No resource is killed or exempted.
