# Branch features — what each one gates, and what runs on

2026-10-27. The platform admin allows or blocks ten `BranchFeature`s per branch (storage, presets, the admin routes:
[platform-admin.md](platform-admin.md) "Branch features"). This page is the enforcement: which action each switch
refuses, which reads hide instead of refusing, and which work already in flight finishes as if nothing changed.

**The rule.** A *new* action needing a blocked feature answers **409 `feature-not-enabled`**, `context.feature` (the
number) and `context.branchId` ([error-contract.md](error-contract.md)). It is checked before the owner's own switch,
so a client tells "locked by the platform" (`feature-not-enabled`) from "switched off by the venue"
(`takeaway-not-accepted`, `pre-orders-not-accepted`, `web-bookings-not-accepted`, `guests-cannot-write`, ...).
A diner or public **read** never refuses: it reports the effective state (allowed AND the owner's switch) or shows
nothing. Blocking never touches the owner's settings or any row; allowing the feature again brings everything back.
The console's settings screens stay readable and writable - the console draws them locked - and a setting saved
while blocked takes effect only once the feature is allowed.

Where the check lives: `Branch.RequireAllowed` / `BranchFeatures.Require` when the branch row is loaded,
`Services/BranchFeatureGate` (one column read) when only its id is. Domain-event handlers and outbox handlers that
must skip a blocked feature ask in the handler.

| # | Feature | Refused (new actions) | Hidden or switched off (reads) | Runs on (in flight) |
|---|---|---|---|---|
| 1 | `AppBookings` | Creating a booking with `channel` App, and moving one made from the app (`ReservationSlotRules.RequireChannelAllowed`, from `ReservationCreation` and `ReservationChanges`). | `acceptsAppBookings` false on the listing and the page (phase 1). | Existing bookings: cancelling, seating, no-show, reminders, "on your way?". |
| 2 | `WebBookings` | Creating with `channel` Web, and moving a web booking - from the page or from its `/booking/{token}` manage link. | `acceptsWebBookings` false; the manage link's `canChange` false. | The manage link's read and cancel; everything above. |
| 1+2 | either | A booking that names no channel (`Unknown`, or `Staff` on the diner route) needs at least one of the two; refused as `AppBookings` when neither is allowed - leaving the field out must not book where the platform allows no diner bookings. | | |
| 3 | `PreOrders` | A guest's save and edit of a pre-order, from the app and the manage link (`PreOrderService.ReplaceCoreAsync`). | `preOrderEditableUntilUtc` null on the diner's booking and the manage link; `policy.acceptsPreOrders` false on the page. | Clearing a pre-order (a guest is never stuck with one). The floor's rail: listing, send to the kitchen, "not coming", and the held ticket on to the tab. "On your way?" at 45 minutes for a waiting pre-order. |
| 4 | `Takeaway` | Placing an order (`TakeawayService.PlaceAsync`). | `GET /api/diner/branches/{id}/takeaway` answers `enabled: false` (not an error); `takeaway.enabled` false on the listing and the page. | Orders already placed: the guest's cancel, the floor's accept / decline / ready / collect / no-show, the five-minute expiry sweep, the guest's pushes, the staff push, the 2 h / 15 min "I'm coming" asks. |
| 5 | `TableService` | Every tab open, join, read and mutation - exactly where the Paid tier was checked (`TabLookups`, `TabQuery`, `TabBillingQuery`). | | An open tab blocks the switch itself: the platform cannot turn table service off while one is open or closing. |
| 6 | `GuestMessages` | A typed message from the guest, and from anybody at the venue - managers included (`GuestMessageService`). | `guestsCanWrite` / `canWrite` false on both sides of a thread; the floor's Messages badge, its "oldest unanswered" remind and the tiles' unread counts read 0; the guest's venue-message push offers no typed reply. | Threads stay readable - the guest's, the tablet's inbox and the console's history. The booking's "Ask: are you coming?" and the guest's answers (on my way, running late) still go in the booking's thread. No `guestMessages` staff push is staged for a guest's message or answer (`GuestMessageStaffPush`), and one staged before the block is dropped at send time. |
| 7 | `Reviews` | Writing and revising a review, reporting one (`BranchReviewService`), and the venue's reply (`ReviewModerationService.ReplyForVenueAsync`). | The public and diner reads show none: no recent reviews, an empty review page, `reviewCount` 0, no `rating`, and no badge earned by reviews (`popular` by sittings still shows). The diner's own `GET .../review` is 404, as if never written. | The rows. Taking a reply down, hiding or showing a review, and the platform's moderation. The console's review list. |
| 8 | `StaffPhone` | Creating a pairing code; previewing or redeeming one (a code issued before the block included); PIN sign-in and renewal on a phone; **every authorised call** from a paired phone, with its session or its device token (`FeatureGateMiddleware`). | | Signing out (an anonymous route). The phone's push subscription is kept but nothing is pushed to it (`StaffPushHandler`); the branch's tablets are pushed as before. |
| 9 | `KitchenDisplay` | A Kitchen-role PIN sign-in and renewal; every authorised call from a kitchen session (`FeatureGateMiddleware`); the Kitchen role's reads of `GET /api/branches/{id}/orders` and moves on `POST /api/orders/{id}/status` (`TabOrderService`). | | **The two routes are the floor's too** - waiters read the queue, serve and void through them - so only the Kitchen role is refused. Waiters and managers finish the tickets the kitchen had: ready, served, voided. |
| 10 | `Reports` | `GET /api/branches/{id}/reports/{occupancy,reservations,revenue,menu,staff}`, the JSON and `format=csv` alike (`ReportQuery.ResolveAsync`). | A venue rollup (`rollUpVenue=true`) asked at a branch that has reports leaves out the siblings that do not. | The History (`activity.csv`) and Staff → Results (`staff-performance.csv`) are not reports here and stay open. |

## Staff phones and kitchen sessions: why a middleware

A phone or a kitchen screen is a whole surface, not one route: the brief is that *its calls* get `feature-not-enabled`.
The token check that already runs on every staff request (`RejectRevokedDevicesAsync`, one cached read per device:
`ITokenAuthorityCheck.GetLiveDeviceAsync` → `StaffDeviceStanding`, kind + branch + allowed bits) decides it, and
`Middleware/FeatureGateMiddleware` - after authentication, before the policies - throws the refusal, so it is the
same 409 problem every other refusal is. A bearer handler can only fail a token into a 401, which the apps would read
as "signed out". Anonymous routes run as for anybody.

The device read is cached for `TokenAuthorityCheck.CacheWindow` (5 s). A platform change of a branch's features
(`PUT /api/platform/{branches,venues}/{id}/features`) evicts its devices, so the next call is refused at once, as a
revocation is.

## Effects and the domain-event rules

Nothing here writes a side effect in a service. The two pushes that skip a blocked feature ask in their own handler:
`DomainEvents/Messages/GuestMessageStaffPush` (does not stage) and `Notifications/StaffPushHandler` (drops a staged
`guestMessages` push, and leaves phones out of the audience). Reminders and asks on in-flight work - the booking
reminder, "on your way?", the takeaway asks, the pre-order question - run on by design.
