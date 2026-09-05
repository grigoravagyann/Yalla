# OpenAPI

The React monorepo generates its TypeScript types from this API's OpenAPI document with
`openapi-typescript`. The document is a dependency of the frontend build, not a convenience: a
schema that is wrong produces types that compile and are quietly untrue.

## Where to point `pnpm api:generate`

| Environment | Swagger JSON URL |
|---|---|
| Local (`dotnet run`, HTTP) | `http://localhost:5086/swagger/v1/swagger.json` |
| Local (`dotnet run`, HTTPS) | `https://localhost:7289/swagger/v1/swagger.json` |
| Local, from another device on the wifi | `http://<LAN-IP>:5086/swagger/v1/swagger.json` - the exact address is logged at startup |
| Staging / QA | `https://<host>/swagger/v1/swagger.json` |

The local ports come from `src/Yalla.Api/Properties/launchSettings.json`; the path is always
`/swagger/v1/swagger.json`. The UI, for humans, is at `/swagger`.

Prefer the HTTP port for generation. The HTTPS one serves the same document, but a development
certificate the toolchain does not trust turns a schema refresh into a TLS argument.

```jsonc
// package.json in the monorepo
{
  "scripts": {
    "api:generate": "openapi-typescript http://localhost:5086/swagger/v1/swagger.json -o packages/api/src/schema.d.ts"
  }
}
```

Run the API first. There is no committed copy of the document to generate from - a checked-in
schema is a schema that is stale the first time somebody forgets to regenerate it.

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
