# AGENTS.md

Conventions for AI agents working in this repository: the map, the rules this project learned the hard way,
and step lists for the usual changes. Depth lives in `docs/`; this file only points. ASP.NET Core 9 minimal
APIs, EF Core, SQL Server. The clients (diner app, staff tablet and phone, venue console, public page) live
in the frontend repo, `grigoravagyann/YallaFrontend`.

## Repo map

| Path | What lives there |
|---|---|
| `src/Yalla.Domain` | Entities and rules, no I/O. `Enums/` (persisted as int), `Common/` (`Guard`, `FieldLengths`, `Entity` - which raises domain events - `IDomainEvent`), `DomainStateException.cs`, `FieldValidationException.cs`, `Audit/` (`ActivityEntry`, `ProcessedCommand`), one folder per area (`Occupancy` = bookings and tables, `Tabs`, `Takeaway`, `Messages`, `Staff`, `Venues`, `Identity`, `Menus`). |
| `src/Yalla.Application` | Contracts only: service interfaces, DTO records, application exceptions, one folder per area. `Abstractions/` (`IClock`, `ICurrentActor`, `IDomainEventHandler<T>`), `Messaging/OutboxContracts.cs`, `Activity/ActivityLogContracts.cs`, `Notifications/` (`NotificationText`, `StaffPushText`, `StaffPushContracts`), `Reservations/BranchZone.cs`. |
| `src/Yalla.Infrastructure` | Implementations. `Services/` (one class per use case: `ReservationCreation`, `TabOrderService`, sweeps), `DomainEvents/<Area>/` (domain-event handlers: the side effects of a change), `Persistence/` (`YallaDbContext`, `DomainEventDispatcher`, `Configurations/`, `Migrations/`, `TableLock`, `ReservationWriter`, `StaffPushSignals`), `Notifications/` (one `IOutboxHandler` per file), `Messaging/` (outbox dispatcher and loop), `Identity/` (sign-in, tokens), `DependencyInjection.cs` (every registration). |
| `src/Yalla.Api` | `Program.cs` (maps every group), `Endpoints/<Area>Endpoints.cs` + `<Area>Requests.cs`, `Authorization/YallaPolicies.cs` + `ScopeHandlers.cs`, `Errors/` (`ErrorCodes` - every problem code declared once - `ProblemCatalogue`, `ApiExceptionMapper`, `ProblemShapes`), `Filters/` (request validation, OpenAPI filters). |
| `tests/Yalla.UnitTests` | The only test project. Root: pure unit tests. `Integration/`: real SQL Server plus the in-process API. `Contracts/`: contract tests every double must pass. |
| `scripts/ci/`, `verify.sh` | Shard planner and timings; `./verify.sh` (Git Bash) runs exactly what CI runs. |
| `deploy/`, `SCHEMA.md` | Compose/Caddy/env (`.env.example` is checked by `DeployEnvExampleTests`); the schema and its decisions. |

Docs (`docs/`): `auth.md` identities, tokens, sessions, Telegram proof - `reservations.md` booking conflict rule
and locking - `tabs.md` scan, join, visibility - `tab-totals.md` which commands write the tab row - `billing.md`
(+ `billing-vectors.json`) money arithmetic - `takeaway.md` - `messages.md` guest threads - `notifications.md`
outbox, pushes, staff push, the diner feed - `activity.md` History, action codes, retention, transfers -
`reports.md` - `domain-events.md` raising events, handlers, the rules, which verticals are migrated -
`error-contract.md` every named problem code and its context - `openapi.md` the document and the
frontend's copy - `contract-tests.md` why doubles have contracts - `public-surface.md` anonymous routes -
`diner-browse.md` diner real-data contract, rate limits, dev seed - `platform-admin.md` - `menu-completeness.md`.

## Golden rules

- **Never read dotnet user-secrets** (`dotnet user-secrets list`, `secrets.json`): they hold the JWT key, the admin
  password and the Telegram bot token. Tests and `pnpm dev:stack` never need them.
