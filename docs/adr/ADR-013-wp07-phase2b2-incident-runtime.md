# ADR-013: WP-07 Phase 2B2 incident runtime

Status: Accepted; documentation-frozen; runtime implementation unstarted
Date: 2026-09-18 (Asia/Bangkok)

Related records:

- [WP-07 contract and policy design](../api/wp07-dashboard-contract-design.md)
- [Implementation status](../implementation-status.md)
- [Requirements traceability](../requirements-traceability.md)
- [ADR-012 WP-06 status and incident policy](ADR-012-wp06-ua01-status-incident-policy.md)

## Context

WP-07 Phase 2B2 Commit 1 froze the incident read and action contracts. Commit 2
verified the additive human-principal and incident-concurrency foundation at
`444f5997b6030896f072f290a20a9153453ba371`. Commit 3 freezes the runtime design
only. It does not materialize production source, tests, a migration, model-snapshot
changes, generated OpenAPI, a build, or verification evidence.

WP-06 remains the owner of result-driven status processing, automatic incident
opening, automatic confirmed-recovery resolution, immutable engine lifecycle rows,
and notification-suppression context. This ADR defines the WP-07 public incident
read/action boundary around those records. It does not change WP-06 behavior or
reimplement the status engine.

## Decision

### 1. Frozen incident route set

Exactly these eight incident routes are frozen for the later runtime slice:

| Operation | Authorization | Frozen behavior |
| --- | --- | --- |
| `GET /api/v1/incidents` | `incidents.read` | Cursor-paged incident list; filters are site, device, Probe, status, and opened-at range; default order is `openedAt DESC, incidentId DESC`; no aggregate ETag. |
| `GET /api/v1/incidents/{id}` | `incidents.read` | Incident detail; a strong opaque ETag is returned in the `ETag` header and `If-None-Match` is supported. |
| `GET /api/v1/devices/{id}/incidents` | `incidents.read` | Cursor-paged incident history for one Device; status and opened-at filters; default order is `openedAt DESC, incidentId DESC`; no aggregate ETag. |
| `GET /api/v1/incidents/{id}/lifecycle-events` | `incidents.read` | Cursor-paged immutable lifecycle union containing WP-06 engine events and WP-07 public action events; default order is occurrence time descending. |
| `GET /api/v1/incidents/{id}/comments` | `incidents.read` | Cursor-paged immutable incident comments; default order is creation time descending. |
| `POST /api/v1/incidents/{id}/acknowledge` | `incidents.operate` | Acknowledges an `Open` incident with `AcknowledgeIncidentRequest`; success is `200 IncidentActionResponse`. |
| `POST /api/v1/incidents/{id}/comments` | `incidents.operate` | Adds an immutable comment with `AddIncidentCommentRequest`; success is `201 IncidentCommentResponse`. |
| `POST /api/v1/incidents/{id}/resolve` | `incidents.operate` | Manually resolves an active incident with `ResolveIncidentRequest` only when the current underlying Probe status is neither `Down` nor `Recovering`; success is `200 IncidentActionResponse`. |

The route parameters are canonical lowercase UUID-D values. The existing DTOs in
`EePulse.Contracts.Dashboard` remain the wire-shape source of truth:
`CursorPage<T>`, `IncidentListFilter`, `IncidentResponse`,
`IncidentLifecycleResponse`, `AcknowledgeIncidentRequest`,
`ResolveIncidentRequest`, `AddIncidentCommentRequest`, `IncidentActionResponse`,
and `IncidentCommentResponse`. No new response property exposes an ETag or
`rowVersion`.

`IncidentCommentResponse.AuthorId` is required and non-null, matching required
`incident_comments.author_id`; it always exposes an internal surrogate UUID.
`IncidentLifecycleResponse.ActorId` is nullable only because WP-06 engine rows
have no human actor. Every WP-07 `incident_lifecycle_actions.actor_id` is
required and non-null. No response exposes issuer or subject.

`incidents.read` remains available to Viewer, Operator, Engineer,
Administrator, and Auditor. `incidents.operate` remains available only to
Operator and Administrator. The existing role matrix is unchanged; a site
filter is a data filter and is not a new authorization boundary.

#### 1.1 Total HTTP and pre-database precedence

The following order is deterministic for all eight routes and is part of the
contract:

1. ASP.NET routing and route matching occur first. A structurally unmatched
   route returns the frozen `404` behavior without endpoint validation or
   database work. The established sensitive-route correlation middleware emits
   the safe server correlation boundary where applicable; no caller-supplied
   correlation value becomes authoritative.
2. For a structurally matched protected endpoint, the server creates/reuses its
   safe server correlation ID at the established post-routing boundary, then
   authentication runs before endpoint input processing.
3. Authorization runs before query/header/body validation and before body
   parsing. An unauthenticated or forbidden matched request therefore returns
   only the frozen authentication/authorization result, discloses no detailed
   validation failure, and performs zero database commands.
4. Only after `incidents.operate` authorization succeeds for a command route,
   invoke `PrincipalIdentityResolver.TryResolve(HttpContext.User, out identity)`
   once. It must return one `PrincipalIdentity` from exactly one `iss` claim
   and exactly one `sub` claim; the principal must be authenticated, must not
   use `AgentContract.CredentialAuthenticationScheme`, and both values must
   pass `IncidentActorIdentityContract.HasValidIssuerAndSubject`: non-empty,
   not whitespace-only, already `.Trim()`-stable, valid UTF-16 scalar
   sequences, at most 512 Unicode scalars each, and containing no `Control`,
   `LineSeparator`, or `ParagraphSeparator` scalar. Claims are case-sensitive
   and are neither normalized nor replaced by a fallback. A false result is
   `403` with code `invalid-incident-actor-identity` and title `Incident actor
   identity is invalid`; it exposes no claim detail, logs no claim value,
   issues no database command, and creates no principal. Read routes omit this
   command-only step. A denied role returns its frozen authorization result
   before this resolver is invoked.
5. Only for a command with a resolved identity, or an authorized read, reject
   forbidden or unrecognized query-string keys first. A route that accepts no
   query rejects any query; collection routes accept only their frozen filter,
   paging, and sort keys.
6. Validate the canonical route value not already enforced by structural
   matching.
7. On command routes, validate the required Idempotency-Key presence, single
   value, length, and canonical lowercase UUID-D syntax. An absent header is
   exactly `400 invalid-idempotency-key`; empty, malformed, or repeated values
   return that same response. Read routes omit this step without changing later
   relative order.
8. On command routes, validate required `If-Match` syntax. Incident detail
   instead validates applicable `If-None-Match` syntax at this header step; the
   other read routes omit conditional-header validation. Header syntax here is
   pre-database; current/stale ETag comparison remains inside the locked command
   transaction as already frozen.
9. On command routes, validate content type and then perform the following
   staged JSON/body read before constructing any DTO. First decode the complete
   request-body byte sequence with exactly
   `new UTF8Encoding(false, true)`: no BOM is emitted or added, and invalid
   UTF-8 byte sequences throw rather than being replaced with U+FFFD. Next,
   parse JSON grammar and every JSON string escape without a replacement
   fallback. A `\uD800` through `\uDBFF` escape is valid only when the
   immediately following six source bytes are `\u` plus one `\uDC00` through
   `\uDFFF` escape; the pair represents one supplementary Unicode scalar.
   A standalone low-surrogate escape, a high-surrogate escape followed by any
   other byte sequence (including a non-low escape, a truncated or malformed
   escape, or string termination) is invalid JSON. No JSON reader may
   materialize an invalid byte or escape sequence as U+FFFD. A literal U+FFFD
   supplied as valid UTF-8 remains that distinct valid scalar. This staged read
   completes before DTO construction, DataAnnotations, trimming,
   fingerprinting, transaction creation, or database access. Any failure
   returns the existing safe `400 invalid-json` Problem Details with title
   `invalid-json` and detail `The request body is invalid.`; the response does
   not echo request bytes, escape text, scalar values, parser exceptions, or a
   stack. It retains the safe server correlation boundary, logs no body text,
   and performs zero database commands and zero mutations. Read routes omit
   this step.
10. Validate DTO/data annotations, including `[Required]` and the raw
    1–2,000 UTF-16-code-unit command-text bound. `null`, empty, and
    whitespace-only raw command text fail this raw `[Required]` stage with
    `400 invalid-incident-text`; no trimming occurs for those failures.
11. Normalize successfully validated command text with `.Trim()`. As a
    defensive semantic check, reject an empty normalized value and reject an
    invalid UTF-16 sequence or any normalized scalar in U+0000–U+001F,
    U+007F–U+009F, U+2028, or U+2029 with `400 invalid-incident-text`.
    The check runs over decoded scalar values, so JSON escapes such as
    `\u0000`, `\u0085`, `\u2028`, and `\u2029` are rejected here before any
    provider encoding or database command. Read routes omit this step.
12. Only after every applicable earlier step succeeds may the endpoint begin a
    transaction or issue any database command.

When multiple failures coexist, the response exposes only the earliest failure
in this order. Every pre-database failure preserves safe correlation and logging,
does not log query/body/header secrets or identity claims, leaks no later-stage
validation detail, and executes zero database commands and zero mutations.

Commit 3A must not use automatic typed-body binding that parses JSON before the
command identity, query, route, and header stages above. Command endpoints
accept the authorized request context and explicitly perform the strict staged
content-type/body read before DTO construction and DTO validation in this order.
Malformed UTF-8 and malformed surrogate escapes are therefore not first
detected by DTO deserialization. Future combination coverage includes
unauthenticated and forbidden requests with malformed route/query/header/body
input; authorized requests with invalid issuer/subject claims plus malformed
input; and valid-identity requests carrying simultaneous invalid query, route,
Idempotency-Key, If-Match, content type, JSON, raw DTO text, and normalized
text. Every case must return only its earliest frozen error with zero database
commands.

Cursor pages default to 50 items and accept at most 200. The incident-list
filters are `siteId`, `deviceId`, `probeId`, `status`, `openedFrom`, and
`openedTo`; time bounds are inclusive UTC instants and `status` uses only
`Open`, `Acknowledged`, or `Resolved`. A cursor is bound to the normalized
filter set and sort, so changing either returns `400 cursor-filter-mismatch`.
An absent cursor begins the query. A missing incident or Device returns `404`;
validation, authentication, authorization, and dependency errors use the
existing safe Problem Details boundary.

### 2. Incident representation, time, and text invariants

Incident detail uses the existing nullable `TotalDowntimeSeconds` field. For a
resolved incident it is the floor of the duration over the half-open interval
`[openedAt, resolvedAt)`:

```text
floor((resolvedAt - openedAt).TotalSeconds)
```

`OpenedAt` and `ResolvedAt` must be UTC instants. A resolved incident requires a
resolved instant; `resolvedAt < openedAt` is invalid persisted state and is
rejected, never clamped. Zero is valid. Open and Acknowledged incidents return
`null`. Active duration is never calculated from request time or server time.
Acknowledgements, comments, occurrences, maintenance, and visible-status
overlays do not pause or reset this interval.

For `AcknowledgeIncidentRequest.Comment`, `ResolveIncidentRequest.Note`, and
`AddIncidentCommentRequest.Comment`, validation preserves the unchanged DTO
metadata and uses this exact order:

1. Deserialize the JSON string without trimming it.
2. Apply the existing `[Required, StringLength(2000, MinimumLength = 1)]`
   contract to the raw .NET string. Its raw `string.Length` must be 1–2,000
   UTF-16 code units.
3. Only after the raw DTO value passes that bound, apply .NET `String.Trim()`.
4. Reject the normalized value when it is empty.
5. Persist only the normalized value. Trimming cannot increase `string.Length`,
   so the persisted value necessarily remains at most 2,000 UTF-16 code units.

Whitespace-only raw input fails `[Required]` during DTO validation; it never
reaches `.Trim()`. Raw input longer than 2,000 UTF-16 code units fails DTO validation even when
trimming would reduce it to 2,000 or fewer. Leading or trailing padding counts
toward the raw maximum. No Form-C normalization, case folding, or other Unicode
normalization is performed. The same raw-then-normalized rule applies to the
general comment, acknowledgement comment, and manual-resolution note. Existing
persisted incident text and new comment/action rows retain the exact normalized
database checks below; the database does not accept untrimmed values. The exact
helpers and checks are the authoritative direct-write boundary; no substitute
is permitted.

### 2.1 UTF8 migration preflight

The four-byte UTF-8/UTF-16 relationship used below is valid only after the
migration has proved the database server encoding. The future migration runs
this exact statement as its first Up operation, inside the same non-suppressed
EF migration transaction as every later Commit 3A schema operation:

```sql
DO LANGUAGE plpgsql $wp07_require_utf8$
BEGIN
    IF pg_catalog.current_setting('server_encoding') COLLATE "C"
       <> 'UTF8' COLLATE "C" THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'WP07 incident runtime requires UTF8 server encoding.',
            CONSTRAINT = 'ck_wp07_incident_runtime_server_encoding_utf8';
    END IF;
END;
$wp07_require_utf8$;
```

SQLSTATE is exactly `55000`; the safe message and diagnostic constraint identity
are exactly those shown. The statement precedes creation or evaluation of
`public.wp07_dotnet_trim`, `public.wp07_dotnet_utf16_code_units`,
`public.wp07_incident_text_is_valid`, every new table, and every constraint that
calls a helper. A mismatch aborts the migration transaction, so no Commit 3A
helper, table, constraint, trigger, or other schema object is partially created.
Migration coverage must prove successful continuation on UTF8 and the fixed
failure diagnostics plus zero Commit 3A schema objects on a non-UTF8 database.
No documentation or implementation may use the four-byte counting algorithm
without this checked prerequisite.

The non-UTF8 case uses a separate disposable PostgreSQL database fixture named
`wp07_non_utf8_fixture`. Before the test run, the fixture provisioner connects
to the server's `postgres` database and executes exactly:

```sql
CREATE DATABASE wp07_non_utf8_fixture
WITH TEMPLATE template0
ENCODING 'LATIN1'
LC_COLLATE 'C'
LC_CTYPE 'C';
```

The ordinary UTF8 integration database is never altered, recreated, or
re-encoded for this case. The fixture is not shared with any other test. Its
exact lifecycle is:

1. The disposable PostgreSQL Testcontainer uses its `postgres` administrative
   role. Its administrative connection targets the `postgres` maintenance
   database as `postgres`; before setup, it executes
   `SELECT rolcreatedb FROM pg_catalog.pg_roles WHERE rolname = 'postgres';`
   and requires the one returned value to be `true`. This role executes the
   create below and therefore owns `wp07_non_utf8_fixture`, giving it authority
   to drop that database.
