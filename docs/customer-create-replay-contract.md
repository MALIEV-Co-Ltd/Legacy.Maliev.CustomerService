# Keyed customer profile creation

`POST /customers` preserves its legacy request/response JSON, named `GetCustomer`
location, and `legacy-customer.customers.create` permission. Existing requests without an
`Idempotency-Key` remain unchanged. A caller that needs retry safety supplies one
non-empty GUID key for one logical create attempt and repeats both that key and
the same request value after an uncertain response.

The keyed operation is scoped to the authenticated token subject and issuer.
The first successful attempt returns `201 Created`; an exact replay returns the
original `201 Created`, location, and customer response. Reusing a key with a
different request or actor returns `409 Conflict` without creating a profile.
A malformed key returns `400 Bad Request`. Authentication and create permission
are checked on every attempt, including replays.

PostgreSQL commits the customer row and `CustomerCreateOperation` together. Its
primary key serializes same-key requests across service instances. The ledger
holds a request hash and original response, not the request payload or token.
Entries are retained for the life of the compatibility service; do not purge
them without an explicit policy and client retry-window migration. The ledger
is not linked by foreign key to `Customer` so a later customer update or delete
cannot silently change an earlier create result. A caller handling subsequent
AuthService identity creation must reconcile uncertain identity commits before
compensating a profile; this producer contract alone does not provide an
atomic cross-service account transaction.

Deploy the additive migration before enabling keyed callers. Roll back code by
stopping keyed calls; do not drop the ledger, since doing so would permit an
old key to create another profile. A schema rollback requires a reviewed
forward-fix/data-retention decision.
