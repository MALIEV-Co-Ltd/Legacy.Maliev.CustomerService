# Legacy.Maliev.CustomerService

This public repository is the sanitized .NET 10 compatibility extraction of the
customer domain from the private `R:\maliev-web` monorepo.

## Non-negotiable boundaries

- Never copy monorepo Git history, connection strings, identity secrets, JWT keys,
  password hashes, service credentials, or generated secret-audit evidence.
- Preserve legacy customer/company/address routes, query names, sort values,
  PascalCase JSON, null omission, named routes, and pagination wire shape.
- CustomerService owns only `Customer`, `Company`, and `Address` data.
  `CustomerIdentity`, credentials, tokens, refresh, revocation, and recovery belong
  to AuthService and must not be proxied through this service.
- Never return `PasswordHash`, `SecurityStamp`, authenticator, recovery, or token data.
- All customer-scoped actions require resource-scoped permissions. List/email lookup
  is staff-only.
- Do not mutate source SQL Server or deploy over the new `Maliev.CustomerService`.

## Service conventions

- Runtime: .NET 10; Scalar/OpenAPI, not Swashbuckle.
- Logging: built-in `ILogger<T>` for actionable failures; never log PII or secrets.
- Data: PostgreSQL projections and bounded queries; no N+1 loading.
- Cache: `legacy:customer:` Redis prefix, short customer-by-id TTL, explicit mutation
  invalidation, and PostgreSQL fallback. Authorization always happens before cache use.
- Deployment is gated to the existing cluster, `maliev-legacy`, and dedicated legacy
  WIF/GitOps resources; no new node pool or paid Cloud SQL.

## Required validation

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format Legacy.Maliev.CustomerService.slnx --verify-no-changes --no-restore
dotnet list package --vulnerable --include-transitive
gitleaks git . --redact=100 --exit-code 0 --no-banner --no-color
```

Contract/auth changes require matching tests for the exact route and JSON shape,
resource templates, identity-boundary exclusion, cache invalidation, and a real
PostgreSQL migration.