2. With no active or ambient database transaction, execute the exact
   `CREATE DATABASE` statement above through a direct administrative command.
   The command is not enlisted in EF, an `NpgsqlTransaction`, a
   `TransactionScope`, or a migration transaction.
3. Build every connection string whose database is
   `wp07_non_utf8_fixture` with Npgsql `Pooling=false`. Every fixture-targeting
   DbContext, command, transaction, data source, and direct Npgsql connection
   uses a connection or data source built from one of those strings.
4. Run the migration-boundary assertion only against that fixture: execute the
   Commit 3A migration Up transaction, assert SQLSTATE `55000`, the fixed
   message and constraint identity, and query its catalogs to prove no Commit
   3A helper, table, constraint, function, or trigger exists.
5. Dispose every fixture-targeting DbContext, command, transaction, data
   source, and Npgsql connection.
6. Using the administrative connection, assert exactly zero rows from
   `pg_catalog.pg_stat_activity` whose `datname` is
   `wp07_non_utf8_fixture`.
7. Open a separate administrative connection targeting `postgres`, never the
   fixture database. With no active or ambient database transaction, execute
   `DROP DATABASE wp07_non_utf8_fixture;` through a direct administrative
   command. The command is not enlisted in EF, an `NpgsqlTransaction`, a
   `TransactionScope`, or a migration transaction.
8. Require that drop to succeed. Cleanup failure fails the test; it is neither
   ignored nor converted into a skip.

Once creation succeeds, fixture cleanup runs after both the expected migration
rejection and any setup or assertion failure. Future coverage proves the
administrative role capability, transaction-free create/drop, successful setup,
the expected UTF8-preflight rejection, successful teardown, and the absence of
the disposable database after each path. This is the required isolated
mechanism; PostgreSQL database encoding is never mutated in place.

### 2.2 Migration-owned PostgreSQL text helpers

The future additive migration creates exactly these schema-qualified functions.
Every function is `STRICT`, `IMMUTABLE`, and `PARALLEL SAFE`, and fixes
`search_path` to `pg_catalog`. Every body uses only schema-qualified
`pg_catalog` built-ins, explicit bytewise/`COLLATE "C"` comparison where text
equality is required, and explicit UTF-8/scalar operations. None consults the
database or server locale, the active database collation, or PostgreSQL POSIX
character classes. PostgreSQL's default `btrim` is prohibited here.

The exact .NET `String.Trim()` set is:

- U+0009–U+000D
- U+0020
- U+0085
- U+00A0
- U+1680
- U+2000–U+200A
- U+2028
- U+2029
- U+202F
- U+205F
- U+3000

`public.wp07_dotnet_trim(value text)` iterates PostgreSQL characters/scalars
from both ends. A character is removed only when its one-scalar text value is
found in the explicit set above under `COLLATE "C"`; interior characters are
never changed. Its exact migration definition is:

```sql
CREATE FUNCTION public.wp07_dotnet_trim(value text)
RETURNS text
LANGUAGE plpgsql
STRICT
IMMUTABLE
PARALLEL SAFE
SET search_path = pg_catalog
AS $function$
DECLARE
    first_character integer := 1;
    last_character integer := pg_catalog.char_length(value);
    trim_set text :=
        pg_catalog.chr(9) || pg_catalog.chr(10) || pg_catalog.chr(11) ||
        pg_catalog.chr(12) || pg_catalog.chr(13) || pg_catalog.chr(32) ||
        pg_catalog.chr(133) || pg_catalog.chr(160) || pg_catalog.chr(5760) ||
        pg_catalog.chr(8192) || pg_catalog.chr(8193) || pg_catalog.chr(8194) ||
        pg_catalog.chr(8195) || pg_catalog.chr(8196) || pg_catalog.chr(8197) ||
        pg_catalog.chr(8198) || pg_catalog.chr(8199) || pg_catalog.chr(8200) ||
        pg_catalog.chr(8201) || pg_catalog.chr(8202) || pg_catalog.chr(8232) ||
        pg_catalog.chr(8233) || pg_catalog.chr(8239) || pg_catalog.chr(8287) ||
        pg_catalog.chr(12288);
BEGIN
    WHILE first_character <= last_character
      AND pg_catalog.strpos(
            trim_set COLLATE "C",
            pg_catalog.substr(value, first_character, 1) COLLATE "C") > 0
    LOOP
        first_character := first_character + 1;
    END LOOP;

    WHILE last_character >= first_character
      AND pg_catalog.strpos(
            trim_set COLLATE "C",
            pg_catalog.substr(value, last_character, 1) COLLATE "C") > 0
    LOOP
        last_character := last_character - 1;
    END LOOP;

    IF first_character > last_character THEN
        RETURN '';
    END IF;
    RETURN pg_catalog.substr(value, first_character,
                             last_character - first_character + 1);
END;
$function$;
```

PostgreSQL `text` contains valid Unicode scalar values encoded as UTF-8. The
exact UTF-16 relationship is one code unit for a scalar encoded in one, two, or
three UTF-8 bytes, and two code units for a scalar encoded in four UTF-8 bytes.
`public.wp07_dotnet_utf16_code_units(value text)` iterates PostgreSQL
characters/scalars, not bytes, and uses this exact expression:

```sql
CREATE FUNCTION public.wp07_dotnet_utf16_code_units(value text)
RETURNS integer
LANGUAGE sql
STRICT
IMMUTABLE
PARALLEL SAFE
SET search_path = pg_catalog
AS $function$
    SELECT pg_catalog.char_length(value)
         + (
             SELECT pg_catalog.count(*)::integer
             FROM pg_catalog.generate_series(
                      1, pg_catalog.char_length(value)) AS characters(ordinal)
             WHERE pg_catalog.octet_length(
                       pg_catalog.substr(value, characters.ordinal, 1)) = 4
           );
$function$;
```

Thus empty text returns zero, BMP scalars contribute one, four-byte
supplementary scalars contribute two, and no normalization is performed.

`public.wp07_incident_text_is_valid(value text)` is `STRICT`, so callers use it
only for persisted non-null values or explicitly allow `NULL` in a surrounding
check. Its exact check is bytewise/C equality with the trim result, a UTF-16
count between 1 and 2,000 inclusive, and an explicit scalar-code-point
restriction. The restriction rejects the already-frozen `Control`,
`LineSeparator`, and `ParagraphSeparator` categories: U+0000–U+001F,
U+007F–U+009F, U+2028, and U+2029. It does not use a locale-sensitive or POSIX
character class and does not reject any other Unicode scalar.

```sql
CREATE FUNCTION public.wp07_incident_text_is_valid(value text)
RETURNS boolean
LANGUAGE sql
STRICT
IMMUTABLE
PARALLEL SAFE
SET search_path = pg_catalog
AS $function$
    SELECT value COLLATE "C" = public.wp07_dotnet_trim(value) COLLATE "C"
       AND public.wp07_dotnet_utf16_code_units(value) BETWEEN 1 AND 2000
       AND NOT EXISTS
           (
               SELECT 1
               FROM pg_catalog.generate_series(
                        1, pg_catalog.char_length(value)) AS characters(ordinal)
               WHERE pg_catalog.ascii(
                         pg_catalog.substr(value, characters.ordinal, 1))
                         BETWEEN 0 AND 31
                  OR pg_catalog.ascii(
                         pg_catalog.substr(value, characters.ordinal, 1))
                         BETWEEN 127 AND 159
                  OR pg_catalog.ascii(
                         pg_catalog.substr(value, characters.ordinal, 1))
                         IN (8232, 8233)
           );
$function$;
```

The `ascii(substr(..., 1))` calls operate on one PostgreSQL character/scalar
under the migration-checked UTF8 database invariant; they are not byte
iteration. PostgreSQL
itself cannot store U+0000 in `text`, while the explicit predicate covers the
rest of the frozen C0/C1 and separator boundary without POSIX classes.

### 2.3 Text constraints, migration order, and direct boundary coverage

The checks added to the two existing incident columns are exactly:

```sql
ALTER TABLE availability_incidents
    ADD CONSTRAINT ck_availability_incidents_acknowledgement_comment_text
    CHECK (
        acknowledgement_comment IS NULL
        OR public.wp07_incident_text_is_valid(acknowledgement_comment)
    );
ALTER TABLE availability_incidents
    ADD CONSTRAINT ck_availability_incidents_resolution_note_text
    CHECK (
        resolution_note IS NULL
        OR public.wp07_incident_text_is_valid(resolution_note)
    );
```

The exact `CREATE TABLE` statements in section 3.1.1 create the new
`incident_comments.comment` and `incident_lifecycle_actions.action_note`
columns as `NOT NULL` with their named checks, so every stored value is
validated without a later duplicate `ALTER`. The existing
`availability_incidents.acknowledgement_comment` and
`availability_incidents.resolution_note` columns remain nullable: `NULL` is
valid for existing rows, while every non-null value must already equal its
trimmed form, satisfy the exact control restriction, and contain 1–2,000 UTF-16
code units.

The additive migration dependency order is frozen. On Up:

1. Run the exact UTF8 preflight in section 2.1 as the first statement in the
   migration transaction.
2. Create `public.wp07_dotnet_trim`.
3. Create `public.wp07_dotnet_utf16_code_units`.
4. Create `public.wp07_incident_text_is_valid`.
5. Create the `idempotency_receipts` base table with its local checks and unique
   constraints but no receipt-to-child foreign keys.
6. Create `incident_comments` and `incident_lifecycle_actions` with their local
   checks, unique keys, and foreign keys to existing tables, but no
   child-to-receipt foreign keys.
7. Add both deferred child-to-receipt foreign keys.
8. Add both deferred composite receipt-to-child foreign keys.
9. Add the new checks to the existing incident columns and create remaining
   indexes and constraints.
10. Create the shared Commit 3A append-only trigger function and then its three
    table triggers in the exact order frozen in section 3.1.2.

On Down:

1. Drop `tr_incident_comments_immutable`,
   `tr_incident_lifecycle_actions_immutable`, and
   `tr_idempotency_receipts_immutable` from their owning tables.
2. Drop `public.fn_wp07_incident_runtime_reject_mutation()`.
3. Remove other Commit 3A dependent triggers and objects.
4. Drop the added constraints from the existing incident columns.
5. Drop `fk_idempotency_receipts_incident_comment_outcome` and
   `fk_idempotency_receipts_lifecycle_action_outcome`.
6. Drop `fk_incident_comments_idempotency_receipt` and
   `fk_incident_lifecycle_actions_idempotency_receipt`.
7. Drop `incident_comments`, then `incident_lifecycle_actions`, then
   `idempotency_receipts`; their local checks, keys, and existing-table foreign
   keys are removed with their owning tables.
8. Drop `public.wp07_incident_text_is_valid`.
9. Drop `public.wp07_dotnet_utf16_code_units`.
10. Drop `public.wp07_dotnet_trim`.

Down removes dependencies before referenced helper functions and never uses
`CASCADE`.

The future direct PostgreSQL boundary coverage must include all of the
following; this documentation freeze records coverage requirements only and
does not claim that runtime verification has started:

- Every member of the exact .NET trim set at both ends.
- Adjacent non-trim characters proving the helper does not over-trim.
- Interior whitespace preservation.
- Empty and trim-to-empty rejection.
- Raw one-code-unit minimum acceptance followed by normalized persistence.
- Raw exactly 2,000 UTF-16 units accepted when otherwise valid.
- Raw 2,001 UTF-16 units rejected before trimming, including a value whose
  padding would trim to 2,000 or fewer.
- Leading/trailing padding counted in the raw bound and removed only after raw
  DTO validation; the resulting normalized value is persisted.
- Whitespace-only raw input rejected by `[Required]` before `.Trim()`.
- Non-skippable route-level coverage for each of
  `POST /api/v1/incidents/{id}/acknowledge`,
  `POST /api/v1/incidents/{id}/comments`, and
  `POST /api/v1/incidents/{id}/resolve` rejects a lone `\uD800`
  high-surrogate escape, a lone `\uDC00` low-surrogate escape, a
  high-surrogate escape followed by a non-low escape, a reversed low/high pair,
  a truncated or malformed escape, and malformed raw UTF-8 during the strict
  staged JSON read. Each returns the safe `400 invalid-json` response with safe
  correlation, no body/escape/scalar logging, zero database commands, and no
  mutation.
- The same non-skippable coverage for each command route separately accepts a
  correctly paired supplementary-surrogate escape as one scalar, a
  supplementary scalar written directly inside the JSON string as its valid
  four-byte UTF-8 sequence, and a literal valid-UTF-8 U+FFFD as that distinct
  scalar unless an already-frozen later text rule rejects the complete value.
  For the direct UTF-8 supplementary case, coverage proves strict decoding and
  JSON parsing preserve the scalar without U+FFFD replacement; it counts as two
  UTF-16 code units; normalization, persisted text, v1 request-fingerprint
  bytes, canonical response representation, and exact replay preserve the same
  scalar; and neither `invalid-json` nor `invalid-incident-text` is returned.
  Coverage proves the paired-surrogate and literal-U+FFFD cases also receive no
  replacement transformation.
- Each command route rejects JSON-escaped U+0000, every U+0001–U+001F scalar,
  U+007F, every U+0080–U+009F scalar, U+2028, and U+2029 with
  `400 invalid-incident-text` before any database command; direct-write
  database checks remain the independent backstop.
- BMP scalars counted as one unit.
- Supplementary scalars encoded as four UTF-8 bytes counted as two units.
- Mixed BMP/supplementary boundary cases.
- No Unicode normalization or case folding.
- Untrimmed persisted values rejected.
- Valid and invalid direct `INSERT`/`UPDATE` checks for
  `incident_comments.comment` and `incident_lifecycle_actions.action_note`.
- `NULL` accepted for each existing nullable action-specific incident column.
- Invalid non-null values rejected for each existing nullable action-specific
  incident column.
- Migration Up confirms all three functions and all text constraints exist.
- Migration Up proves the UTF8 preflight succeeds on the ordinary UTF8 fixture
  and fails closed on the separate disposable `wp07_non_utf8_fixture` with
  SQLSTATE `55000`, the fixed safe message, and the fixed diagnostic constraint
  before any Commit 3A schema object exists; the ordinary fixture is unchanged.
- The isolated non-UTF8 fixture uses `Pooling=false` on every
  fixture-targeting connection string, proves zero remaining fixture sessions
  before administrative drop, and proves the fixture database is removed both
  after the expected migration rejection and after a setup/assertion failure;
  cleanup failure fails rather than skips the test. Coverage proves the
  Testcontainer `postgres` role has `CREATEDB`, create/drop execute outside
  transaction blocks, setup succeeds, the UTF8-preflight rejection is observed,
  teardown succeeds, and the database no longer exists.
