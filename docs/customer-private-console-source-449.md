# Customer private console source adaptation

Original integration source `449d87ea9d1d6ab40f1ec444353ab6aba80a5a76` has parents
`b630d879f2e7ea0be4b35fbf85b9967d0288ad13` and
`29214f53f1574b89799e6d58ea1cebea03fffe04`. Its Customer Program selects
the production metadata-only console formatter and console Warning filter.
The exact Customer Program and Startup blobs equal the second parent; this is
merge integration evidence, not newly authored behavior credited to this merge.

Customer base `c759c05ec801c8521056d66b12e0f6c5c7973f68` selects the general
console formatter through AddServiceDefaults. AddPrivateRequestObservation
selects the request boundary without selecting a console formatter.

This bounded change selects the existing PrivateFailureConsoleFormatter from
the unchanged accepted ServiceDefaults pin
`c40a7f82cea347b949444dcd7fb730f2b8dc3c0e`, after AddServiceDefaults.
The console provider accepts Warning and above. The formatter itself refuses
Trace, Debug and Information even when a category-specific rule enables them.
Arbitrary formatted messages, string state, scopes, exception messages and
stacks remain outside console output.

General log providers are cleared so an arbitrary-message OTLP log sink cannot
bypass this private console contract. Existing OpenTelemetry tracing and metrics
registrations, feature gates and exporters remain registered and unchanged.
This does not establish deployed telemetry transport or cloud log access.

Seven authored cases use the actual Customer Production Program and DI with the
existing disposable PostgreSQL/Redis authority fixture. A bounded test provider
passes actual ILogger entries through the exact DI-selected console formatter.
It does not replace that formatter with a structured-state approximation.
Six severity rows assert exact provider selection, retained tracing/metrics,
fixed severity and allowed event fields, no arbitrary formatter invocation,
and no protected scalar/scope values. One real HTTP case reuses existing fixture
failure routes, checks liveness, anonymous authorization, unchanged private
503/500 responses and single-owner request observations, and compares the
existing physical snapshot plus cached bytes before and after. No new production
or test endpoint is added.

The existing current-pin formatter does not supply complete source runtime
provenance. Source metadata and ordinary exception throw-site delivery remain
shared follow-ons; this proposal neither changes shared code nor advances its
dependency. No outbound HTTP client is invented where Customer registers none.
Startup containment, HTTP/wire/authentication/authorization/health/request
ownership, data/cache writers, packages, workflows and frozen qualifications
remain unchanged apart from the exact reviewed source-pin successor required
by the existing focused proof manifests.

All compiler, Python, focused/full, HTTP, format, static/security/audit and
coverage execution is NOT RUN by this source-only author. The existing 1120
full-suite acceptance is supplied by Root; 7 new cases and 1127 full are
source forecasts until hosted execution. Root owns publication and exact-head
plus protected-main validation. No source-ledger closure, whole449 acceptance,
provider/data work, credentials, deployment or production requests are included.
