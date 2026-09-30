# Additive quotation-profile completion

This capability is producer-first support for Web migration issues Web#148 and
Workflows#234; CustomerService#29 tracks implementation. It does not make the
five Web source commits fully migrated until the consumer is separately merged:

- `c1ae969aef50a17ff7b80636fa6680cbce8df662`
- `7e2f00948db4bef52e465b80018ef1cbfc42e260`
- `e863a3f1c156bcfa664c41d08e0405c10b12553d`
- `371aa2fb4db1e2a52e9a81db687832806b1a8fb0`
- `b2f5e0c1514d7ce886b8f4d9dae059b5ab359b86`

## Wire contract

GET and POST `/customers/{id}/instant-quotation-profile-completion` use the existing
RS256 validation, issuer and audience. Exactly one nonempty `sub`, exactly one
`identity_kind=customer`, and exactly one positive `legacy_database_id` equal to
the route ID are required. Employee/service permissions, posted email, and posted
customer identity are not substitutes. Missing/invalid authentication is 401;
validated but non-owning/ambiguous claims are 403 before database/cache access.
No identity credentials, token minting or new customer-session staff permission
are introduced. Existing routes are unchanged.

GET returns the existing PascalCase public `CustomerResponse`, omitting nulls,
and `Cache-Control: no-store, private`. Its quoted 64-character hex `ETag` hashes
customer/company/billing/shipping IDs and native PostgreSQL xmin values from one
repeatable-read snapshot. No internal remark or identity credential is exposed.

POST requires that exact `If-Match` and a nonzero UUID in canonical D format as
`Idempotency-Key`. The PascalCase JSON request is:

```json
{
  "FirstName": "", "LastName": "", "Telephone": "", "Mobile": "",
  "Company": "", "TaxNumber": "", "TaxBranch": null, "TaxBranchCode": null,
  "Billing": {"Building": "", "AddressLine1": "", "AddressLine2": "", "City": "", "State": "", "PostalCode": "", "CountryId": 764},
  "Shipping": {"Building": "", "AddressLine1": "", "AddressLine2": "", "City": "", "State": "", "PostalCode": "", "CountryId": 764},
  "ShipToBillingAddress": true
}
```

All textual candidates are optional, trimmed, and blank-normalized to null. Name,
phone and company maximum length is 50; address text maximum is 256 (postal code
20); nonempty tax number is exactly 13 digits. Posted email/identity fields have
no mutation authority. Existing nonblank values always win. A newly linked
address requires address line 1, city, state, postal code, and a positive country
ID. Existing incomplete addresses can be filled incrementally.

### Contract 2 readiness and optional source parity

CustomerService#31 extends this producer without changing the GET graph, ETag,
receipt JSON or existing plain-13 tax behavior. An authorized owner GET emits
exactly `X-Quotation-Profile-Completion-Contract: 2` only after the receipt journal
has its seven required nonnull columns/types, an immediate usable unique key for
`(CustomerId, Key)`, and current-role schema USAGE plus table SELECT/INSERT/UPDATE.
Unexpected required columns without defaults also fail readiness. The probe
rejects generated/identity receipt columns and receipt-table row-level
security, whose INSERT/replay semantics cannot be inferred from role grants.
Unexpected CHECK/foreign-key/exclusion/unique constraints or extra unique indexes,
user triggers and rewrite rules are outside the supported PR #30 journal envelope
and also fail closed. Native not-null constraints and ordinary nonunique
performance indexes remain allowed. This does not execute custom database logic
to infer its safety or claim arbitrary-schema write guarantees.
The read-only
catalog query has a five-second cancellation bound and performs no DDL. A missing
or incompatible journal/privilege returns PII-free 503 with no capability header
before graph access. Valid POST repeats this gate before mutation; malformed
preconditions retain their existing 400/428 behavior. Owner and email-claim checks
precede the metadata probe. Successful POST does not require or emit the GET-only
header. Capability proves code/schema readiness, not full production parity.

The consumer must keep enablement false by default and require the exact unique
GET header `2` before quotation persistence. Old producers cannot advertise this
support, and configuration alone is not proof of schema readiness. Deployment or
persistent migration application is not authorized by this producer change.