- Migration Down confirms the dependent constraints and all three functions are
  removed in the frozen order.

- Direct PostgreSQL response_etag coverage must accept the shortest valid
  strong tag, "!"; a representative existing canonical generator value such
  as "wp07-2b1-sha256-ZbwxBPj5ue8V2Lhi9yqng6-89XjaLJEk3-39qc2mliI"; and the
  boundary values "!", "#", and "~". It must also accept an opaque value
  containing the literal U+005C backslash character, such as "a\b".
- Direct PostgreSQL response_etag coverage must reject NULL, empty text, the
  empty opaque tag "", unquoted text, W/"tag", w/"tag", leading or trailing
  OWS, an embedded DQUOTE, a space inside the opaque value, C0 controls, DEL
  U+007F, U+0080 and every other non-ASCII character, a missing opening or
  closing quote, extra text before or after the quoted tag, and newline or
  other control-based regex-bypass attempts.
- Migration/model/EF coverage must verify that the exact named
  ck_idempotency_receipts_response_etag_strong_quoted CHECK exists after Up,
  invalid direct INSERT and UPDATE operations fail at PostgreSQL, and valid
  stored ETags round-trip unchanged. Down must remove the dependent receipt
  foreign keys, then the receipt table and its response_etag constraint, before
  dropping related helper functions; no CASCADE is allowed. Exact replay
  coverage must return the stored quoted ETag without recomputation.

Commit 2 files remain unchanged by this documentation freeze.

### 3. Additive persistence decision

The runtime schema is a later, single additive EF migration for the Commit 3
runtime slice. It may add new tables and additive receipt linkage, but it must
not rewrite or amend a historical migration, remove existing WP-06 rows, or
change the meaning of the existing engine lifecycle table. The verified Commit 2
migration and model snapshot are the baseline and are not part of this commit.

The new persistence boundary is:

| Store | Required shape and purpose |
| --- | --- |
| `incident_comments` | Immutable `id`, `incident_id`, `probe_id`, non-null `author_id`, trimmed general `comment`, `created_at`, and `idempotency_key NOT NULL UNIQUE`; one row per successful public general-comment command. Its receipt-facing unique key is exactly `(idempotency_key, id, incident_id, author_id)`. |
| `incident_lifecycle_actions` | Immutable `event_id`, `incident_id`, `probe_id`, non-null public-action `actor_id`, exact `action_kind` discriminator and `reason_code`, `occurred_at`, `action_note NOT NULL` containing the trimmed required acknowledgement comment or resolution note, and `idempotency_key NOT NULL UNIQUE`; one row for acknowledgement or manual resolution only. Its receipt-facing unique key is exactly `(idempotency_key, event_id, incident_id, actor_id, action_kind)`. This is distinct from the WP-06 `incident_lifecycle_events` table. |
| `idempotency_receipts` | The exact receipt row contract is defined below: canonical request identity, SHA-256 digest, exact successful response representation, one exact outcome kind, and exactly one command-specific outcome reference. It stores no raw request body or OIDC claims. |

The schema defines `incident_lifecycle_actions.action_note` as `NOT NULL`.
The stored public-action discriminator is always named `action_kind`; its only
values are `acknowledgement` and `manual_resolution`. The matching stored
`reason_code` values are respectively `operator-acknowledgement` and
`manual-resolution`. General comments have neither value because they never
create an `incident_lifecycle_actions` row.

The existing `availability_incidents` row remains the authoritative mutable
incident state. Acknowledge writes its existing acknowledgement fields and one
public acknowledgement action; manual resolve writes its existing resolution
fields and one public manual-resolution action. A general comment, under the
already-frozen target incident row lock, writes exactly one immutable
`incident_comments` row and increments `availability_incidents.row_version`
exactly once. It changes no incident status, lifecycle timestamp, actor,
acknowledgement field, or resolution field. It writes no lifecycle action and
creates no lifecycle transition; its audit event, new strong incident ETag,
and complete response receipt are committed atomically. Acknowledgement
comments and resolution notes are action-specific text on their lifecycle
action and never become general incident comments. Public lifecycle rows and
comments are append-only records, not a replacement for the WP-06 engine rows.
The WP-06 `incident_lifecycle_events` and
`notification_suppression_contexts` tables remain immutable and retain their
existing constraints, triggers, policy lineage, and foreign keys.

Every child-to-receipt foreign key is explicitly deferred and non-cascading.
The idempotency key is the receipt identity and the first column of each exact
receipt-to-child composite relationship:

```text
idempotency_receipts.idempotency_key PRIMARY KEY

incident_comments.idempotency_key NOT NULL UNIQUE
    REFERENCES idempotency_receipts(idempotency_key)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED
UNIQUE (idempotency_key, id, incident_id, author_id)

incident_lifecycle_actions.idempotency_key NOT NULL UNIQUE
    REFERENCES idempotency_receipts(idempotency_key)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED
UNIQUE (idempotency_key, event_id, incident_id, actor_id, action_kind)
```

The receipt points back through those complete unique keys. Consequently the
receipt key, selected child ID, incident identity, actor/author identity, and,
for a lifecycle action, `outcome_kind = action_kind` must all describe the same
row at deferred-constraint checking time. A child from another receipt, incident,
or principal cannot be cross-wired into a receipt even when its standalone UUID
is valid.

Audit records remain part of the same atomic command transaction, but
`audit_events` has no receipt column and no receipt foreign-key relationship.

No receipt deletion policy is introduced here. Receipts and both child stores
are append-only and retained indefinitely until WP-09/UA-09 defines retention.
The current triggers reject every delete. A later approved retention design
would require its own governed migration or maintenance boundary to replace or
remove the applicable protection before deleting a receipt and its children in
one transaction; the deferred `NO ACTION` constraints must still validate that
final state, and no cascade is permitted. This ADR does not authorize that
future mechanism.

#### 3.1 Exact idempotency receipt row and EF mapping

The future EF entity is `IdempotencyReceipt`. Its PostgreSQL columns and EF
property mappings are frozen as follows; these names are part of the runtime
contract, not illustrative metadata:

| CLR property | PostgreSQL column | PostgreSQL type | EF mapping and nullability |
| --- | --- | --- | --- |
| `IdempotencyKey` | `idempotency_key` | `character varying(36) COLLATE "C"` | Required `string`; primary key; `HasColumnType("character varying(36)")`, `HasMaxLength(36)`, and `UseCollation("C")`. |
| `RouteTemplate` | `route_template` | `text COLLATE "C"` | Required `string`; `UseCollation("C")`. |
| `IncidentId` | `incident_id` | `uuid` | Required `Guid`. |
| `ActorId` | `actor_id` | `uuid` | Required `Guid`; the canonical surrogate actor UUID. |
| `RequestFingerprintVersion` | `request_fingerprint_version` | `smallint` | Required `short`; `HasColumnType("smallint")`; exactly `1` for the frozen v1 byte encoding below. |
| `RequestDigest` | `request_digest` | `bytea` | Required `byte[]`; exactly 32 bytes for SHA-256. |
| `ResponseStatus` | `response_status` | `integer` | Required `int`; successful command status only. |
| `ResponseBody` | `response_body` | `bytea` | Required `byte[]`; exact response bytes. |
| `ResponseContentType` | `response_content_type` | `text` | Required `string`; exact `Content-Type` header value. |
| `ResponseEtag` | `response_etag` | `text` | Required `string`; exact strong `ETag` header value including HTTP quotes. |
| `OutcomeKind` | `outcome_kind` | `character varying(32) COLLATE "C"` | Required `string`; `HasColumnType("character varying(32)")`, `HasMaxLength(32)`, `UseCollation("C")`, and the three-value check below. |
| `IncidentCommentId` | `incident_comment_id` | `uuid` | Optional `Guid?`; locally unique and part of the composite foreign key to the selected comment. |
| `IncidentLifecycleActionId` | `incident_lifecycle_action_id` | `uuid` | Optional `Guid?`; locally unique and part of the composite foreign key to the selected lifecycle action. |
| `CreatedAt` | `created_at` | `timestamp with time zone` | Required `DateTimeOffset`. |
| `CompletedAt` | `completed_at` | `timestamp with time zone` | Required `DateTimeOffset`. |

The exact receipt base table is:

```sql
CREATE TABLE idempotency_receipts (
    idempotency_key character varying(36) COLLATE "C" NOT NULL,
    route_template text COLLATE "C" NOT NULL,
    incident_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    request_fingerprint_version smallint NOT NULL,
    request_digest bytea NOT NULL,
    response_status integer NOT NULL,
    response_body bytea NOT NULL,
    response_content_type text NOT NULL,
    response_etag text NOT NULL,
    outcome_kind character varying(32) COLLATE "C" NOT NULL,
    incident_comment_id uuid NULL,
    incident_lifecycle_action_id uuid NULL,
    created_at timestamp with time zone NOT NULL,
    completed_at timestamp with time zone NOT NULL,
    CONSTRAINT pk_idempotency_receipts PRIMARY KEY (idempotency_key),
    CONSTRAINT ck_idempotency_receipts_request_fingerprint_version
        CHECK (request_fingerprint_version = 1),
    CONSTRAINT ck_idempotency_receipts_request_digest
        CHECK (pg_catalog.octet_length(request_digest) = 32),
    CONSTRAINT ck_idempotency_receipts_response_status_valid
        CHECK (response_status BETWEEN 100 AND 599),
    CONSTRAINT ck_idempotency_receipts_response_status_success
        CHECK (response_status IN (200, 201)),
    CONSTRAINT ck_idempotency_receipts_outcome_status
        CHECK (
            (outcome_kind = 'general_comment' AND response_status = 201)
            OR
            (outcome_kind IN ('acknowledgement', 'manual_resolution')
             AND response_status = 200)
        ),
    CONSTRAINT ck_idempotency_receipts_content_type_nonempty
        CHECK (response_content_type COLLATE "C" <> ''),
    CONSTRAINT ck_idempotency_receipts_response_etag_strong_quoted
        CHECK (response_etag COLLATE "C" ~ '^"[!#-~]+"$'),
    CONSTRAINT ck_idempotency_receipts_route_nonempty
        CHECK (route_template COLLATE "C" <> ''),
    CONSTRAINT ck_idempotency_receipts_outcome_kind
        CHECK (outcome_kind IN
               ('acknowledgement', 'general_comment', 'manual_resolution')),
    CONSTRAINT ck_idempotency_receipts_outcome_references
        CHECK (
            (outcome_kind = 'general_comment'
             AND incident_comment_id IS NOT NULL
             AND incident_lifecycle_action_id IS NULL)
            OR
            (outcome_kind = 'acknowledgement'
             AND incident_comment_id IS NULL
             AND incident_lifecycle_action_id IS NOT NULL)
            OR
            (outcome_kind = 'manual_resolution'
             AND incident_comment_id IS NULL
             AND incident_lifecycle_action_id IS NOT NULL)
        ),
    CONSTRAINT ck_idempotency_receipts_completed_at
        CHECK (completed_at >= created_at),
    CONSTRAINT uq_idempotency_receipts_incident_comment
        UNIQUE (incident_comment_id),
    CONSTRAINT uq_idempotency_receipts_lifecycle_action
        UNIQUE (incident_lifecycle_action_id)
);
```

`request_fingerprint_version` is exactly `1`. It identifies the v1 byte
encoding in section 5; receipt comparison dispatches on this stored version and
must retain the v1 comparer for every retained receipt. The outcome kind values
are exactly `acknowledgement`, `general_comment`, and
`manual_resolution`. A `general_comment` receipt has status `201`, a non-null
`incident_comment_id` and a null `incident_lifecycle_action_id`. An
`acknowledgement` or `manual_resolution` receipt has status `200` and a non-null
`incident_lifecycle_action_id` and a null `incident_comment_id`. The combined
check rejects every other null/non-null combination; the two unique constraints
make each outcome reference one-to-one with its receipt.

##### 3.1.1 Exact comment and public lifecycle-action rows

The future EF entities are `IncidentComment` and `IncidentLifecycleAction`.
Their exact columns and mappings are:

| CLR property | PostgreSQL column | PostgreSQL type | EF mapping and nullability |
| --- | --- | --- | --- |
| `IncidentComment.Id` | `id` | `uuid` | Required `Guid`; primary key; `ValueGeneratedNever()`. |
| `IncidentComment.IncidentId` | `incident_id` | `uuid` | Required `Guid`. |
| `IncidentComment.ProbeId` | `probe_id` | `uuid` | Required `Guid`. |
| `IncidentComment.AuthorId` | `author_id` | `uuid` | Required `Guid`. |
| `IncidentComment.Comment` | `comment` | `text` | Required `string`; checked by `wp07_incident_text_is_valid`. |
| `IncidentComment.CreatedAt` | `created_at` | `timestamp with time zone` | Required `DateTimeOffset`. |
| `IncidentComment.IdempotencyKey` | `idempotency_key` | `character varying(36) COLLATE "C"` | Required `string`; `HasColumnType("character varying(36)")`, `HasMaxLength(36)`, and `UseCollation("C")`. |
| `IncidentLifecycleAction.EventId` | `event_id` | `uuid` | Required `Guid`; primary key; `ValueGeneratedNever()`. |
| `IncidentLifecycleAction.IncidentId` | `incident_id` | `uuid` | Required `Guid`. |
| `IncidentLifecycleAction.ProbeId` | `probe_id` | `uuid` | Required `Guid`. |
| `IncidentLifecycleAction.ActorId` | `actor_id` | `uuid` | Required `Guid`. |
| `IncidentLifecycleAction.ActionKind` | `action_kind` | `character varying(32) COLLATE "C"` | Required `string`; `HasColumnType("character varying(32)")`, `HasMaxLength(32)`, and `UseCollation("C")`. |
| `IncidentLifecycleAction.ReasonCode` | `reason_code` | `character varying(64) COLLATE "C"` | Required `string`; `HasColumnType("character varying(64)")`, `HasMaxLength(64)`, and `UseCollation("C")`. |
| `IncidentLifecycleAction.OccurredAt` | `occurred_at` | `timestamp with time zone` | Required `DateTimeOffset`. |
| `IncidentLifecycleAction.ActionNote` | `action_note` | `text` | Required `string`; checked by `wp07_incident_text_is_valid`. |
| `IncidentLifecycleAction.IdempotencyKey` | `idempotency_key` | `character varying(36) COLLATE "C"` | Required `string`; `HasColumnType("character varying(36)")`, `HasMaxLength(36)`, and `UseCollation("C")`. |

