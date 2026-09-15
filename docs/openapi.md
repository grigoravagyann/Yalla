# OpenAPI

The React monorepo generates its TypeScript types from this API's OpenAPI document with
`openapi-typescript`. The document is a dependency of the frontend build, not a convenience: a
schema that is wrong produces types that compile and are quietly untrue.

## Where the document is served

| Environment | Swagger JSON URL |
|---|---|
| Local (`dotnet run`, HTTP) | `http://localhost:5086/swagger/v1/swagger.json` |
| Local (`dotnet run`, HTTPS) | `https://localhost:7289/swagger/v1/swagger.json` |
| Local, from another device on the wifi | `http://<LAN-IP>:5086/swagger/v1/swagger.json` - the exact address is logged at startup |
| Staging / QA, when `Swagger:Enabled` is on there | `https://<host>/swagger/v1/swagger.json` |

The local ports come from `applicationUrl` in `src/Yalla.Api/Properties/launchSettings.json` - see
README.md for which profile binds what, and why HTTP is for local device testing only. The path is
always `/swagger/v1/swagger.json`; the UI, for humans, is at `/swagger`.

## The frontend's committed copy

The frontend monorepo **commits** two generated files in `packages/api/src/generated/`, so the
workspace typechecks with no backend running and its CI needs no network:

| File | What it is |
|---|---|
| `swagger.json` | The document exactly as fetched, pretty-printed with two-space indentation. `check-gateway-schema.mjs` reads its `required` lists, which the types alone cannot express. |
| `schema.ts` | The TypeScript types `openapi-typescript` generates from that same fetch. Never edited by hand. |

Both are written in one run by `pnpm api:generate` (`packages/api/scripts/generate.mjs`), from
`http://localhost:5086/swagger/v1/swagger.json` by default. Another address goes in `--url` or
`YALLA_OPENAPI_URL`:

```
pnpm api:generate
pnpm api:generate --url http://192.168.1.42:5086/swagger/v1/swagger.json
```

Run the API first, in Development, where Swagger is on. Prefer the HTTP port: the HTTPS one serves the
same document, but a development certificate the toolchain does not trust turns a schema refresh into
a TLS argument.

A committed copy is only as current as the last regeneration. **Regenerate whenever a backend change
touches a route, a request or a response,** and commit both files with the frontend change that uses
them.

## The CI artifact

`.github/workflows/backend.yml` uploads the document every backend build serves, as the artifact
**`openapi-swagger`** (`swagger.json`). `SwaggerExposureTests` writes it to
`artifacts/openapi/swagger.json` during the test run - `./verify.sh` produces the same file locally -
in the committed copy's layout: two-space indentation, LF line endings.

That is the document to compare the frontend's `swagger.json` with, without starting an API: download
the artifact for the backend commit the frontend targets and diff the two. A difference means the
committed copy is stale and `pnpm api:generate` is due.

## Availability

Swagger is **off by default**. It is served only when the `Swagger__Enabled` setting is `true`;
`appsettings.Development.json` sets it, and nothing else does.

When it is off, `/swagger/index.html` and `/swagger/v1/swagger.json` return **404** - not 401 and
not a redirect. A 404 does not reveal that Swagger is there at all, which is the point for a
production ordering and payment API.

`Swagger__AllowedIps` optionally restricts `/swagger` to a list of addresses, so a QA environment
can expose it to an office or VPN address without exposing it to the internet. An empty list means
no IP restriction; a request from an address that is not on a non-empty list also gets 404.

```bash
Swagger__Enabled=true
Swagger__AllowedIps__0=203.0.113.14
Swagger__AllowedIps__1=203.0.113.15
```

## What the schema guarantees

These are the properties the generated client depends on. Each is covered by a test in
`SwaggerExposureTests`, so a change that breaks one fails the build rather than the frontend.

**Explicit operation ids, in camelCase.** Every endpoint declares one with `WithName`, because the
generated function names come from them. An auto-generated id gives you
`ApiBranchesBranchIdTablesTableIdSeatWalkInPost`; an explicit one gives you `seatWalkIn`.

**Every response declared.** Including the ones that are not errors in any useful sense: the
**409** from `TableStateConflictException` and the **422** from `InvalidTableTransitionException`.
The frontend has to treat "someone just took that table" as a normal outcome and redraw the floor,
and it can only do that if the shape is in the generated types.

**One error shape, everywhere.** Every failure is an RFC 7807 problem document served as
`application/problem+json`, with four extension members:

| Member | Use |
|---|---|
| `code` | Stable kebab-case slug. **This is what to branch on** - never the message. |
| `traceId` | Correlates with the single log entry written for the request. |
| `errors` | Field-level complaints, when the failure was about the payload. |
| `context` | Machine-readable facts. A table conflict puts `currentStatus` here so the tablet can redraw. |

**Correct nullability.** `SupportNonNullableReferenceTypes` and `NonNullableReferenceTypesAsRequired`
are on, so a non-nullable C# `string` does not become `string | null` in TypeScript and a required
property does not become optional. Without them every generated model is a sea of `?` and the
frontend writes null checks for values that cannot be null.

**Required nullable values are required.** A `[Required]` member that is nullable in C# only so the
attribute can tell absent from a default - a booking's `date` (`DateOnly?`) and `time`
(`TimeOnly?`), a release's `outcome` - is published in `required` and not nullable, by
`RequiredValueSchemaFilter`. Swashbuckle does not read `[Required]` off a positional record
parameter and published them optional, so a generated client could build a booking with no date or
time, satisfy its type, and fail at runtime.

**Integer enums, with names.** Enums serialise as integers, matching the database - a renamed
member must not orphan existing rows. So that they are not *anonymous* integers, every enum schema
carries `x-enum-varnames` (which `openapi-typescript` reads to emit named members) and a
written-out legend in its description:

```jsonc
"Yalla.Domain.Enums.TableStatus": {
  "enum": [1, 2, 4, 5],
  "type": "integer",
  "description": "The physical state of a table … Values: 1 Free, 2 Held, 4 Occupied, 5 OutOfService.",
  "x-enum-varnames": ["Free", "Held", "Occupied", "OutOfService"]
}
```

Note the gap. `3` was `Reserved` and is permanently retired rather than reused, so it never appears -
see the remarks on `TableStatus`.

**XML doc comments.** Generation is on for every project and all three assemblies' XML files are fed
to Swashbuckle, so endpoint summaries and DTO property comments reach the document and the
generated types.

**Tags matching the three surfaces**, plus authentication: `diner`, `staff`, `admin`, `auth`.
`getBranchFloor` carries both `staff` and `diner`, because both apps call it.

**JWT bearer security definition**, so Swagger UI has a working Authorize button. Get a token from
one of the four flows under `/api/auth` - see [auth.md](auth.md) - and paste it without the
`Bearer` prefix.

Outside `/api/auth`, exactly one operation needs no token: `getBranchAvailability`. Browsing needs
no account, so a generated client should not assume every non-auth operation carries one. Every
other operation answers **401** without a token and **403** with the wrong kind of one - a staff
token on a diner route, or a tab participant on either.