Optional `TaxBranch` accepts `head-office` or `branch`, case-insensitively after
bounded outer trimming (maximum 20 characters). With `head-office`, omit the code;
with `branch`, supply exactly five ASCII digits in `TaxBranchCode`. Both require
the existing 13-ASCII-digit `TaxNumber`. Head office stores
`<13> (สำนักงานใหญ่)`; branch stores `<13> (สาขาที่ <5>)`. Omit both branch fields
when no tax ID exists. Omitted branch fields still store plain13, never invent a
designation. Arbitrary Thai/English suffix strings, orphan branch fields, wrong
codes and oversized candidates are 400 with no mutation or receipt. Historical
stored raw IDs or suffixes (including one-to-five-digit branch codes) remain
unchanged and authoritative; consumer parsing may left-pad those codes for display
and subsequent typed input. No schema extension is needed: source and producer
Company.TaxNumber columns already allow 256 characters.

The actual Auth issuer emits `email` without inbound claim mapping. After the
existing unique customer-kind/subject/database-owner checks, exactly one plain,
valid email claim of at most 256 characters may fill a blank stored email.
Posted `Email`/identity data never supplies authority; no Auth identity lookup or
token minting is performed. No claim means no fallback and preserves existing
client behavior (including blank stored text). Duplicate, blank, display-name,
malformed, control/whitespace-bearing or oversized claims produce 403 before
database/cache/receipt access. A nonblank stored email always wins, including when
the token carries a different valid email. This copies a validated identity claim,
not a claim of current email verification or recovery policy.

Replay hashes preserve PR #30's exact nine-field normalized JSON projection,
property order and null representation; new optional null keys do not change old
hashes. Typed branch folds into canonical TaxNumber before hashing. New operations
with trusted email hash an explicit version-2 projection containing that email,
so changing it under the same key conflicts (409). A matching legacy digest can
only return its unchanged original receipt before graph mutation: replay never
retroactively adds email to an old committed operation. A later intended email
completion requires a fresh graph/key. This keeps old receipts valid without
reinterpretation, receipt rewriting or a migration. Same normalized designation
and request remain replay-equivalent. A frozen PR #30 digest HTTP regression and
an isolated actual PR #30 binary-to-new-binary PostgreSQL run verify this boundary.

Stored distinct shipping is preserved regardless of `ShipToBillingAddress`.
Stored billing/shipping alias remains an alias. Only missing shipping can select
billing. Changed company/address records referenced by another customer are
copied transactionally with all their populated content retained; only the
authenticated customer's links change. This deliberately improves on source
replacement semantics and prevents cross-owner writes.

Success is 200 with `{"CustomerId":1,"CompletionId":"uuid","Changed":true}`.
No-op is 200 with `Changed=false`. Missing precondition is 428; malformed headers
or invalid/new-incomplete address payload is 400; missing customer is 404; stale
graph is 412; reusing the owner-scoped key with another subject or normalized
payload is 409. These rejections leave no receipt or partial mutation.

The transaction reserves `(CustomerId, Key)`, locks customer then company then
sorted address rows, checks the aggregate revision, fills missing fields, and
commits graph and durable receipt together. Same-subject/same-normalized-payload
replay returns the original receipt even with the old ETag; concurrent identical
requests have one commit. A changed receipt invalidates only the owner's cache
after commit, including on replay. Cache failure may return an error after a
durable commit: retry the same key/body, not a fresh operation.

## Consumer ordering and rollout

The Web consumer must read this trusted graph on GET and reread/merge on POST,
lock populated form fields, and persist its quotation first. Only then may it
complete the profile. Retain the durable quotation reference and stable completion
key/body through profile failure; never retry quotation creation because profile
completion failed. A 412 requires explicit graph refresh/remerge and a new key.

The additive EF migration creates only `QuotationProfileCompletionOperation`;
it does not alter legacy data or xmin columns. The journal intentionally has no
customer foreign key: replay history is retained across customer lifecycle changes
rather than cascaded away. Receipts retain hashes rather than raw profile/subject
payloads; these hashes and customer IDs are pseudonymous, not anonymous, and still
require restricted access. Downgrade is intentionally blocked to avoid erasing
replay history; use a reviewed forward fix. Migration application and producer
deployment require separate approval before consumer enablement; the consumer
must not enable completion until the additive schema is ready. This change
applies migrations only to disposable Testcontainers PostgreSQL during tests.
Receipt retention/cleanup is deliberately not introduced; a later policy must
preserve the agreed retry window. Source ledger completion remains with Workflows.