Every property uses the shown explicit `HasColumnName`; types, lengths,
collations, requiredness, keys, and indexes are configured explicitly. The base
tables, excluding only the cyclic receipt foreign keys added afterward, are
exactly:

```sql
CREATE TABLE incident_comments (
    id uuid NOT NULL,
    incident_id uuid NOT NULL,
    probe_id uuid NOT NULL,
    author_id uuid NOT NULL,
    comment text NOT NULL,
    created_at timestamp with time zone NOT NULL,
    idempotency_key character varying(36) COLLATE "C" NOT NULL,
    CONSTRAINT pk_incident_comments PRIMARY KEY (id),
    CONSTRAINT ck_incident_comments_comment
        CHECK (public.wp07_incident_text_is_valid(comment)),
    CONSTRAINT uq_incident_comments_idempotency_key
        UNIQUE (idempotency_key),
    CONSTRAINT uq_incident_comments_receipt_outcome
        UNIQUE (idempotency_key, id, incident_id, author_id),
    CONSTRAINT fk_incident_comments_availability_incident
        FOREIGN KEY (incident_id, probe_id)
        REFERENCES availability_incidents(id, probe_id)
        ON DELETE NO ACTION,
    CONSTRAINT fk_incident_comments_human_principal
        FOREIGN KEY (author_id)
        REFERENCES human_principals(id)
        ON DELETE NO ACTION
);

CREATE TABLE incident_lifecycle_actions (
    event_id uuid NOT NULL,
    incident_id uuid NOT NULL,
    probe_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    action_kind character varying(32) COLLATE "C" NOT NULL,
    reason_code character varying(64) COLLATE "C" NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    action_note text NOT NULL,
    idempotency_key character varying(36) COLLATE "C" NOT NULL,
    CONSTRAINT pk_incident_lifecycle_actions PRIMARY KEY (event_id),
    CONSTRAINT ck_incident_lifecycle_actions_kind_reason
        CHECK (
            (action_kind = 'acknowledgement'
             AND reason_code = 'operator-acknowledgement')
            OR
            (action_kind = 'manual_resolution'
             AND reason_code = 'manual-resolution')
        ),
    CONSTRAINT ck_incident_lifecycle_actions_action_note
        CHECK (public.wp07_incident_text_is_valid(action_note)),
    CONSTRAINT uq_incident_lifecycle_actions_idempotency_key
        UNIQUE (idempotency_key),
    CONSTRAINT uq_incident_lifecycle_actions_receipt_outcome
        UNIQUE (idempotency_key, event_id, incident_id, actor_id, action_kind),
    CONSTRAINT fk_incident_lifecycle_actions_availability_incident
        FOREIGN KEY (incident_id, probe_id)
        REFERENCES availability_incidents(id, probe_id)
        ON DELETE NO ACTION,
    CONSTRAINT fk_incident_lifecycle_actions_human_principal
        FOREIGN KEY (actor_id)
        REFERENCES human_principals(id)
        ON DELETE NO ACTION
);
```

The existing named alternate key `availability_incidents(id, probe_id)` is the
principal key for both child composite incident/Probe foreign keys. EF maps all
non-cyclic foreign keys with `DeleteBehavior.NoAction`. The action-kind/reason
check is the PostgreSQL boundary for the only two allowed stored pairs; neither
column is nullable, and no unlisted pairing is valid.

The child-to-receipt foreign keys remain deferred, non-cascading, and exact:

```sql
ALTER TABLE incident_comments
    ADD CONSTRAINT fk_incident_comments_idempotency_receipt
    FOREIGN KEY (idempotency_key)
    REFERENCES idempotency_receipts(idempotency_key)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE incident_lifecycle_actions
    ADD CONSTRAINT fk_incident_lifecycle_actions_idempotency_receipt
    FOREIGN KEY (idempotency_key)
    REFERENCES idempotency_receipts(idempotency_key)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
```

The receipt's two nullable outcome references are explicit, non-cascading,
deferred composite foreign keys. They replace standalone child-ID foreign keys;
no receipt-to-child relationship omits the idempotency key or stored identities:

```sql
ALTER TABLE idempotency_receipts
    ADD CONSTRAINT fk_idempotency_receipts_incident_comment_outcome
    FOREIGN KEY
        (idempotency_key, incident_comment_id, incident_id, actor_id)
    REFERENCES incident_comments
        (idempotency_key, id, incident_id, author_id)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;

ALTER TABLE idempotency_receipts
    ADD CONSTRAINT fk_idempotency_receipts_lifecycle_action_outcome
    FOREIGN KEY
        (idempotency_key, incident_lifecycle_action_id, incident_id,
         actor_id, outcome_kind)
    REFERENCES incident_lifecycle_actions
        (idempotency_key, event_id, incident_id, actor_id, action_kind)
    ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
```

EF maps all four relationships with `DeleteBehavior.NoAction`; the migration
SQL adds `DEFERRABLE INITIALLY DEFERRED` because EF fluent configuration does
not express PostgreSQL deferrability. The required child properties
`IncidentComment.IdempotencyKey` and
`IncidentLifecycleAction.IdempotencyKey` are unique foreign keys to the
receipt primary key. The optional receipt properties participate in the exact
composite foreign keys back to the created child rows. The two physical
directions are separate no-navigation relationships so the reciprocal
constraints do not create an implicit cascade or an ambiguous aggregate
ownership model. The existing receipt-local unique constraints on each nullable
child ID retain one-to-one cardinality.

EF configuration uses explicit `HasColumnName`, `HasColumnType`, and required
or optional property configuration for every row in the mapping table; no
convention-derived column name or type is permitted. The immutable
`incident_comments.id` and `incident_lifecycle_actions.event_id` columns are
primary keys and are also included in the exact receipt-facing composite unique
keys targeted by the two optional receipt relationships.

##### 3.1.2 Exact append-only function, triggers, and EF behavior

`incident_comments`, `incident_lifecycle_actions`, and
`idempotency_receipts` are append-only at the PostgreSQL boundary. Receipts
receive the same protection because their fingerprint, actor, outcome identity,
status, body bytes, Content-Type, and quoted ETag are immutable replay evidence.
After all three tables and every required local and reciprocal constraint exist,
the migration creates exactly this distinct Commit 3A function and these
triggers:

```sql
CREATE FUNCTION public.fn_wp07_incident_runtime_reject_mutation()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $wp07_incident_runtime_immutable$
BEGIN
    RAISE EXCEPTION USING
        ERRCODE = '23514',
        MESSAGE = 'WP07 incident runtime records are immutable.',
        CONSTRAINT = TG_NAME;
    RETURN OLD;
END;
$wp07_incident_runtime_immutable$;

CREATE TRIGGER tr_incident_comments_immutable
BEFORE UPDATE OR DELETE ON public.incident_comments
FOR EACH ROW
EXECUTE FUNCTION public.fn_wp07_incident_runtime_reject_mutation();

CREATE TRIGGER tr_incident_lifecycle_actions_immutable
BEFORE UPDATE OR DELETE ON public.incident_lifecycle_actions
FOR EACH ROW
EXECUTE FUNCTION public.fn_wp07_incident_runtime_reject_mutation();

CREATE TRIGGER tr_idempotency_receipts_immutable
BEFORE UPDATE OR DELETE ON public.idempotency_receipts
FOR EACH ROW
EXECUTE FUNCTION public.fn_wp07_incident_runtime_reject_mutation();
```

The function signature is exactly
`public.fn_wp07_incident_runtime_reject_mutation() RETURNS trigger`. Every
rejection uses SQLSTATE `23514`, fixed safe message `WP07 incident runtime
records are immutable.`, and PostgreSQL `ConstraintName` equal to the fixed
trigger identity for the affected table:
`tr_incident_comments_immutable`,
`tr_incident_lifecycle_actions_immutable`, or
`tr_idempotency_receipts_immutable`. No row value or caller-controlled text is
included in the diagnostic.

EF configuration sets
`Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Throw` as the
after-save behavior for every mapped property of `IncidentComment`,
`IncidentLifecycleAction`, and `IdempotencyReceipt`. Inserts remain allowed;
after insertion, tracked property modification fails before SQL generation.
Tracked or direct deletion and every
direct PostgreSQL `UPDATE` remain protected by the database triggers. This
function is separate from, and does not replace, alter, invoke, or share a
trigger with Commit 2's
`public.fn_wp07_human_principals_reject_mutation()` and
`tr_human_principals_immutable`; the verified HumanPrincipal mechanism and all
WP-06 artifacts remain unchanged.

Future direct PostgreSQL coverage must attempt both `UPDATE` and `DELETE` on
each of the three tables and assert the exact SQLSTATE, message, and trigger
identity above. Full-row before/after snapshots must be identical after every
rejection. Rejected lifecycle-action mutation must leave total ordering and
protected cursor traversal stable, without gaps or duplicates. Rejected receipt
mutation must leave subsequent exact status/body/Content-Type/ETag replay
byte-for-byte unchanged. Migration Up must prove function-then-trigger creation
after constraints; Down must drop all three triggers before the shared function
and before any owning table.

`response_status` is a non-null PostgreSQL `integer` with both a valid HTTP
status check (`100..599`) and the frozen successful-command check (`200` or
`201`). `response_body` is `bytea NOT NULL` and stores the exact canonical
UTF-8 response bytes sent for the original successful command. It is not a
JSON document to be decoded and regenerated. `response_content_type` is
`text NOT NULL` and stores the exact `Content-Type` header value.
`response_etag` is `text NOT NULL` and stores the exact strong `ETag` header
value, including its HTTP quotes.

Replay writes the stored `response_status`, `response_body`,
`response_content_type`, and `response_etag` directly. It does not reconstruct
a DTO, reserialize JSON, recanonicalize response data, recompute an ETag, or
reread mutable incident, projection, comment, lifecycle-action, audit, or
outcome state. Its only permitted non-receipt read is the immutable
HumanPrincipal mapping required to verify `receipt.actor_id` against the
authenticated issuer/subject pair; it does not mutate that mapping.

The named response_etag constraint is
ck_idempotency_receipts_response_etag_strong_quoted with the exact
locale-independent expression
CHECK (response_etag COLLATE "C" ~ '^"[!#-~]+"$'). The anchors require exactly
one leading and one trailing DQUOTE; the plus quantifier requires at least one
opaque-value character; and the allowed class is U+0021 or U+0023-U+007E.
Therefore U+0022, spaces, controls, DEL, non-ASCII characters, W/ or w/
prefixes, unquoted values, and extra surrounding characters are rejected.
The C-collated expression uses no POSIX locale character class. It validates
the stored header value without transforming it.

Before freezing this check, the existing Wp07DashboardCanonicalizer.EtagFor
generator was confirmed to emit the strong quoted ASCII format
"wp07-2b1-sha256-<43-character-unpadded-base64url-SHA256>". Its prefix and
hyphen separators and unpadded base64url alphabet are within the allowed
class. The current generator emits no U+005C; direct boundary coverage still
tests that literal as an allowed stored opaque character without changing or
broadening the generator.

Migration creation handles the physical FK cycle in the exact Up order frozen
in section 2.3: create the receipt base table; create both child base tables;
add `fk_incident_comments_idempotency_receipt` and
`fk_incident_lifecycle_actions_idempotency_receipt`; then add
`fk_idempotency_receipts_incident_comment_outcome` and
`fk_idempotency_receipts_lifecycle_action_outcome`. All four cyclic constraints
are `ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED`. A command transaction
uses the inverse data order: the applicable child outcome is inserted before
the receipt, and all applicable deferred relationships are checked at commit.

Within the frozen Down step for these tables, first drop
`tr_incident_comments_immutable`,
`tr_incident_lifecycle_actions_immutable`, and
`tr_idempotency_receipts_immutable`; then drop
`public.fn_wp07_incident_runtime_reject_mutation()`; then drop
`fk_idempotency_receipts_incident_comment_outcome` and
`fk_idempotency_receipts_lifecycle_action_outcome`, then drop
`fk_incident_comments_idempotency_receipt` and
`fk_incident_lifecycle_actions_idempotency_receipt`, and only then drop the
child tables followed by the receipt table and their local constraints. No
`CASCADE` is used. The helper
functions are dropped only after all of these table dependencies and the
existing incident-column checks have been removed.

Each fresh mutating command uses exactly two EF flushes inside one explicit
relational database transaction and one atomic database commit:

1. Begin the explicit READ COMMITTED relational transaction, then acquire locks
   and perform reads in the frozen order.
2. Track the incident/projection mutation, the applicable child comment or
   lifecycle-action row, and the audit row. Do not create or track a new
   `IdempotencyReceipt` yet.
3. Call the first `SaveChangesAsync`. This child-first flush persists the state,
   child, and audit rows while PostgreSQL's reciprocal foreign keys remain
   `DEFERRABLE INITIALLY DEFERRED`.
4. Only after the first flush succeeds, produce and canonicalize the HTTP
   response exactly once, capture its final status, exact body bytes,
   Content-Type, quoted ETag, identity, fingerprint, outcome, and timestamps,
   then create and track the fully populated `IdempotencyReceipt`.
5. Call `SaveChangesAsync` a second time. This receipt-last flush inserts only
   the new receipt; it does not reapply the already-flushed state, child, or
   audit mutations.
6. Call `CommitAsync` exactly once. PostgreSQL checks the final deferred
   reciprocal constraints at this commit. Return the successful HTTP response
   only after `CommitAsync` confirms success; that confirmation is the only
   local proof that either flush is durable.

No `SaveChanges` call occurs outside the explicit transaction. The context is
discarded after any failed attempt; accepted in-memory entity states from the
first flush are never reused as proof of durability. A failure or cancellation
before `COMMIT` is issued, and a server-rejected `COMMIT` including a deferred-
constraint rejection, rolls back both flushes, leaving no partial state, child,
audit, principal, or receipt row. A receipt is never committed for validation,
authorization, not-found, stale-ETag, state-conflict, or a pre-COMMIT failure.

`CommitAsync` outcome classification is exact. A `PostgresException` returned
by `CommitAsync` is a server-rejected commit; call `RollbackAsync`, then dispose
the still-uncommitted transaction and context and return the frozen sanitized
failure, with no durable row. Every exception or cancellation returned by
`CommitAsync` that is not a `PostgresException` is indeterminate because
`COMMIT` may already have reached PostgreSQL: the complete transaction may be
committed or rolled back. Dispose the context and transaction without a
compensating rollback and send no success response. Return the frozen
caller-cancellation behavior when the request token is canceled; otherwise
return the sanitized provider-failure behavior. A same-key retry acquires the
idempotency lock, then either finds and exact-replays the durable receipt or
finds no receipt and executes as a new command. Atomic PostgreSQL commit means
the retry can observe only the complete successful state and receipt or the
pre-command state, never a partial child, audit, state, principal, or receipt
result. Tracking the receipt before the first flush is prohibited because it
would recreate EF's circular command dependency across the reciprocal
relationships.

