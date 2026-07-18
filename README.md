# Legacy.Maliev.CustomerService

Public, sanitized .NET 10 compatibility extraction of the old customer domain from
the private `maliev-web` monorepo. It preserves the legacy customer, company,
address, email-lookup, and pagination contracts while the new MALIEV implementation
is completed independently.

## Architecture and security boundaries

Dependency direction is `Api -> Application -> Domain`; PostgreSQL and Redis
adapters live in `Data`. Scalar/OpenAPI, JWT validation, standard
middleware, health endpoints, and structured logging come from
the public `Legacy.Maliev.ServiceDefaults` package/repository. CI source builds also pin the public
`Legacy.Maliev.CompatibilityContracts` repository. Compatibility namespaces remain unchanged, so
this isolation does not alter customer, address, company, permission, DTO, or JSON contracts.

CustomerService neither opens, migrates, nor proxies `CustomerIdentity`. Passwords,
password hashes, security stamps, authenticator keys, recovery material, sessions,
tokens, credential validation, and identity administration are owned exclusively by
`Legacy.Maliev.AuthService`. Customer routes use resource-scoped permissions so IAM
can enforce customer ownership; staff list and email lookup use separate permissions.

## Preserved route families

- `/customers[/{id}]` and `/customers?sort=&search=&index=&size=`
- `/customers/{customerId}/addresses[/{addressId}]`
- `/customers/addresses[/{id}]`
- `/customers/companies[/{id}]`
- `/customers/emails/{email}`

PascalCase JSON, null omission, legacy sort names, named routes, related customer
objects, and the pagination fields `Items`, `PageIndex`, `TotalPages`,
`TotalRecords`, `HasNextPage`, and `HasPreviousPage` remain compatible.

Security corrections intentionally bound list pages to 250 records, require
address/customer association for customer-address reads and deletes, and remove all
identity and credential operations from this domain boundary.

## Data and caching

- PostgreSQL target: the customer logical database in `legacy-postgres-<environment>`
  in `maliev-legacy` after parity gates.
- Preserved tables: `Customer`, `Company`, `Address` with legacy `ID`/foreign-key
  column names, field lengths, computed `FullName`, and constraint names.
- Redis prefix: `legacy:customer:`; customer-by-id entries expire quickly and are
  invalidated on customer/address mutations. Cache failure falls back to PostgreSQL.
- Source SQL Server and `CustomerIdentity` remain unchanged throughout extraction.

## Deployment gate

No deployment is performed during service extraction. Cutover requires a dedicated
`legacy-maliev-customer` WIF identity, `maliev-gitops/3-apps/_legacy-customer-service`,
the AuthService customer-identity API, data parity/rollback evidence, and
Web/Intranet consumer tests. Everything must run in the existing cluster and
`maliev-legacy` namespace with no new node pool and no Cloud SQL.

## Validate

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.CustomerService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```
