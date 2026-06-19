# P2P NoSQL Scaffold Contract Part 02

This file preserves ordered scaffold-contract content split from `p2p-nosql-scaffold-contract.md` so GitHub API publishing can avoid large single-file payload limits.

- admin readiness checks

Final ACK, revocation, lease, outbox, and readiness traffic must not share an unbounded queue with upload or repair streams.

## First Crash And Chaos Fixtures

These fixtures are beta blockers for service glue:

- head crash after one fsynced replica
- late ACK after reservation expiry
- late ACK after delete epoch bump
- revoked-node final result
- interrupted repair conversion
- metadata pause and recover
- restore with outbox lag
- temp disk full during upload
- repair reserve exhausted
- orphan cleanup under critical capacity
- agent-local SQLite manifest replay after crash
- task cancellation after fsync and before final result publication
- lock-held-across-await check in service code
- bounded queue overflow under repair pressure
- test-controlled clock skew for leases and envelope expiry

## Scaffold Validation Task

The first implementation task is `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract`. It should run before any service project is accepted and should be cheap enough for every local build, CI job, and pre-PR check.

### Ownership

The validation task belongs to `Hedgehog.Xtask` but must treat `Hedgehog.Types` as the source for executable labels once the workspace exists. Until generated .NET enums exist, the labels in this contract are the seed fixture.

The task should fail closed: missing files, unreadable manifests, unknown workflow names, missing fixture names, or parse failures are validation failures. A skipped check is allowed only behind an explicit `--allow-missing-scaffold` flag for the very first empty workspace bootstrap.

### Inputs

The task reads:

- `Hedgehog.sln`
- `src/**/*.csproj`
- `tools/**/*.csproj`
- `migrations/**/*.sql`
- `tests/**/*`
- `src/**/*`
- `dashboards/**/*`
- `admin/**/*`
- `fixtures/**/*`
- `p2p-nosql-scaffold-contract.md`
- `p2p-nosql-implementation-contract.md`

Implementation should parse TOML, SQL migrations, and .NET code with real parsers where practical. Plain text scanning is acceptable only for metric label strings, fixture names, dashboard JSON, and the temporary pre-scaffold markdown seed.

### Checks

| Check | Failure condition | First implementation approach |
| --- | --- | --- |
| `labels.canonical` | .NET enums, SQL accepted values, metric labels, admin filters, dashboard variables, or fixture names use implementation-state labels outside the canonical lowercase set | Seed a canonical-label table in `Hedgehog.Xtask`, then replace it with `Hedgehog.Types` generated metadata |
| `labels.uppercase_quarantine` | Uppercase pre-contract states from `p2p-nosql-replication-repair-state-machine.md` appear in code, migrations, metrics, admin filters, dashboards, or fixtures | Maintain a denylist from the old state-machine document and report the exact file and token |
| `deps.direction` | A project imports a forbidden owner or service project bypasses the owner project named in the ownership map | Parse project files and direct `ProjectReference`/`PackageReference` entries; later add a solution graph check |
| `metadata.workflows` | Metadata mutation APIs are exposed without one of the named workflow identifiers | Require public metadata-sqlite mutation modules or functions to carry a workflow name from the matrix |
| `metadata.sql_scope` | Service projects contain raw SQL mutation strings or depend on `Microsoft.Data.Sqlite` without being `Hedgehog.Metadata.Sqlite`, migrator, or test-only harness | Scan dependencies first, then scan source for `UPDATE`, `INSERT`, and `DELETE` markers outside allowed projects |
| `fixtures.present` | Any first crash or chaos fixture is missing from `fixtures/` or the named project test path | Require one manifest entry per fixture with owner project, scenario name, and beta-blocker flag |
| `cache.api` | Authority-sensitive code exposes raw cached authority records to mutation workflows | Require cache decision helpers to return `AuthorityCacheDecision<T>` and forbid raw cache modules in head mutation paths |
| `pressure.policy` | Repair, cleanup, and write admission tests do not include every capacity pressure label | Require test or fixture names for `normal`, `pressure`, `critical`, and `emergency` in the pressure-ordering owner |
| `recovery.gates` | Readiness output lacks one of the named recovery gates | Require admin/status schema or fixture labels for every gate in the readiness table |
| `runtime.guardrails` | Service projects use unbounded channels, blocking disk APIs on async paths, or task spawns without supervision markers | Start as a source scan with allowlisted wrappers; graduate to lints once wrappers exist |

### Output Contract

The validator should print grouped failures with stable check IDs, for example:

```text
labels.uppercase_quarantine: src/Hedgehog.Repair/State.cs used REPAIRING
metadata.sql_scope: src/Hedgehog.Head/Write.cs contains UPDATE outside Hedgehog.Metadata.Sqlite
fixtures.present: missing beta fixture "late ACK after delete epoch bump"
```

CI should treat any failure as blocking. The local command should also support `--json` for editor integration and future admin-dashboard display of scaffold readiness.

### Bootstrap Sequence

1. Add `Hedgehog.Xtask` with hardcoded contract seed data and parser tests.
2. Add empty project manifests for the owner projects in the ownership map.
3. Add fixture manifest stubs under `fixtures/scaffold/`.
4. Make `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract` pass on the empty scaffold.
5. Add `Hedgehog.Types` canonical label metadata and switch the validator away from markdown-derived labels.
6. Add CI so service projects cannot land without the validator.

The key constraint is ordering: the validator may start with hardcoded seed data, but service projects must not start with hardcoded labels. Once `Hedgehog.Types` exists, labels flow from it into SQL tests, metrics labels, admin filters, dashboard variables, and fixture names.

## Validator Seed And Fixture Manifest Contract

This slice defines the first machine-readable contract that `dotnet run --project tools/Hedgehog.Xtask -- validate-scaffold-contract` consumes. It deliberately keeps v1 small enough to implement before service projects exist while still proving pass and fail behavior.

### `Hedgehog.Types` Label Metadata

`Hedgehog.Types` owns a static label registry once the project exists. Until then, `Hedgehog.Xtask` may carry the same data as seed TOML or .NET constants, but the shape should already match the future API.

Recommended public shape:

```csharp
public enum LabelDomain
{
    Object,
    ObjectVersion,
    Replica,
    Lease,
    RepairJob,
    Reservation,
    CapacityPressure,
    DegradedMode,
    Node,
    Invitation,
    AuditDecision,
}

public sealed record LabelSpec(
    LabelDomain Domain,
    string Wire,
    string DotNetMember,
    string SqlValue,
    string MetricLabel,
    string AdminFilter,
    string FixtureSlug,
    string Display);

public static IReadOnlyList<LabelSpec> LabelSpecs { get; }
public static IReadOnlyList<LabelSpec> LabelsFor(LabelDomain domain);
public static LabelSpec? LookupLabel(LabelDomain domain, string wire);
```

Rules:
- `wire`, `sql_value`, `metric_label`, `admin_filter`, and `fixture_slug` are lowercase stable strings.
- `DotNetMember` is the only PascalCase field and is never emitted to SQL, metrics, dashboards, logs, or fixture names.
- `display` is presentation-only and must not be parsed back into workflow code.
- Domains that reuse words, such as `normal` in capacity pressure and degraded mode, must be validated with domain context.
- Adding, renaming, or removing a label requires updating `Hedgehog.Types` tests, SQL accepted-value tests, fixture manifest coverage, and admin or dashboard filter tests in the same change.

The first `Hedgehog.Types` tests:

```text