The focused receipt coverage must prove all of the following; this is a future
verification requirement, not evidence that runtime verification has started:

- Each of `acknowledgement`, `general_comment`, and `manual_resolution`
  persists `request_fingerprint_version = 1`, the exact successful status, `response_body` bytea,
  `response_content_type`, `response_etag`, `outcome_kind`, and its correct
  mutually exclusive outcome reference.
- The v1 encoder produces the fixed SHA-256
  `937CF17541DFE9365E1CCE7039A105AF09E3435E6FBE0518E6A38A436543E608` for
  method `POST`, route `/api/v1/incidents/{id}/comments`, incident
  `01234567-89ab-cdef-0123-456789abcdef`, actor
  `fedcba98-7654-3210-fedc-ba9876543210`, `If-Match` value `"e"`, and body
  `{"Comment":"x"}`. Coverage also proves field-length framing prevents
  boundary ambiguity and proves the U+0022/U+005C `J(value)` escapes.
- A replay uses the stored fingerprint version, compares v1 with the frozen v1
  encoder, and rejects an unsupported stored version as a sanitized dependency
  failure without replay or mutation. Retained v1 receipts remain replayable
  after later encoder versions are introduced.
- Stored body bytes equal the original HTTP response bytes byte-for-byte.
- After a later incident mutation advances the current ETag, replay returns
  the original status, identical body bytes, identical Content-Type, and
  original quoted ETag.
- Replay succeeds through a fresh `DbContext`/host path without an in-memory
  response.
- Same authenticated issuer/subject mapped to `receipt.actor_id` plus an equal
  request fingerprint exact-replays the stored representation byte-for-byte.
- A different authenticated actor using the same key returns the frozen safe
  idempotency conflict without replay. A missing or inconsistent immutable
  principal mapping returns the sanitized dependency failure without replay.
- A same-actor fingerprint mismatch returns the frozen idempotency conflict.
- All replay/conflict/dependency cases prove no principal insertion, attachment,
  update, deletion, or `SaveChangesAsync`; no mutable outcome, incident,
  projection, comment, lifecycle-action, or audit read; and no mutation.
- Replay reads the matched receipt plus the immutable HumanPrincipal mapping
  required for actor verification; it does not reserialize JSON or query or
  mutate incident, projection, comment, lifecycle-action, or audit state, and
  it never mutates a principal or receipt.
- Direct PostgreSQL writes reject every invalid `outcome_kind` and outcome
  reference combination, reject `NULL` in every required response column, and
  reject every `request_fingerprint_version` other than `1`.
- At deferred-constraint checking time, direct PostgreSQL writes reject a
  receipt pointing to a different child ID or to a child with a different
  `idempotency_key`, `incident_id`, or `author_id`/`actor_id`; this includes two
  otherwise valid children deliberately crossed between receipts. Each
  rejection rolls back the receipt, child, incident/projection change, and
  audit row.
- Direct PostgreSQL writes reject an `acknowledgement` receipt referencing a
  `manual_resolution` action and a `manual_resolution` receipt referencing an
  `acknowledgement` action. The composite lifecycle-action foreign key proves
  `outcome_kind = action_kind`, and each rejection leaves no mutation.
- Direct PostgreSQL writes accept each valid complete comment and lifecycle
  relationship and prove the applicable reciprocal constraints are deferred
  until commit while receipt-last insertion remains valid.
- EF coverage proves no circular command graph exists because the receipt is
  not tracked for the child-first flush; the first `SaveChangesAsync` succeeds
  under deferred constraints, the receipt-last second `SaveChangesAsync`
  succeeds, and the final constraints are checked by the single `CommitAsync`.
- Injected failure or cancellation before `COMMIT`, and a `PostgresException`
  from final deferred-constraint checking, roll back both flushes and preserve
  full before/after snapshots. An `NpgsqlException`, transport I/O failure,
  timeout, or `OperationCanceledException` from `CommitAsync` is tested as
  indeterminate: same-key retry exact-replays a complete durable receipt when
  present or executes fresh from the pre-command snapshot when absent. Losing
  races leave no partial child, audit, state, principal, or receipt row. Exact
  replay calls neither `SaveChangesAsync` nor `CommitAsync` and performs no
  writes.
- Migration Up exposes the exact receipt columns, PostgreSQL types,
  nullability, checks, and four foreign keys; Down removes dependent objects,
  constraints, and tables in dependency-safe order before dropping helpers.
- Before/after structural snapshots prove no mutation on replay and
  idempotency-conflict paths.

#### 3.2 Global idempotency-key concurrency coverage

The future focused PostgreSQL/integration coverage must prove the global
transaction-scoped serialization and records only coverage requirements; it is
not evidence that runtime verification has started:

- Two concurrent requests with the same new Idempotency-Key and identical
  canonical request derive the same advisory key; exactly one transaction
  proceeds as the new command, the loser waits on that advisory lock, and,
  after the winner commits, the loser reads the completed receipt and replays
  it. Exactly one incident mutation, command outcome row, audit event,
  HumanPrincipal mapping, and receipt exists.
- Two concurrent requests with the same new Idempotency-Key but a different
  actor, route, incident, body, digest, or normalized If-Match have one winner.
  After the winner commits, the loser reads the completed receipt and returns
  409 idempotency-key-reuse-conflict without downstream lock acquisition or
  mutation.
- The losing transaction never observes or creates a partial receipt.
- Winner rollback, pre-COMMIT failure, or server-rejected commit releases the
  advisory lock, persists no receipt or command mutation, and allows the waiter
  to acquire the lock and execute as the new command. A transport failure after
  `COMMIT` is issued is indeterminate; the waiter obtains the lock only after
  PostgreSQL resolves that transaction, then full-key receipt lookup replays a
  committed winner or executes fresh when no receipt exists.
- Cancellation or timeout while waiting returns through the frozen cancellation
  or failure behavior, releases or rolls back its transaction, and creates no
  HumanPrincipal, receipt, audit, comment, lifecycle-action, incident, or
  projection mutation.
- Different Idempotency-Key values that derive different signed Int32 values in
  namespace `1464873015` do not block one another. Mandatory forced-collision
  coverage at the derivation seam supplies different full keys with the same
  derived Int32: the loser waits, then performs a full-key receipt read, finds
  no receipt for its own key, and executes as a fresh command. The collision
  causes only extra serialization and never receipt equivalence, incorrect
  replay, or a uniqueness failure.
- In the forced-collision losing-race variant, failure or cancellation after
  lock acquisition rolls back every principal, incident/projection, child,
  audit, and receipt mutation; the other full key's committed receipt and
  outcome remain unchanged.
- Command-observer and lock-order coverage confirms that idempotency advisory
  lock acquisition precedes HumanPrincipal creation and every Probe,
  projection, and incident lock.
- Replay and idempotency-conflict paths prove that no Probe, projection, or
  incident lock is acquired and no command-owned row is mutated.

#### 3.3 General-comment ETag and concurrency coverage

The future focused PostgreSQL/integration coverage must prove that comment-only
means no lifecycle action or transition, not that the incident row is untouched;
this is a coverage requirement and not evidence that runtime verification has
started:

- A fresh successful general-comment command, under the incident row lock,
  inserts exactly one `incident_comments` row, increments
  `availability_incidents.row_version` exactly once, changes no incident
  status, lifecycle timestamp, actor, acknowledgement field, or resolution
  field, creates zero `incident_lifecycle_actions` rows and no lifecycle
  transition, writes its audit event, returns the new strong incident ETag,
  stores the exact response and ETag in the receipt inserted last, and commits
  all changes atomically.
- Exact replay returns the original stored response body and ETag without a
  second `row_version` increment or any additional comment, lifecycle-action,
  audit, or receipt row.
- A new key with stale `If-Match` returns `412 concurrency-conflict` and
  performs no mutation.
- Idempotency-key-reuse conflict, cancellation, provider failure, and
  transaction rollback do not advance `row_version` or create a comment,
  lifecycle action, audit event, or receipt.
- Concurrent fresh general comments using the same starting `If-Match` result
  in only one successful `row_version` increment and one successful comment at
  that concurrency point; the other command follows the frozen stale-ETag
  behavior.

### 4. HumanPrincipal get-or-create

The authenticated human identity is exactly one non-empty, bounded, case-
sensitive OIDC `iss` plus `sub` pair. Email, display name, role,
`NameIdentifier`, a development role value, and `Guid.Empty` are never
fallbacks. Issuer, subject, tokens, authorization values, and mapping internals
are not public response fields and are not logged.

For commands, this identity is resolved at the pre-database section 1.1
`PrincipalIdentityResolver` boundary, not during route/header/body validation
and not by a database lookup. Only the resolved `PrincipalIdentity` reaches the
transaction and HumanPrincipal get-or-create path below. A resolver failure has
already returned `403 invalid-incident-actor-identity`; it never enters this
section, acquires an idempotency lock, or creates a HumanPrincipal.

Future command coverage must prove resolver acceptance of exactly one valid
case-sensitive `iss`/`sub` pair and rejection of an unauthenticated principal,
the Agent credential scheme, zero or duplicate `iss` or `sub` claims, null,
empty, whitespace-only, trim-unstable, invalid-UTF-16, over-512-scalar, Control,
LineSeparator, and ParagraphSeparator values. Every rejection is the frozen
`403 invalid-incident-actor-identity` with safe correlation and no claim value
in the response or logs. It occurs after a successful role check but before an
otherwise-invalid query, route, header, content type, JSON body, or DTO value,
with zero database commands and zero mutations.

The command transaction uses the following atomic insert. `@id` is a newly
generated nonempty UUID supplied by the application for this insertion attempt;
`human_principals.id` has no assumed database default or database-generated ID:

```sql
INSERT INTO human_principals (id, issuer, subject, created_at)
VALUES (@id, @issuer, @subject, @created_at)
ON CONFLICT (issuer, subject) DO NOTHING
RETURNING id;
```

If the insert returns an ID, that returned ID is the resolved principal ID. If
the uniqueness conflict returns no row, discard the unused candidate `@id` and,
in the next READ COMMITTED statement, deterministically re-read the existing
principal by the exact case-sensitive `issuer` and `subject` pair. Concurrent
inserts therefore converge on the one row protected by the unique pair
boundary. The discarded candidate is never exposed or persisted. The insert
and reread remain inside the command transaction. If the command later fails,
a principal inserted by this attempt is rolled back with the command
transaction.

All later command failures roll back a newly inserted principal; no error path
commits the candidate merely because identity resolution ran first. Receipt
replay and idempotency-conflict paths never invoke this insert and never insert
a HumanPrincipal.

On the receipt-present path, actor verification is read-only. The receipt query
loads the complete receipt by its full exact Idempotency-Key and also reads the
immutable `human_principals` row referenced by `receipt.actor_id` in the same
database query using `LEFT JOIN`. The
authenticated case-sensitive issuer/subject pair must equal that
row's issuer/subject, and the joined principal ID must equal `receipt.actor_id`,
before the actor surrogate is used to compute and compare the canonical request
fingerprint. The query never inserts, updates, deletes, or attaches a new
principal and never calls `SaveChangesAsync`.

A different authenticated actor using the same key returns the frozen safe `409
idempotency-key-reuse-conflict`; it never receives another actor's replay. A
missing or internally inconsistent referenced principal is a sanitized
dependency/read failure under the existing privacy contract, not a replay or a
new-key path. Issuer and subject never enter the public response or logs.
Receipt-scoped replay therefore permits the immutable principal verification
read but forbids reads of mutable incident, projection, comment,
lifecycle-action, audit, or outcome state.

### 5. ETags, preconditions, and canonical request identity

The incident detail ETag is server-generated, strong, quoted, opaque, and stable
while incident identity and concurrency state are unchanged. It appears only in
the `ETag` response header. The representation never exposes its encoding or the
underlying `rowVersion`.

Detail `If-None-Match` uses the existing bounded entity-tag parser: multiple
header lines are supported, weak comparison is used for GET, wildcard is valid
only as the sole member, and malformed or empty-list-member input is
`400 invalid-if-none-match`. A match returns bodyless `304` with the current
ETag, `Cache-Control: private, max-age=0, must-revalidate`, and the safe server
`X-Correlation-ID`. List responses have no aggregate ETag.

Every command requires exactly one strong `If-Match` value. Missing is
`428 Precondition Required`. An empty or OWS-only field value, malformed tag,
weak tag, wildcard, comma-list, or multiple header values is
`400 invalid-if-match`. The quoted empty opaque tag `""` is syntactically valid;
it is current only when it equals the current ETag. A syntactically valid stale
tag returns `412 concurrency-conflict` with the current ETag in both the header
and the `currentEtag` Problem Details extension.

Every command also requires exactly one non-empty canonical lowercase UUID-D
`Idempotency-Key` header. An absent, empty, malformed, or repeated header value
is exactly `400 invalid-idempotency-key`.

#### 5.1 Exact incident ETag v1 byte encoding

Incident ETag v1 is immutable. It is exactly the following canonical byte
sequence, with `||` denoting concatenation:

```text
canonicalBytes =
    ASCII("EE-PULSE/WP07/INCIDENT-ETAG/V1")
    || 0x00
    || u32be(length(incidentIdBytes))
    || incidentIdBytes
    || u32be(length(rowVersionBytes))
    || rowVersionBytes
```

The marker is exactly `EE-PULSE/WP07/INCIDENT-ETAG/V1`, whose exact hexadecimal
bytes are `45452D50554C53452F575030372F494E434944454E542D455441472F5631`.
The separator after the marker is exactly one `0x00`. `u32be` is exactly four
unsigned big-endian bytes. `incidentIdBytes` is a non-empty Guid formatted only
as lowercase UUID-D: exactly 36 ASCII/UTF-8 bytes. Guid.Empty is rejected before
hashing. The implementation must not use Guid.ToByteArray, TryWriteBytes, a
database GUID byte layout, caller spelling, or culture-sensitive formatting.