- **Never touch the `Yalla` database** (the developer's own, in `appsettings.Development.json`). Tests create
  `Yalla_Tests_<guid>`; run the API against a throwaway database (`pnpm dev:stack --db <name>`, below).
- **Ports**: 5086, 5173, 8081 and 8095 belong to the user's own processes - never bind them. 5199 is the
  `dev:stack` API default; pick another (`--api-port`) if one is up already.
- **The merge gate builds with `-p:TreatWarningsAsErrors=true`** (`Directory.Build.props` says false so a local
  build passes mid-thought). An unresolvable `<see cref>` in a doc comment fails CI. Build strictly before pushing.
- **Time comes from `IClock`** (`Application/Abstractions/IClock.cs`), never `DateTime.UtcNow`, in domain,
  services, handlers and sweeps; timers use the injected `TimeProvider`. Instants are stored UTC
  (`Guard.NotLocalTime`). Tests pin time: `YallaApiFactory.StartingAt(utc)` / `StartingAtYerevan(new TimeOnly(15, 0))`,
  `TestClock`/`FakeClock`, and prove independence by running your class with `YALLA_TEST_NOW=05:30` and `=23:50`
  (`HH:mm` Yerevan today, or an ISO instant; see `tests/Yalla.UnitTests/TestNow.cs`).
- **Branch-local time** (Asia/Yerevan in fixtures): convert only through `BranchZone.For(tz)` (`ToUtc`,
  `ToLocal`, `LocalDateAt`) or `Domain/Venues/BranchTime`; bookings store the UTC instant and the local date and
  time the guest saw; "today" is the branch's service day (`OpeningCalendars.ServiceDayAsync`), which runs past
  midnight. Test branches are open 10:00-23:00 every day.
- **Problem codes are a contract**: a slug in `ErrorCodes` is never reworded; enums go on the wire as integers;
  deliberate refusals are `DomainStateException` (409) or a named exception, never a bare
  `InvalidOperationException` (that is a logged 500).
- **One unit of work**: the outbox row, the activity entry, the diner feed row and the staff push are added to the
  same `YallaDbContext` and saved by the caller's single `SaveChangesAsync` (or a lock scope's `CommitAsync`).
  `IOutbox` and `IActivityLog` deliberately have no save; `StaffPushSignals.Stage` runs inside `SaveChangesAsync`.
- **Side effects hang on domain events** (`docs/domain-events.md`): the aggregate raises (`Entity.Raise`) from the
  method that makes the change, and handlers in `Infrastructure/DomainEvents/` write the activity entry, outbox
  message, feed row, tab-stream event and staff push in the same save - dispatched by `SaveChanges` before it writes
  (and by `TabLedger` before it numbers the stream). Migrated: **tab orders**. On a migrated vertical never write a
  side effect in a service; on the others, prefer moving the vertical over adding another hand-written call.
