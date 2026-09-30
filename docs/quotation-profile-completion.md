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
  "Company": "", "TaxNumber": "",
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