`rowVersionBytes` comes from a signed Int64 input in the inclusive range 1
through Int64.MaxValue. It is invariant-culture minimal base-10 ASCII digits:
no sign, leading zero, grouping, whitespace, or locale digits; its length is 1
through 19 bytes. A row version less than or equal to zero is rejected before
hashing. Int64.MaxValue is valid for read/vector generation, but a command that
would advance it fails the checked advancement: no mutation or receipt commits,
and sanitized server-failure handling applies. UTF-8 has no BOM. No JSON
serializer, Unicode normalization, delimiter in place of a length, or
platform-native endianness participates in this encoding.

The final strong ETag is exactly the quoted result returned by
`Wp07DashboardCanonicalizer.EtagFor(canonicalBytes)`: the literal
`"wp07-2b1-sha256-"` prefix, followed by the raw unpadded base64url SHA-256
digest, followed by the closing quote.
Clients treat it as opaque even though this internal generation algorithm is
frozen. Every successful acknowledge, comment, and resolve advances
`row_version` exactly once; current ETag comparison remains mandatory before
state-conflict evaluation. Replay returns `IdempotencyReceipt.ResponseEtag`
exactly and never recomputes it, including for retained receipts after later
state changes. Commit 3C detail reads reuse this provider. A v2 requires a new
marker, vectors, ADR decision, and coordinated rollout.

The following vectors were independently calculated from the specified bytes;
each canonical hexadecimal value is one complete uninterrupted byte string.

Vector 1:

```text
incident ID: 01234567-89ab-cdef-0123-456789abcdef
row version: 1
canonical hex: 45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002430313233343536372D383961622D636465662D303132332D3435363738396162636465660000000131
byte count: 76
raw SHA-256: 7B5C71860C8A67B247BDDC90B7E63522832F598BD627BA44156FD77915A2786C
raw unpadded base64url digest: e1xxhgyKZ7JHvdyQt-Y1IoMvWYvWJ7pEFW_XeRWieGw
final strong ETag: "wp07-2b1-sha256-e1xxhgyKZ7JHvdyQt-Y1IoMvWYvWJ7pEFW_XeRWieGw"
```

Vector 2:

```text
incident ID: 01234567-89ab-cdef-0123-456789abcdef
row version: 123456789
canonical hex: 45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002430313233343536372D383961622D636465662D303132332D34353637383961626364656600000009313233343536373839
byte count: 84
raw SHA-256: B16C53AC0053A82BB3B1AEC63E7647927089D7E79ED93B75426B205FECC97727
raw unpadded base64url digest: sWxTrABTqCuzsa7GPnZHknCJ1-ee2Tt1QmsgX-zJdyc
final strong ETag: "wp07-2b1-sha256-sWxTrABTqCuzsa7GPnZHknCJ1-ee2Tt1QmsgX-zJdyc"
```

Vector 3:

```text
incident ID: 00112233-4455-6677-8899-aabbccddeeff
row version: 9223372036854775807
canonical hex: 45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002430303131323233332D343435352D363637372D383839392D6161626263636464656566660000001339323233333732303336383534373735383037
byte count: 94
raw SHA-256: 54A6D3300D79FB35D122D05A3DCE69BD678CF41D411D13FDE3546FEF6A049B09
raw unpadded base64url digest: VKbTMA15-zXRItBaPc5pvWeM9B1BHRP941Rv72oEmwk
final strong ETag: "wp07-2b1-sha256-VKbTMA15-zXRItBaPc5pvWeM9B1BHRP941Rv72oEmwk"
```

Vector 4:

```text
incident ID: ffeeddcc-bbaa-9988-7766-554433221100
row version: 42
canonical hex: 45452D50554C53452F575030372F494E434944454E542D455441472F5631000000002466666565646463632D626261612D393938382D373736362D353534343333323231313030000000023432
byte count: 77
raw SHA-256: 3E9584F118D29E9D633B68AD31E02E395D78F4DF9865641E1716CCCBBC4D0BA5
raw unpadded base64url digest: PpWE8RjSnp1jO2itMeAuOV149N-YZWQeFxbMy7xNC6U
final strong ETag: "wp07-2b1-sha256-PpWE8RjSnp1jO2itMeAuOV149N-YZWQeFxbMy7xNC6U"
```

#### 5.2 Exact request-fingerprint v1 byte encoding

Every new receipt stores `request_fingerprint_version = 1` and a 32-byte
`request_digest`. Version 1 is exactly SHA-256 over this one unambiguous byte
sequence, with no UTF-8 BOM and no bytes after the sixth field:

```text
ASCII("EE-PULSE/WP07/REQUEST-FINGERPRINT/V1") || 0x00 ||
field(ASCII("POST")) ||
field(ASCII(routeTemplate)) ||
field(ASCII(canonicalIncidentId)) ||
field(ASCII(canonicalActorId)) ||
field(ASCII(parsedStrongIfMatch)) ||
field(canonicalRequestBodyUtf8)
```

`field(bytes)` is exactly the four-byte unsigned big-endian byte length of
`bytes`, followed immediately by `bytes`. Length is the number of bytes, not
UTF-16 code units or Unicode scalars. `canonicalIncidentId` and
`canonicalActorId` are their lowercase UUID-D text. `parsedStrongIfMatch` is
the exact validated strong entity tag including its two DQUOTE bytes. The three
possible ASCII `routeTemplate` values are exactly:

```text
/api/v1/incidents/{id}/acknowledge
/api/v1/incidents/{id}/comments
/api/v1/incidents/{id}/resolve
```

`canonicalRequestBodyUtf8` contains no BOM or whitespace and has exactly one
property in this exact property order:

```text
acknowledge: {"Comment":J(normalizedComment)}
comment:     {"Comment":J(normalizedComment)}
resolve:     {"Note":J(normalizedNote)}
```

`J(value)` is one JSON string token. It begins and ends with ASCII DQUOTE. For
each scalar in `value`, it emits `\"` for U+0022, `\\` for U+005C, and otherwise
emits that scalar's shortest valid UTF-8 encoding directly. The pre-database
semantic validation rejects all JSON-control scalars that would need another
escape, so v1 has no other string escape. Property names, punctuation, and the
absence of whitespace are the literal bytes shown above. No serializer setting,
property enumeration, escaping policy, culture, or request formatting may alter
these bytes.

The `If-Match` value is deliberately part of the fingerprint. The Idempotency-
Key is the receipt lookup key and is compared with stored route, incident,
actor, fingerprint version, and digest; it is not a substitute for those
identity fields. Receipt comparison dispatches on the stored version. The v1
encoder and comparer remain available for every retained version-1 receipt;
there is no fallback encoding or reinterpretation of a stored v1 digest.

#### 5.3 Exact idempotency-key advisory-lock derivation

For every valid command request, the lock identity starts with the exact
validated, case-sensitive Idempotency-Key value after only the already-frozen
HTTP OWS handling. The lock derivation does not case-fold, Unicode-normalize,
trim additional characters, or incorporate actor, route, incident, body,
correlation ID, or If-Match. The canonical lowercase UUID-D header grammar
remains a validation rule; it is not an additional lock-key transformation.

The exact two-integer derivation is:

1. Set the signed Int32 namespace key to decimal `1464873015`, whose unsigned
   hexadecimal representation is `0x57503037` (the ASCII bytes `WP07`). This
   constant is not hashed or configured.
2. Encode the exact Idempotency-Key value as UTF-8.
3. Prefix it with the fixed ASCII bytes
   EE-PULSE/WP07/IDEMPOTENCY\0, where \0 means one byte with value 0x00:
   ASCII-prefix bytes || exact UTF-8 Idempotency-Key bytes.
4. Compute SHA-256 over that concatenation.
5. Read digest bytes 0 through 3 in network/big-endian order as one 32-bit bit
   pattern, then reinterpret that pattern as a signed two's-complement Int32.
   The exact calculation is:

       uint bits = ((uint)digest[0] << 24)
                 | ((uint)digest[1] << 16)
                 | ((uint)digest[2] << 8)
                 | digest[3];
       int derivedKey = unchecked((int)bits);

6. Inside the command transaction, pass the two signed Int32 values as the two
   integer arguments to exactly:

       SELECT pg_advisory_xact_lock(@namespace_key, @derived_key);

`@namespace_key` is always `1464873015`; `@derived_key` is the signed result
above. The fixed namespace, prefix bytes, UTF-8 encoding, SHA-256 algorithm,
byte selection, byte order, and signed reinterpretation make every API instance
derive the same PostgreSQL `(integer, integer)` lock identity for the same exact
Idempotency-Key.

This is a transaction-scoped PostgreSQL advisory lock, global across all
application instances connected to the same PostgreSQL database. It is
acquired inside the command transaction and PostgreSQL releases it
automatically on commit or rollback. The command's bounded database-command
timeout applies while waiting, and the request cancellation token is passed
to the wait and every surrounding database operation. Cancellation or timeout
aborts and rolls back the transaction without inserting a receipt or performing
any command mutation.

A theoretical 32-bit derived-key collision within the fixed namespace serializes
unrelated Idempotency-Key values, but can never give them the same receipt
identity: after the wait, receipt lookup and the
idempotency_receipts primary-key comparison always use the full exact
Idempotency-Key. No incomplete receipt row is inserted. No
in-process semaphore, process-local cache, or distributed service outside
PostgreSQL is authoritative for this serialization.

The existing WP-06 Probe advisory-lock mapping was audited and is preserved
exactly. ProbeTransactionLock uses canonical lowercase UUID-D Probe text with
the separate derivation form:

    SELECT pg_advisory_xact_lock(hashtextextended(@canonical_probe_id, 0));

PostgreSQL defines the two-`integer` advisory-lock key space as non-overlapping
with the one-`bigint` key space. Therefore the WP-07 tuple
`(1464873015, derivedKey)` cannot alias WP-06's existing one-`bigint`
`hashtextextended` Probe lock, regardless of numeric bit patterns. WP-06 is not
changed: it acquires its Probe lock after any required Agent `FOR SHARE` work
and before its projection `FOR UPDATE` and downstream incident-engine work, and
it never acquires the idempotency lock. WP-07 always acquires its two-integer
idempotency lock before its unchanged WP-06 Probe advisory lock, then the
projection row lock and incident row lock. Therefore no path acquires Probe and
then waits for an idempotency lock while another path does the reverse.

The exact command transaction order is frozen as follows:

1. Complete the total routing, correlation, authentication, authorization,
   query, route, header, content-type/body, DTO, and normalization sequence in
   section 1.1. No database command occurs before that sequence succeeds.
2. Begin the PostgreSQL READ COMMITTED database transaction.
3. Derive the exact signed Int32 namespace/derived-key tuple above and acquire
   the transaction-scoped two-integer idempotency advisory lock.
4. Read `idempotency_receipts` by the full exact Idempotency-Key and left-join
   the immutable HumanPrincipal identified by `receipt.actor_id`.
5. If a receipt exists:
   - Require the joined principal to exist, require its ID to equal
     `receipt.actor_id`, and compare its case-sensitive issuer/subject with the
     authenticated pair. Never attach or mutate a principal.
   - Only after actor equivalence, dispatch on the stored fingerprint version
     and perform the complete route, incident, and request-fingerprint
     comparison. A version-1 receipt uses only the exact section 5.1 v1 byte
     encoding, including the normalized original If-Match value.
   - Replay the stored representation for an exact match, or return 409
     idempotency-key-reuse-conflict for any mismatch.
   - Do not acquire the Probe, projection, or incident locks.
   - Do not query mutable outcome/incident/projection/comment/lifecycle state.
   - Call neither `SaveChangesAsync` nor `CommitAsync`; end the read-only
     transaction by rollback/disposal so no mutation can be committed.
6. If no receipt exists:
   - Atomically get or create the HumanPrincipal using the frozen application-
     generated-ID behavior.
   - If needed, perform the non-locking route lookup that identifies the Probe;
     it is not authoritative and occurs before the authoritative Probe lock.
   - Acquire the existing locks in their frozen order: Probe advisory lock,
     projection row lock, then incident row lock.
   - Complete the authoritative locked reads and not-found/consistency checks.
   - Derive the current incident ETag and compare the supplied syntactically
     valid `If-Match` value before evaluating any command-state conflict.
   - Only when the tag is current, evaluate the applicable incident-status or
     underlying-status conflict.
   - Track incident/projection changes, the command-specific child row, and the
     audit event without tracking a new receipt; call the first
     `SaveChangesAsync` inside the explicit transaction.
   - After that flush succeeds, produce and capture the final exact response
     representation once and track the complete receipt last, with
     `request_fingerprint_version = 1` and every response, identity, outcome,
     and timestamp field populated.
   - Call the second `SaveChangesAsync` to insert the receipt, then call
     `CommitAsync` exactly once so both flushes commit atomically.

Only a commit-confirmed successful 200 or 201 response is sent with a receipt.
Validation, authorization, not-found, stale-ETag, state-conflict, and every
pre-COMMIT failure roll back the transaction and insert no receipt or command
mutation. A transport failure or cancellation after `COMMIT` is issued but
before confirmation is indeterminate and follows the retry rule above. No
incomplete receipt row is ever inserted. A receipt primary-key conflict is
only the final database uniqueness backstop; it is not the mechanism for
locking an absent key. The advisory lock serializes the normal same-key race
before either transaction can reach the final receipt insert.

Receipt matching is complete before current-ETag comparison. A matching receipt
replays its stored status, body bytes, Content-Type, and original quoted ETag
byte-for-byte without downstream locks or mutation, even when the incident's
current ETag has advanced. The replay path does not reconstruct a DTO,
re-serialize JSON, recanonicalize, recompute an ETag, or reread mutable outcome
state. Its only non-receipt read is the immutable HumanPrincipal mapping needed
to verify the authenticated actor. A mismatch in actor, route, incident,
body/digest, or the normalized original `If-Match` value remains `409
idempotency-key-reuse-conflict` with no replay and no mutation.

### 6. Transaction and lock order

All public commands use PostgreSQL READ COMMITTED. After the transaction begins,
the authoritative total order is:

1. Acquire the idempotency advisory lock derived from the exact key.
2. Read `idempotency_receipts` by the full exact Idempotency-Key together with
   the receipt actor's immutable HumanPrincipal mapping. If a receipt exists,
   verify actor equivalence and its stored-version request fingerprint, then
   replay or conflict read-only without `SaveChangesAsync` or `CommitAsync`.
3. If no receipt exists, atomically get or create the HumanPrincipal.
4. Acquire the existing WP-06 Probe transaction advisory lock using the
   canonical lowercase Probe UUID.
5. Lock the probe_status_projections row with FOR UPDATE and validate the
   current underlying status/watermark consistency.
6. Lock the target availability_incidents row with FOR UPDATE and re-check its
   identity, status, row version, and Probe relationship.
