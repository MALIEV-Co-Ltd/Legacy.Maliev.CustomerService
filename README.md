# Legacy.Maliev.CustomerService

Public, sanitized .NET 10 compatibility extraction of the old customer domain from
the private `maliev-web` monorepo. It preserves the legacy customer, company,
address, email-lookup, pagination, and safe identity DTO contracts while the new
MALIEV implementation is completed independently.

## Architecture and security boundaries

Dependency direction is `Api -> Application -> Domain`; PostgreSQL, Redis, and
AuthService HTTP adapters live in `Data`. Scalar/OpenAPI, JWT validation, standard
middleware, health endpoints, and structured logging come from
`Maliev.Aspire.ServiceDefaults`.

CustomerService no longer opens or migrates `CustomerIdentity`. Password hashes,
security stamps, authenticator keys, recovery material, sessions, refresh tokens,
and credential validation remain owned by AuthService. The compatibility identity
DTO contains only fields consumed by the legacy UIs.

The old anonymous `/customers/v1/validate` oracle is now service-authorized,
live-checked, critical, and returns the same unauthorized result for unknown users
and invalid passwords. Customer/address/identity routes use resource-scoped
permissions so IAM can enforce customer ownership; staff list and email lookup use
separate permissions.

## Preserved route families

- `/customers[/{id}]` and `/customers?sort=&search=&index=&size=`
- `/customers/{id}/identity[/{password?}]`
- `/customers/{customerId}/addresses[/{addressId}]`
- `/customers/addresses[/{id}]`
- `/customers/companies[/{id}]`
- `/customers/emails/{email}`
- `/customers/v1/validate`

PascalCase JSON, null omission, legacy sort names, named routes, related customer
objects, and the pagination fields `Items`, `PageIndex`, `TotalPages`,
`TotalRecords`, `HasNextPage`, and `HasPreviousPage` remain compatible.

Security corrections intentionally bound list pages to 250 records, require
address/customer association for customer-address reads and deletes, prevent
Identity secret serialization, and remove account-enumerating credential errors.

## Data and caching

- PostgreSQL target: `legacy-postgres-customer` in `maliev-legacy` after parity gates.
- Preserved tables: `Customer`, `Company`, `Address` with legacy `ID`/foreign-key
  column names, field lengths, computed `FullName`, and constraint names.
- Redis prefix: `legacy:customer:`; customer-by-id entries expire quickly and are
  invalidated on customer/address mutations. Cache failure falls back to PostgreSQL.
- Source SQL Server and `CustomerIdentity` remain unchanged throughout extraction.

## Deployment gate

No deployment is performed during service extraction. Cutover requires a dedicated
`legacy-maliev-customer` WIF identity, `maliev-gitops/3-apps/_legacy-customer-service`,
the AuthService legacy customer-identity adapter, data parity/rollback evidence,
and Web/Intranet consumer tests. Everything must run in the existing cluster and
`maliev-legacy` namespace with no new node pool and no Cloud SQL.

The password-in-route identity-create template exists only for legacy compatibility.
The downstream adapter sends the password in a JSON body, never in its URI. The
Intranet caller must migrate to a body-only endpoint before production cutover.

## Validate

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.CustomerService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```