- **Idempotency**: device writes carry `clientCommandId` (`IClientCommandRequest` in
  `Endpoints/TableStateRequests.cs`, enforced by `ClientCommandIdFilter`); the service records it in
  `ProcessedCommands` (or the row's own unique `ClientCommandId`) and answers a replay with the first result -
  see `ReservationChanges`, `ReservationAnswers`, `PreOrderService`.
- **Locking** (re-read everything you check *inside* the lock): `TableLock.AcquireAsync` -> `TableLockScope.CommitAsync`
  (`UPDLOCK, HOLDLOCK` on the table, bounded wait, `TableLockTimeoutException` = 503 retryable); every reservation
  insert/move goes through `ReservationWriter.InsertUnderTableLockAsync`/`MoveUnderTableLockAsync`;
  `BranchWriteLock.AcquireAsync` for whole-branch config writes (timeout = 409 `concurrent-update`);
  `DinerAccountLock.RequireLiveAsync`; an app lock (`sp_getapplock`) in
  `TabAdmissionCommands.UnderTabAdmissionLockAsync`; unique-index races via `UniqueViolation.IsOn(ex, DatabaseIndexNames.X)`.
  Concurrency tests need the real server - that is why CI runs SQL Server.

## Recipes

### Add an endpoint
1. Contract in `Application/<Area>/`: interface + DTO records (model: `IActivityQuery` + `ActivityReadModels.cs`).
   Implement in `Infrastructure/Services/`, register in `Infrastructure/DependencyInjection.cs` (`AddInfrastructure`).
2. Request record in `Api/Endpoints/<Area>Requests.cs`: `[Required]` on nullable value types (`bool? KitchenReady`
   in `StaffDeviceRequests.cs`), `[NotEmpty]` on ids. Validation runs for every route (`RequestValidationFilter`
   on the root group in `Program.cs`). Device writes implement `IClientCommandRequest` and the group adds
   `.AddEndpointFilter<ClientCommandIdFilter>()`.
3. Map it in `<Area>Endpoints.cs` (new file: add `api.Map<Area>Endpoints()` in `Program.cs`). Model: `ActivityEndpoints`:
   `app.MapGroup("/api/branches/{branchId:guid}").WithTags(EndpointConventions.AdminTag).RequireAuthorization(YallaPolicies.ManagerOrAbove, YallaPolicies.BranchScoped)`.
4. Policies (`YallaPolicies`): `WaiterOrAbove`, `KitchenOrAbove`, `ManagerOrAbove` (role only, pair with a scope),
   `BranchScoped` (route `{branchId}`, `{tableId}` or `{tabId}`), `VenueScoped` (`{venueId}` or `{branchId}`),
   `PlatformAdminOnly`, `VenueStaffAccount`, `VerifiedDiner` (any diner account - read `PhoneVerifiedAtUtc` when proof
   matters), `TabParticipant`/`TabParticipantCanOrder`/`TabParticipantMutating`, `StaffDeviceSession`, `StaffDeviceToken`.
   Routes addressed by a bare entity id have no branch to compare: inject `IStaffBranchGuard` and call
   `RequireAtBranchAsync`/`RequireAsync` where the entity is loaded (`StaffBranchGuard.cs` - also the second line
   under the policy).
5. OpenAPI on every route: `.WithName("camelCaseUniqueId")`, `.WithSummary(...)`, `.WithDescription(...)` (markdown:
   query, shape, refusals, who may call), `.Produces<T>()`, `.ProducesProblemDetails(status, "what it means here")`
   or `.ProducesProblem<XxxProblem>(...)` when the client reads `context`.
6. Tests: an HTTP integration test; `EveryRouteIsAuthorizedTests` fails on a route with no policy (anonymous only under
   `/api/public/` or in its `Anonymous` list with a reason); add a row to `AuthorizationMatrixTests` for console
   branch routes; `RequestShapeContractTests` scans every body the tests post against the request record (a key the
   record lacks fails); `SwaggerExposureTests` checks ids, tags, enums, required-ness and problem shapes.

### Add a problem code
The code is declared once, in `Api/Errors/ErrorCodes.cs`; everything else is read or generated from that
declaration (`ProblemCatalogue`): the mapper's status, `TitleFor`, the OpenAPI root extension
`x-problem-codes` (the frontend generates `packages/api/src/generated/problemCodes.ts` from it) and the
catalogue table in `docs/error-contract.md`.
1. Exception carrying the context: derive from `DomainStateException` for a state refusal (model:
   `NoShowUndoWindowClosedException` in `Domain/Occupancy/BookingBlock.cs`), or an Application exception (model:
   `TabNotTransferableException` in `Application/Activity/TableTransferContracts.cs`). Field problems: throw
   `FieldValidationException`, or `ArgumentException` with a camelCase `nameof(param)` -> 422 with `context.field`.
   A code the exception mints (a booking rule's `ErrorCode`, a sign-in reason passed to
   `AuthenticationFailedException`) is a literal or a `const`, never built at run time.
2. `Api/Errors/ErrorCodes.cs`: a kebab-case `const` with a doc comment, `[Problem(status, "one-line meaning",
   Title = "...")]` (no `Title` = the slug humanised) and one `[ProblemContext("name", typeof(T))]` per context
   member, in wire order (`Optional = true` when left out of some contexts, `typeof(Guid?)` / `Nullable = true`
   when it can be null; enums, arrays and records are fine). No `TitleFor` arm: there is none any more.
3. `Api/Errors/ApiExceptionMapper.cs`: a `Refuse(ErrorCodes.X, e.Message, context: ...)` arm placed ABOVE any arm
   for a type it derives from (`DomainStateException`, `KeyNotFoundException`, `ArgumentException`). Never write a
   status there: `Refuse` takes it from the declaration.
4. If clients read the context in the endpoint's own responses: `XxxContext` + `XxxProblem : ProblemShape` in
   `Errors/ProblemShapes.cs`, and `.ProducesProblem<XxxProblem>` on the endpoints.
5. Regenerate the catalogue table: `YALLA_UPDATE_DOCS=1 dotnet test tests/Yalla.UnitTests --filter
   "FullyQualifiedName~ProblemCatalogueTests"` (it fails when stale), and add a dated prose section to
   `docs/error-contract.md` when the code needs more than a line. `ProblemCatalogueTests` also fails on a code thrown
   anywhere that is not declared, a declared code nothing sends, and a context member the mapper writes that the
   declaration does not list (or a required one it leaves out). Add a case in `ApiExceptionMapperTests` (model:
   `Guest_message_refusals_map_to_their_own_codes`) and an HTTP test asserting the `code`.
6. Frontend: regenerate (`pnpm api:generate`) - the new code appears in `generated/problemCodes.ts`, and the
   frontend's check lists it as unmapped until a typed error handles it.

### Add an entity, a column, or an enum (+ migration)
1. Entity in `Domain/<Area>/` (private setters, guards in the constructor, lengths from `FieldLengths`), `DbSet` in
   `YallaDbContext`, `Persistence/Configurations/<Entity>Configuration.cs` (picked up by `ApplyConfigurationsFromAssembly`).
   Index names in `DatabaseIndexNames`. Enums stay int; never renumber a member.
2. Scaffold offline - `YallaDbContextFactory` means no startup project and no database:
   `dotnet tool restore && dotnet tool run dotnet-ef migrations add <PascalName> --project src/Yalla.Infrastructure --output-dir Persistence/Migrations`.
   Read the generated `Up`/`Down`; backfill existing rows in `Up`; `Down` must work over real rows.
3. Gate: `dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/Yalla.Infrastructure` says none.
4. A new enum in `Yalla.Domain.Enums`: pin each value in `PersistedEnumValueTests` and add it to `covered` in
   `Every_domain_enum_is_covered_by_this_test`. Pin new members of existing enums too.
5. Data-moving migrations get a rollback test (model: `GuestMessagesMigrationRollbackTests`: throwaway database,
   `IMigrator.MigrateAsync(target)`, seed with raw SQL, migrate down to the one before, up again, drop in `finally`).
   Columns on tables the builders insert into must be added to `Integration/LaterColumns.cs` for older-schema tests.
6. After merging master that brought another migration, regenerate yours on top: take master's snapshot
   (`git show origin/master:<snapshot path> > <snapshot path>`), delete your two migration files, add again (as
   merge `0607257` did for `ActivityHistory`).

### Raise a domain event and handle it
1. The event: a past-tense record in `Domain/<Area>/<Aggregate>Events.cs`, `: IDomainEvent`, carrying the aggregate,
   what a handler cannot read back afterwards (where it was, what came off, the reason as given) and `OccurredAtUtc`
   (an `IClock` instant passed in). Model: `Domain/Tabs/TabOrderEvents.cs`.
2. Raise it from the domain method that makes the change, after the change, on the aggregate root the context tracks
   (`Raise(new TabOrderMoved(...))` in `TabOrder.MoveTo`). Refusals and replays raise nothing; a replay of history raises
   nothing (`TabOrder.ArriveFromHeldTicket`). Make the non-raising route impossible (`TabOrderLine.Void` is `internal`).
3. The handler: `internal sealed class X(...) : IDomainEventHandler<TEvent>` in `Infrastructure/DomainEvents/<Area>/`,
   injecting what a service would; add or change tracked rows, never save, read the tracker before querying
   (`TabOrderReads`); a lock held to the commit calls `db.EnsureTransactionAsync` first (model: `OrderReadyNotice`).
4. Register it in `DependencyInjection.AddDomainEventHandlers`, in the order the event's side effects should be written
   (activity, stream, outbox, staff push). The test fixture composes the same list (`SqlServerFixture.CreateUnitOfWork`).
5. Tests: `DequeueEvents()` after the domain call in a unit test (`TabOrderTests`); a service or HTTP test asserting the
   rows. A bare `fixture.CreateContext` refuses events - drive the change through a service the fixture composes. When
   moving a vertical, write its characterisation snapshot first (model: `TabOrderSideEffectsSnapshotTests`).

### Add an outbox message (push to a diner) + feed entry
1. `OutboxMessageTypes.<Name> = "area.what"` in `Application/Messaging/OutboxContracts.cs`, documenting its key.
2. Enqueue from a **domain-event handler** of the event that causes it: `outbox.Enqueue(type, new { ... }, clock.UtcNow, OutboxMessageTypes.KeyFor("order", id, "ready"))`.
   Model: `DomainEvents/TabOrders/OrderReadyNotice` (on `TabOrderMoved` to Ready). On a vertical not yet on events, the
   service enqueues before its save (model: `ReservationNotices.TellDinerUnblocked`); withdraw with
   `outbox.CancelAsync(OutboxMessageTypes.PrefixFor("reservation", id))`.
3. Handler `Infrastructure/Notifications/<Name>Handler.cs : IOutboxHandler` (one per file): `Payload.Read<T>`,
   `Payload.LocaleForAsync`, text in `NotificationText` (hy/ru/en); re-check the subject is still true; throw = retry.
   Model: `DinerBookingsUnblockedHandler`. Register `services.AddScoped<IOutboxHandler, <Name>Handler>()`.
4. The in-app feed: a `DinerNotices.<Name>(db, ...)` method, called beside the enqueue (the same handler), and a
   `DinerNotificationKinds` const (`Domain/Identity/DinerNotification.cs`). A diner's entry and push are written under
   their account lock: `db.EnsureTransactionAsync` then `DinerAccountLock.IsLiveAsync` (as `OrderReadyNotice` does). Kinds are in the `CK_DinerNotifications_Kind` check, so a new kind needs
   a migration whose `Down` deletes that kind's rows first.
5. Anything over an hour overdue is dead-lettered (`OutboxOptions.StaleAfterMinutes`): a deadline promised to someone
   is a sweep, not a message. Tests that count outbox rows filter by `Type`. Update `docs/notifications.md`.

### Add a staff push kind
1. A new power-of-two `StaffPushKinds` member (`Domain/Enums/IdentityEnums.cs`); decide `Defaults`, update `All`; pin it.
2. Stage it from a **domain-event handler** of the event the floor is told about: `StaffPushSignals.StageOne(db, kind,
   id, $"{StaffPushSignals.KeyPrefix}<what>:{id}", clock.UtcNow)` (append `:{StaffPushSignals.Again()}` when it can
   happen again) - model: `DomainEvents/TabOrders/KitchenReadyStaffPush` (or `DomainEvents/Calls/TableCallStaffPush`).
   Not in services. The older kinds left are still detected in `StaffPushSignals.StageDetected` from the change tracker
   until their verticals move.
3. `StaffPushHandler`: `KindName`, the subject switch (return null when no longer true), text in `StaffPushText`.
4. The switch: `StaffNotificationPreferences` + `StaffNotificationSettings` (`StaffPushContracts.cs`), both mappings in
   `StaffPushService`, the request in `StaffDeviceRequests.cs` and `StaffDeviceEndpoints.cs`.
5. Tests in `StaffPushTests`; `docs/notifications.md` "Staff push". Grep `KitchenReady` to see every site.

### Record an activity entry
1. `ActivityActions.<Area><Verb> = "area.verb"` (stable: the console routes and renders on it), the `ActivityCategory`,
   an `ActivitySubjects` type - all in `Application/Activity/ActivityLogContracts.cs` / `Domain/Enums/ActivityEnums.cs`.
2. Record it from a **domain-event handler** of the change: inject `IActivityLog` and call
   `await activity.RecordAsync(new ActivityRecord(category, action, subjectType, id) { BranchId = ..., SubjectCode = ..., AmountAmd = ..., IsMoney = ..., Before = ..., After = ..., Summary = new { ... } }, ct)`.
   Model: `DomainEvents/TabOrders/TabOrderActivity` (one class, one entry per tab-order event). Who and which device are
   read by the log from the request, so attribution is the same as from a service; a sweep sets `BySystem = true`, a
   manage link `ByGuest = true`. On a vertical not yet on events, the service records it before its save (models:
   `TakeawayActivity.RecordAsync`, `BookingActivity`) - and a change with one path and one entry (a setting) may stay so.
3. `IsMoney` (payments, discounts, comps, voids, write-offs, takeaway collected) keeps it
   `ActivityEntry.MoneyRetentionDays` (1095) instead of `RetentionDays` (365); `ActivityRetention` purges on the
   outbox loop.
4. List the code in `docs/activity.md` section 3. Service tests build the log with `SqlServerFixture.CreateActivityLog`.

### Add a background sweep
1. `<Name>Sweep` (scoped; `YallaDbContext` + `IClock`; `RunOnceAsync(ct)` returns a count) and
   `<Name>HostedService : BackgroundService` (own scope per pass, `PeriodicTimer(interval, TimeProvider)`, failure logged
   and waited out). Model: `ApprovalExpirySweep` + `ApprovalExpiryHostedService` + `ApprovalExpiryOptions` (0 = off,
   negative = startup error). Cheap and daily: ride the outbox loop like `ActivityRetention`.
2. Register options, sweep and `AddHostedService` in `DependencyInjection.cs` (the ApprovalExpiry block).
3. Switch it off in `YallaApiFactory`'s settings (the test database is shared, as `ApprovalExpiry:SweepIntervalSeconds = "0"`);
   test a pass by calling `RunOnceAsync` (see `SqlServerFixture.CreateApprovalExpirySweep`), the loop with `FakeClock.Provider`.
4. Take the same locks as the request path; record activity with `BySystem = true`.

### Write an integration test
- Class: `[Collection(SqlServerCollection.Name)] public sealed class XTests(SqlServerFixture fixture)`; each test
  `[SkippableFact]` starting with `Skip.If(!fixture.IsAvailable, fixture.SkipReason);`.
- Host: `await using var factory = new YallaApiFactory().WithDatabase(fixture.ConnectionString).StartingAtYerevan(new TimeOnly(15, 0));`
  (`.With(key, value)` for settings, `.WithServices(...)` for doubles). Data: `await using var db = fixture.CreateContext(factory.Clock);`
  then `AuthTestData.CreateBranchAsync(db)` (branch, waiter, manager with real credentials), `TestBranchBuilder`,
  `TestMenuBuilder`, `ConsoleTestData.AddBookingAsync`, `ReviewTestData`. Service-level: the fixture's factories, e.g. `fixture.CreateReservationService(...)`,
  `fixture.CreateTabService(db, clock, TestActor.Waiter(id))`.
- Sign in: console `StaffAuthTests.SignInManagerAsync(factory, branch)` or `AuthTestData.SeedOwnerAsync`/`SeedGeneralManagerAsync`
  + `AuthTestData.SignInAsync(factory, account)`; platform `AuthTestData.CreatePlatformAdminAsync` + `SignInAsync`;
  tablet `StaffAuthTests.EnrolDeviceAsync` (device token) / `SignInWaiterAsync` (PIN session); kitchen
  `AuthTestData.SeedKitchenAsync` + `SignInKitchenAsync`; staff phone `StaffPhoneTests.PairWaiterAsync`; diner
  `DinerFeedTestData.SignInDinerAsync` (proved) / `RegisterUnprovedAsync`; tab guest `AuthTestData.CreateOpenTabAsync` + `JoinTabAsync`.
  Then `factory.CreateClientWithToken(token)`.
- Moving `factory.Clock` mid-test: mint tokens first, move back rather than forward past their expiry.

## Running things

- Build like CI: `dotnet build Yalla.sln -c Release -p:TreatWarningsAsErrors=true`. Everything CI checks:
  `./verify.sh` (`--no-test` for build + migrations only).
- One class: `dotnet test tests/Yalla.UnitTests --filter "FullyQualifiedName~Integration.StaffTodayTests"`. Integration
  tests find SQL Server at `localhost`, `localhost\SQLEXPRESS` or `(localdb)\MSSQLLocalDB` (else set
  `YALLA_TEST_SQL_SERVER`) and **skip** when none answers - a skip proves nothing, and CI fails on any skip.
- The full suite takes roughly 25 minutes serially (2,075 tests on 2026-10-24), about 3 locally in parallel; run the classes you touched plus the
  guard classes above. CI (`.github/workflows/backend.yml`): `build` (locked restore, strict build, model check, shard
  plan) -> 4 `test (shard n)` jobs by class (`scripts/ci/test_shards.py`, balanced by `test-timings.json`) ->
  `build-and-test`, the required gate, which checks every listed test ran exactly once.
- OpenAPI: `SwaggerExposureTests.The_served_document_is_written_to_artifacts_for_CI_to_upload` writes
  `artifacts/openapi/swagger.json` (CI artifact `openapi-swagger`). The frontend commits
  `packages/api/src/generated/swagger.json` + `schema.ts` from `pnpm api:generate --url http://localhost:5199/swagger/v1/swagger.json`
  against a running Development API (default URL is 5086, the user's). See `docs/openapi.md`.
- The whole stack, from the frontend checkout: `pnpm dev:stack --backend <this worktree> --db Yalla_Agent_<topic>`
  (API 5199, web 5192, Metro 8110; `--only api`, `--api-port`, `--mock`, `--help`). Always pass `--db`: without it
  the API uses the `Yalla` database.

## Contract-first features

When a feature spans both repos, the first backend commit is the contract: routes, request and response records, policies,
OpenAPI metadata and problem codes, with the service stubbed (an Application interface whose Infrastructure
implementation returns fixed data), plus the guard tests green (`EveryRouteIsAuthorizedTests`, `SwaggerExposureTests`,
`RequestShapeContractTests`). Push it on a branch; the frontend generates its types from it on day one, then the
logic, migration and real tests follow on the same branch without changing the shapes.

## Git

- Never `git checkout`/`restore`/`reset`/`stash`/`clean` on files. To take one side of a conflict:
  `git show <ref>:<path> > <path>`, or regenerate the file.
- Work in a worktree (`git worktree add ../Yalla-<topic> -b <type>/<topic> origin/master`); the default branch is `master`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; PR bodies end with
  `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- The frontend CI runs its live jobs against the backend branch **with the same name** as the frontend branch (or a
  `backend-ref: <ref>` line in its PR body). Name both branches the same, and do not delete the backend branch until
  the frontend PR has merged.

## Coding Tasks

When spawning Claude Code sessions for coding work, tell the session to use gstack skills.
Open the prompt with `Load gstack.` so the session pulls in the skill suite, then name the
skill to run.

Examples:

- **Security audit** — `Load gstack. Run /cso`
- **Code review** — `Load gstack. Run /review`
- **QA test a URL** — `Load gstack. Run /qa https://...`
- **Build a feature end-to-end** — `Load gstack. Run /autoplan, implement the plan, then run /ship`
- **Plan before building** — `Load gstack. Run /office-hours then /autoplan. Save the plan, don't implement.`