7. After all required locked reads, compare the syntactically valid supplied
   `If-Match` value with the current incident ETag. Return the frozen `412
   concurrency-conflict` response for a stale value before evaluating any
   command-state conflict.
8. When the ETag is current, evaluate the command-state rules; track the
   incident/projection mutation, immutable command outcome, and audit event,
   then perform the child-first `SaveChangesAsync` without a tracked receipt.
9. After that flush succeeds, produce the exact response, track the complete
   version-1 receipt, and perform the receipt-last second `SaveChangesAsync`.
10. Call `CommitAsync` exactly once; final deferred constraints are checked and
    both EF flushes become durable together.

A receipt-match path ends after step 2's full-key plus immutable-principal read
and comparison: it does not acquire the Probe, projection, or incident locks. A
no-receipt decision is represented only by the absence of the full-key row; the
final primary-key constraint remains only the database uniqueness backstop.

The shared Probe advisory lock is taken before projection and incident row locks,
matching the WP-06 status processor's Probe serialization boundary. A preliminary
non-locking route lookup, if needed to identify the Probe, occurs only after the
no-receipt decision and HumanPrincipal resolution and before the Probe lock. It
is never used as the authority for a mutation; the locked reads are repeated
inside the transaction.
No command holds an incident lock while acquiring a Probe lock. A missing or
inconsistent projection is a sanitized dependency/read failure and makes no
incident, projection, lifecycle, comment, audit, receipt, or HumanPrincipal
mutation.

### 7. Command lifecycle and conflict behavior

| Command | Allowed state | Durable result |
| --- | --- | --- |
| Acknowledge | `Open` only | Require `AcknowledgeIncidentRequest.Comment`; persist its normalized value as `action_note` on one public lifecycle action with `action_kind = 'acknowledgement'` and `reason_code = 'operator-acknowledgement'`, with the acknowledgement state mutation, audit row, and idempotency receipt; return `200`. The acknowledgement comment is not a general incident comment. |
| Add comment | `Open`, `Acknowledged`, or `Resolved` | Under the target incident row lock, insert exactly one immutable general `incident_comments` row and increment `availability_incidents.row_version` exactly once without changing incident status, lifecycle timestamp, actor, acknowledgement, or resolution fields; write the audit row; create no `incident_lifecycle_actions` row and no lifecycle transition; return `201` with the new strong incident ETag, store that exact response and ETag in the receipt inserted last, and commit atomically. |
| Manual resolve | `Open` or `Acknowledged`, with underlying Probe not `Down` or `Recovering` | Require `ResolveIncidentRequest.Note`; persist its normalized value as `action_note` on one public lifecycle action with `action_kind = 'manual_resolution'` and `reason_code = 'manual-resolution'`. Resolve the incident and, under the already-held projection lock, clear `probe_status_projections.open_incident_id` and increment its `state_version` exactly once; persist the audit row and idempotency receipt and return `200`. The resolution note is not a general incident comment. |

`AcknowledgeIncidentRequest.Comment` is required, not optional. Its raw value
must pass unchanged `[Required]` and 1–2,000 UTF-16-code-unit DTO validation;
whitespace-only input fails `[Required]` before `.Trim()`. Normalize a
successfully validated value using .NET `String.Trim()` (`.Trim()`), then apply
the defensive non-empty and prohibited-scalar checks from section 1.1 before
persisting. Persist that normalized value as the acknowledgement action's non-null
`incident_lifecycle_actions.action_note`. It is action-specific text and is
never copied into `incident_comments`.

`ResolveIncidentRequest.Note` is required, not optional. Its raw value must
pass unchanged `[Required]` and 1–2,000 UTF-16-code-unit DTO validation;
whitespace-only input fails `[Required]` before `.Trim()`. Normalize a
successfully validated value using .NET `String.Trim()` (`.Trim()`), then apply
the defensive non-empty and prohibited-scalar checks from section 1.1 before
persisting. Persist that normalized value as the manual-resolution action's non-null
`incident_lifecycle_actions.action_note`. It is action-specific text and is
never copied into `incident_comments`.

General `POST /api/v1/incidents/{id}/comments` creates exactly one
`incident_comments` row, increments the locked incident's `row_version` exactly
once, writes an audit record, and inserts the complete idempotency receipt last;
its raw `AddIncidentCommentRequest.Comment` first passes unchanged `[Required]`
and 1–2,000 UTF-16-code-unit DTO validation, so whitespace-only input fails
before `.Trim()`; its trimmed value then passes the defensive non-empty and
prohibited-scalar checks from section 1.1 before it is persisted;
it changes no incident status or other lifecycle/actor/acknowledgement/
resolution field and creates no lifecycle action or lifecycle transition. The
response carries the new strong incident ETag, and the response plus ETag are
committed with the comment, row-version change, audit record, and receipt
atomically. Only acknowledge and manual resolve create public lifecycle-action
rows.

For a fresh successful manual resolution, the locked projection must currently
reference the target active incident. The same transaction sets
`probe_status_projections.open_incident_id` to `NULL` and advances
`probe_status_projections.state_version` with the exact assignment
`state_version = state_version + 1`; exactly one projection row must be affected.
The incident's `row_version` also advances exactly once as part of its resolution
mutation. The projection update does not change
`underlying_status`, `visible_status`, either consecutive counter, any result or
authority watermark column, or any freshness/heartbeat source column. The
incident resolution mutation, one projection increment, lifecycle action,
audit event, exact response, and receipt-last insert commit atomically. Exact
replay reads only the receipt and its immutable HumanPrincipal mapping for
verification and neither clears nor increments the projection again.

After receipt handling and all required locked reads, every fresh command
compares the syntactically valid supplied `If-Match` with the current incident
ETag before it evaluates command-state conflicts. A stale tag always returns
the frozen `412 concurrency-conflict` response, even when the same locked state
would also reject the command. State conflicts are reached only with a current
tag. This precedence does not alter validation-before-database behavior or
exact replay before current-state reads.

With a current tag, an acknowledge against an already Acknowledged or Resolved
incident returns `409 incident-action-state-conflict`. With a current tag, a
manual resolve against a Resolved incident also returns `409
incident-action-state-conflict`, including the unchanged current ETag, and
makes no incident, projection, lifecycle, comment, audit, principal, or receipt
mutation. The explicit Resolved branch precedes the underlying-status branch
only after the current-ETag comparison.

For an active incident with a current tag whose current underlying Probe status
is `Down` or `Recovering`, manual resolve returns HTTP 409 with code and type
`incident-manual-resolution-state-conflict`, title `Manual resolution is not
currently allowed`, the unchanged ETag in the `ETag` header, and
`currentEtag` plus `incidentId`, `probeId`, `underlyingStatus`, `visibleStatus`,
and `stateVersion` extensions. It makes no incident, projection, lifecycle,
comment, audit, principal, or receipt mutation. The client refetches the
incident and device-status reads and keeps the action unavailable until the
state permits it.

#### 7.1 Conflict, projection, and rollback coverage

The future focused PostgreSQL/integration matrix must cover these exact
combinations and prove before/after structural equality on every non-success
path:

| Command and locked state | Stale syntactically valid tag | Current tag |
| --- | --- | --- |
| Acknowledge; incident `Open` | `412 concurrency-conflict`; no mutation | Success; incident `row_version` advances once and one `acknowledgement` action is committed. |
| Acknowledge; incident `Acknowledged` or `Resolved` | `412 concurrency-conflict`; state conflict is not evaluated | `409 incident-action-state-conflict`; unchanged ETag and no mutation. |
| Manual resolution; active incident; underlying neither `Down` nor `Recovering` | `412 concurrency-conflict`; no mutation | Success; incident concurrency advances once, projection `open_incident_id` becomes `NULL`, projection `state_version` advances once, and all other projection fields are unchanged. |
| Manual resolution; active incident; underlying `Down` or `Recovering` | `412 concurrency-conflict`; manual-resolution conflict is not evaluated | `409 incident-manual-resolution-state-conflict`; unchanged ETag/projection and no mutation. |
| Manual resolution; incident `Resolved`, including when underlying is `Down` or `Recovering` | `412 concurrency-conflict`; neither state conflict is evaluated | `409 incident-action-state-conflict`; the Resolved branch wins and no mutation occurs. |
| General comment; incident `Open`, `Acknowledged`, or `Resolved` | `412 concurrency-conflict`; no mutation | Success; one comment and exactly one incident `row_version` increment, with no lifecycle action or projection mutation. |

Exact replay for all three commands occurs before Probe/projection/incident
locks and returns the original stored bytes and headers without any second
incident or projection increment. Separate manual-resolution cases cover
cancellation before commit, provider/command failure, deferred-constraint
failure, and explicit transaction rollback after the tentative incident and
projection mutations; each must leave the incident, full projection row,
HumanPrincipal, lifecycle action, audit event, and receipt unchanged. A
server-rejected commit is likewise not durable. A transport failure or
cancellation after `COMMIT` is issued but before confirmation is indeterminate;
a same-key retry exact-replays a committed result or executes fresh when no
receipt exists. Cancellation after a confirmed successful commit is recovered
only by exact replay of that already committed result.

Manual resolution never replaces WP-06 confirmed recovery. A confirmed-recovery
resolution keeps `resolved_by = NULL` and `resolution_note =
'confirmed-recovery'`; it advances incident concurrency through the existing
WP-06 transaction and is visible through the lifecycle union.

### 8. Public lifecycle union and protected cursors

The lifecycle-events read combines two immutable sources:

| Source | `source_rank` | Exact `IncidentLifecycleResponse` mapping |
| --- | ---: | --- |
| Existing `incident_lifecycle_events` | `0` | Engine `Opened` maps to `Type = "opened"`, `ReasonCode = "failure-threshold-met"`; `Resolved` maps to `Type = "resolved"`, `ReasonCode = "recovery-threshold-met"`; `Occurrence` maps to `Type = "occurrence"`, `ReasonCode = "recovery-failed"`. These are the existing row's exact `source_reason_code` tokens. `ActorId` and `Comment` are `null`; other engine provenance remains internal. |
| New `incident_lifecycle_actions` | `1` | `action_kind = 'acknowledgement'` and `reason_code = 'operator-acknowledgement'` map to `Type = "acknowledged"`, `ReasonCode = "operator-acknowledgement"`, the stored `ActorId`, and `Comment = action_note`. `action_kind = 'manual_resolution'` and `reason_code = 'manual-resolution'` map to `Type = "manually-resolved"`, `ReasonCode = "manual-resolution"`, the stored `ActorId`, and `Comment = action_note`. General comments are read from `incident_comments` and are not lifecycle actions. |

The database query is a `UNION ALL` into one normalized lifecycle shape before
ordering or applying the page limit. It never takes an independent page from
each source and merges already-truncated pages. The total descending order is:

```text
occurred_at DESC,
source_rank DESC,
canonical lowercase event_id DESC
```

`source_rank` is the fixed signed integer literal `0` for every engine branch
and `1` for every public-action branch. Because ordering is descending, a public
action sorts before an engine event at the same `occurred_at`; rows within that
source then sort by canonical lowercase UUID-D `event_id` descending. The source
rank and event ID together remain a total tie-breaker when timestamps are equal.
UUID comparison is ordinal over canonical lowercase UUID-D text. The response exposes only the frozen
`IncidentLifecycleResponse` shape, not `source_rank`.

Incident lists use `openedAt DESC, incidentId DESC` by default; device incident
history uses the same order. Comments use `createdAt DESC, commentId DESC`.
Every alternate direction reverses the timestamp and retains the ID/source
tie-breakers.

Every cursor is a protected, versioned, base64url envelope with a server-side
HMAC-SHA-256 integrity value. It binds the route/resource, incident or Device
ID, normalized filters, sort, schema version, and last complete seek key. It
contains no raw comments, OIDC claims, tokens, or authorization values. A bad
encoding, version, or MAC returns `400 invalid-cursor`; a valid cursor used with
different bound filters or sort returns `400 cursor-filter-mismatch`.

The lifecycle cursor contains the complete last tuple
`(occurredAt, sourceRank, eventId)`, so paging across the total union cannot
duplicate or omit a row because it came from a different source. The comments
and incident cursors use their corresponding complete tuples.

Focused union coverage must seed equal timestamps in both sources, including
UUIDs whose relative order differs within each rank, and assert the exact
`occurred_at DESC, source_rank DESC, event_id DESC` sequence. Page boundaries
must be placed immediately before and after the cross-source rank transition
and between equal-rank UUIDs. Following the protected cursor's complete tuple
must return every row exactly once, with no gap or duplicate; a cursor whose
rank or event ID is altered without a valid MAC is `400 invalid-cursor`.

### 9. Audit, privacy, cancellation, and no-mutation rules

Each successful state-changing command writes one append-only audit event in the
same transaction as the incident change where applicable, command-specific
public lifecycle action or general comment, and receipt. The audit row contains
only the approved action/entity
metadata, safe server correlation ID, UTC occurrence time, and existing safe
source metadata. It does not contain before/after JSON, raw comment/note text,
issuer, subject, email, display name, role, authorization headers, tokens,
Idempotency-Key, If-Match, or SQL/provider details.

The safe server-generated lowercase UUID-N correlation ID is used in the public
response, Problem Details, and action audit row. Caller-supplied correlation or
tracing values are never authoritative and are not persisted.

Request cancellation is propagated to every database statement and transaction
operation. Cancellation before commit rolls back all command work and does not
produce a 503 response or an unavailable event. Cancellation after a successful
commit may prevent the response from reaching the caller, but retrying the same
Idempotency-Key exact-replays the committed result. Database/provider failures
are sanitized and never expose exception, SQL, entity, or stack details.

The following operations are mutation-free: authentication/authorization
failure, malformed route/body/header input, malformed cursor, invalid
If-Match, a stale If-Match detected after the required locked reads on a fresh
command, an idempotency reuse conflict, any current-tag command-state conflict,
and any missing/inconsistent read model. These paths create or change no
principal, incident, projection, lifecycle action, comment, audit event, or
receipt. A durable receipt used only to exact-replay an earlier committed result
is not a new mutation; failed command paths do not finalize one.

### 10. Future Commit 3A-3G eight-route acceptance matrix

The following matrix is required future Commit 3A-3G coverage. No row has run or
passed at this documentation checkpoint. Every one of the eight routes must
independently cover its exact allowed roles; unauthenticated requests; every
representative forbidden role; the applicable section 1.1 precedence stages;
not-found behavior where a resource identity is present; sanitized database or
provider failure; caller cancellation; an unrelated provider cancellation that
must follow sanitized provider-failure handling rather than masquerade as caller
cancellation; safe response correlation; logging privacy; and before/after
database snapshots.

