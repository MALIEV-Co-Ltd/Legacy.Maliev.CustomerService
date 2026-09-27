# Opt-in customer profile revision contract

Existing `GET /customers/{id}` and `PUT /customers/{id}` keep their legacy JSON,
status, caching, and unconditional-update behavior. They do not provide lost-update
protection. New editors must use the opt-in routes below rather than mixing a
legacy read with a versioned write.

`GET /customers/{id}/versioned` requires the existing customer-read permission
for `/customers/{id}`. It bypasses the short-lived profile cache and obtains the
legacy PascalCase, null-omitting body and PostgreSQL `xmin` revision in one
projection. The response includes a strong quoted eight-digit hexadecimal `ETag`
and `Cache-Control: no-store`. The token is opaque to clients, represents the
Customer row (not independently edited Company or Address rows), and must not be
derived from `ModifiedDate`, whose timestamp precision is insufficient.

`PUT /customers/{id}/versioned` requires the existing customer-update permission
for `/customers/{id}`, the unchanged `UpsertCustomerRequest` JSON, and exactly one
`If-Match` value copied verbatim from the versioned read. Missing precondition
returns `428 Precondition Required`; a malformed, weak, wildcard, or multiple
value returns `400 Bad Request`. An atomic PostgreSQL `UPDATE ... WHERE xmin = ...`
returns `204 No Content` for the winner. A stale token returns `412 Precondition
Failed` without changing any profile field or invalidating cache; a missing
customer returns `404 Not Found`. Even an identical-payload retry with a consumed
token is stale: clients must reload before another edit. A successful write
invalidates the profile cache. Authentication and resource permission checks occur
before token parsing or database access.

The EF migration `MapNativeCustomerXminRevision` updates the model snapshot only.
PostgreSQL already supplies `xmin`; it must never be created or dropped as a
physical column. Apply the no-DDL migration before enabling versioned callers.
The token is valid only against the same PostgreSQL row; clients must treat it as
opaque and reload after any 412. This producer contract does not update the
Intranet BFF or browser, which must propagate the token in a later consumer slice.