For every route, a pre-database failure must prove zero database commands. Every
failed read or command must prove no mutation. Success must prove the frozen
status, headers, DTO shape, canonical representation, and privacy boundary.
Commands additionally require transaction rollback/snapshot preservation,
receipt replay/conflict/race coverage, and the exact two-flush/one-commit
boundary. They also require the command-only `PrincipalIdentityResolver`
boundary, v1 fingerprint vectors/version dispatch, and both determinate and
indeterminate commit-outcome coverage. Read routes omit Idempotency-Key,
`If-Match`, content type, body, DTO body, normalization, and principal-resolution
stages when inapplicable; command routes omit `If-None-Match`; those omissions
never reorder the remaining stages.

| Frozen route | Authorization and ordered input coverage | Success, ETag, and representation coverage | Route-specific persistence, paging, and failure coverage |
| --- | --- | --- | --- |
| `GET /api/v1/incidents` | `incidents.read`: Viewer, Operator, Engineer, Administrator, Auditor; unauthenticated and forbidden-role cases; allowlisted filter/page/sort query validation first; no route value, conditional header, or body stage. | Canonical `CursorPage<IncidentResponse>` success; frozen filters and default order; no aggregate ETag. | Protected cursor integrity and filter/sort binding, page boundaries without gaps/duplicates, empty result semantics, sanitized dependency failure, both cancellation categories, correlation/log privacy, zero pre-database commands, mutation-free snapshots. |
| `GET /api/v1/incidents/{id}` | All `incidents.read` roles plus unauthenticated/forbidden; reject any query, then canonical incident ID, then `If-None-Match`; no body stage. | Canonical `IncidentResponse` with strong quoted ETag and cache header; matching conditional request returns bodyless `304`; nonmatch returns canonical `200`. | Missing incident `404`, malformed conditional combinations, provider failure/cancellations/privacy, zero pre-database commands for earlier failures, and unchanged snapshots for `200`, `304`, and every failure. |
| `GET /api/v1/devices/{id}/incidents` | All `incidents.read` roles plus unauthenticated/forbidden; allowlisted status/time/page/sort query first, then canonical Device ID; no conditional header or body stage. | Canonical cursor page in frozen incident order; no aggregate ETag. | Missing Device `404`; protected cursor route/Device/filter/sort binding and boundary traversal; sanitized failures, both cancellation categories, privacy, zero pre-database commands, and mutation-free snapshots. |
| `GET /api/v1/incidents/{id}/lifecycle-events` | All `incidents.read` roles plus unauthenticated/forbidden; allowlisted page/sort query first, then canonical incident ID; no conditional header or body stage. | Canonical `CursorPage<IncidentLifecycleResponse>` with exact discriminator/reason mapping and no aggregate ETag. | Missing incident `404`; engine rank `0` and public rank `1`, equal-timestamp cross-source ordering, complete protected `(occurredAt, sourceRank, eventId)` cursor identity, no gaps/duplicates, append-only rejection stability, sanitized failures/cancellations/privacy, zero early database work, unchanged snapshots. |
| `GET /api/v1/incidents/{id}/comments` | All `incidents.read` roles plus unauthenticated/forbidden; allowlisted page/sort query first, then canonical incident ID; no conditional header or body stage. | Canonical `CursorPage<IncidentCommentResponse>`; every `AuthorId` required/non-null surrogate UUID; no aggregate ETag. | Missing incident `404`; protected incident/filter/sort and `(createdAt, commentId)` cursor binding, no gaps/duplicates, sanitized failures/cancellations/privacy, zero early database work, mutation-free snapshots. |
| `POST /api/v1/incidents/{id}/acknowledge` | Operator and Administrator only plus unauthenticated/forbidden; after role authorization, invalid `PrincipalIdentityResolver` identity is `403 invalid-incident-actor-identity`, then forbidden query, route, missing/invalid Idempotency-Key `400 invalid-idempotency-key`, `If-Match`, content type, strict malformed-UTF8/surrogate JSON read `400 invalid-json`, raw `[Required]`/1–2,000 DTO validation, then trim/scalar validation in exact order. | Canonical `200 IncidentActionResponse` and resulting strong ETag; stale `412` precedes current-tag state `409`; success stores the exact acknowledgement action/reason and required actor/note. | Missing incident, v1 fingerprint vector/version replay, immutable-principal verification, same-key and forced-collision races, two EF flushes/one commit, append-only receipt/action rejection, strict JSON malformed/paired-supplementary/direct-UTF8-supplementary/U+FFFD coverage proving preserved text/fingerprint/response/replay with safe correlation/privacy/zero commands, provider failure/cancellations/privacy, determinate rollback, and indeterminate post-COMMIT retry/snapshot preservation. |
| `POST /api/v1/incidents/{id}/comments` | Operator and Administrator only plus unauthenticated/forbidden; after role authorization, invalid `PrincipalIdentityResolver` identity is `403 invalid-incident-actor-identity`, then forbidden query, route, missing/invalid Idempotency-Key `400 invalid-idempotency-key`, `If-Match`, content type, strict malformed-UTF8/surrogate JSON read `400 invalid-json`, raw `[Required]`/1–2,000 DTO validation, then trim/scalar validation in exact order. | Canonical `201 IncidentCommentResponse`, required non-null `AuthorId`, and resulting strong ETag; stale tag is `412`. | Missing incident; v1 fingerprint/replay/conflict/principal verification and races; exactly one incident `row_version` increment on fresh success, no lifecycle action or projection mutation; two flushes/one commit; append-only rejection; strict JSON malformed/paired-supplementary/direct-UTF8-supplementary/U+FFFD coverage proving preserved text/fingerprint/response/replay with safe correlation/privacy/zero commands; provider failure/cancellations/privacy; determinate rollback and indeterminate post-COMMIT retry snapshots. |
| `POST /api/v1/incidents/{id}/resolve` | Operator and Administrator only plus unauthenticated/forbidden; after role authorization, invalid `PrincipalIdentityResolver` identity is `403 invalid-incident-actor-identity`, then forbidden query, route, missing/invalid Idempotency-Key `400 invalid-idempotency-key`, `If-Match`, content type, strict malformed-UTF8/surrogate JSON read `400 invalid-json`, raw `[Required]`/1–2,000 DTO validation, then trim/scalar validation in exact order. | Canonical `200 IncidentActionResponse` and resulting strong ETag; stale `412` precedes current-tag Resolved or Down/Recovering `409`; success stores exact manual-resolution action/reason and required actor/note. | Missing incident; v1 fingerprint/replay/conflict/principal verification and races; fresh success clears `open_incident_id` and advances projection and incident versions once while preserving other projection fields; two flushes/one commit; append-only rejection; strict JSON malformed/paired-supplementary/direct-UTF8-supplementary/U+FFFD coverage proving preserved text/fingerprint/response/replay with safe correlation/privacy/zero commands; determinate cancellation/failure rollback and indeterminate post-COMMIT retry/snapshot preservation. |

Combination cases must include unauthorized plus malformed query/route/header/body
input; forbidden-role plus invalid identity; authorized invalid identity plus
malformed query/header/body; and valid-identity requests with multiple
simultaneous invalid values. The earliest frozen response alone is observable.
Command success, replay, conflict, losing race, failure after each EF flush,
deferred-constraint rejection, server-rejected commit, and indeterminate
post-COMMIT transport/cancellation failure must each prove the exact permitted
final snapshot and same-key retry behavior. This matrix supplements rather than
weakens every focused coverage requirement elsewhere in this ADR.

### 11. Implementation subdivisions 3A-3G

The later runtime work is subdivided as follows. None of these subdivisions has
started in this checkpoint.

Commit 3B keeps `IncidentEtagV1`, fingerprint v1, advisory-key derivation, the
coordinator, handlers, contexts, results, and related runtime types internal to
EePulse.Api. The new API `Properties/AssemblyInfo.cs` grants both
`InternalsVisibleTo("EePulse.UnitTests")` and
`InternalsVisibleTo("EePulse.IntegrationTests")`; both are test-to-API
dependencies only and create no production circular dependency. No public
facade, public Contracts DTO, reflection/dynamic boundary, or test-only HTTP
route is introduced. `EePulse.IntegrationTests` may directly resolve and
compose the internal coordinator and typed generic handler/context/result types
through its PostgreSQL `WebApplicationFactory` test host. Its existing project
reference to EePulse.Api requires no project-file change. HTTP command routes
remain deferred to Commit 3E/3F, while mandatory PostgreSQL coordinator
verification remains in Commit 3B. Commit 3C reuses the API-internal ETag
provider.

| Subdivision | Later scope | Required boundary |
| --- | --- | --- |
| 3A | Additive runtime persistence: UTF8 preflight with the separate disposable non-UTF8 fixture, public lifecycle actions, comments, idempotency receipts, exact reciprocal composite child linkage, action-kind/reason constraints, append-only triggers/EF behavior, and UTF-16/.NET Trim PostgreSQL checks. | One new additive migration; exact Up/Down order; no historical migration rewrite and no OpenAPI change. |
| 3B | Command `PrincipalIdentityResolver` boundary, HumanPrincipal resolution, request-fingerprint v1, global two-integer idempotency-key advisory-lock serialization, immutable-principal replay verification, shared Probe lock, incident lock, and atomic command transaction coordinator. | Exact application-generated `@id` SQL, v1 length-prefixed bytes, signed Int32 advisory-lock derivation, two EF flushes/one commit, determinate/indeterminate commit outcome, ETag-before-state precedence, and lock order in sections 4-6; rollback and race behavior are mandatory. |
| 3C | Incident list, detail, and device incident-history reads. | Existing DTOs, role policies, filters, ETag detail semantics, no aggregate ETag, and protected cursors. |
| 3D | Total lifecycle union and comment reads. | Exact engine rank `0`/public-action rank `1`, public discriminator/reason mapping, global union ordering, complete seek tuple, immutable rows, and cursor filter binding. |
| 3E | Acknowledge and add-comment commands. | Strong If-Match, idempotency, trimmed text, acknowledgement lifecycle action, general-comment row, audit atomicity, and correct 200/201 results. |
| 3F | Manual resolve, exact conflict branches, projection unlink/version advancement, safe error/cancellation handling, and no-mutation evidence. | Stale ETag precedes state conflicts; current-tag Resolved and Down/Recovering conflicts remain distinct and return unchanged ETags. |
| 3G | Focused unit/integration/runtime verification and Lead review of the frozen behavior. | The complete section 10 eight-route matrix plus UTF8, append-only, two-flush, race, replay, rollback, cursor, privacy, and cancellation coverage is mandatory. Generated OpenAPI inclusion is a separate later checkpoint. |

### 12. Future production and test allow-lists

The following are the exact future Commit 3 implementation allow-lists. They do
not authorize any change in this documentation-only freeze.

Future production paths:

- `src/backend/EePulse.Api/EePulse.Api.csproj`
- `src/backend/EePulse.Api/Properties/AssemblyInfo.cs` (new)
- `src/backend/EePulse.Api/Program.cs`
- `src/backend/EePulse.Api/Dashboard/IncidentEndpoints.cs` (new)
- `src/backend/EePulse.Api/Dashboard/IncidentRuntimeService.cs` (new)
- `src/backend/EePulse.Domain/Status/ProbeStatusProcessingModels.cs`
- `src/backend/EePulse.Domain/Status/IncidentRuntimeModels.cs` (new)
- `src/backend/EePulse.Domain/Auditing/AuditEvent.cs`
- `src/backend/EePulse.Infrastructure/Persistence/EePulseDbContext.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Configurations/ProbeStatusProcessingConfiguration.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Configurations/IncidentRuntimeConfiguration.cs` (new)
- `src/backend/EePulse.Infrastructure/Persistence/Configurations/AuditEventConfiguration.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Migrations/*_WP07Phase2B2IncidentRuntime.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Migrations/*_WP07Phase2B2IncidentRuntime.Designer.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Migrations/EePulseDbContextModelSnapshot.cs`

Future test paths:

- `tests/EePulse.UnitTests/EePulse.UnitTests.csproj`
- `tests/EePulse.UnitTests/Wp07IncidentRuntimeTests.cs` (new)
- `tests/EePulse.IntegrationTests/Wp07IncidentRuntimeApiTests.cs` (new)
- `tests/EePulse.IntegrationTests/Wp07IncidentRuntimePersistenceTests.cs` (new)

No contract DTO, policy name, or generated artifact is to be changed merely to
implement these routes. Any genuine wire-contract change requires a separate
compatibility decision.

### 13. Current exclusions and artifact boundary

This ADR authorizes no current source or test work. The current Commit 3 change
may touch only these four documentation paths:

- `docs/adr/ADR-013-wp07-phase2b2-incident-runtime.md` (new)
- `docs/api/wp07-dashboard-contract-design.md`
- `docs/implementation-status.md`
- `docs/requirements-traceability.md`

The following are excluded from this documentation-only freeze:

- `docs/api/openapi-v1.json`
- `src/backend/EePulse.Contracts/Dashboard/Wp07DashboardContracts.cs` and all
  other contract paths
- `src/backend/EePulse.Infrastructure/Persistence/Migrations/20260914145214_WP07Phase2B2IncidentFoundation.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Migrations/20260914145214_WP07Phase2B2IncidentFoundation.Designer.cs`
- `src/backend/EePulse.Infrastructure/Persistence/Migrations/EePulseDbContextModelSnapshot.cs`
  for this current freeze
- all WP-06 status/incident engine implementation, migration, and test paths
- all SignalR/hub paths
- all `src/web/**` frontend/UI paths
- audit listing/read implementation paths
- WP-08 notification paths
- WP-09 reporting and retention paths

Generated OpenAPI inclusion, SignalR, frontend/UI, audit listing, WP-08, and
WP-09 remain outside this ADR. The checked-in OpenAPI artifact must remain
154,533 bytes with SHA-256
`44F2C9D1EB902E1EC44C6395305F328F262A3D592E9EDF7BA40724C030DE435C`.

## Consequences

The design gives public incident actions a durable, replayable command boundary
without weakening WP-06's immutable engine history. Public lifecycle actions and
comments can be read consistently across two immutable sources, while protected
seek cursors preserve a total order. The cost is an additive persistence slice,
receipt retention, explicit Probe serialization, and future verification of
Unicode, race, rollback, privacy, and cancellation behavior.

This is a documentation-frozen design checkpoint, not runtime completion. Commit
2 remains the verified baseline, Commit 3 runtime implementation and verification
remain unstarted, and WP-07 overall remains incomplete.
