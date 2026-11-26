# Resource Booking Portal

* **Author:** Joe Luo
* **Status:** Approved
* **Date:** 2026-11-25

---

## Overview

An internal web portal where employees book shared company resources (meeting rooms, pool cars, catering) and admins control what can be booked and under which rules. Employees book from a free/busy overview through one booking form. An assistant can turn a loosely worded request into a proposal in that same form; the employee always confirms. Admins manage users, resources and rules through forms and see utilisation and history.

This build is a working demonstration. Sign-in and email are mocked, and anyone with the link can use it. Where production would differ, this document says so. The ideation record and the decisions behind this design are in [concepts_brief.md](concepts_brief.md).

## The Problem

Without a shared system, resources get booked through a mix of calendars, spreadsheets and messages to an office manager. That causes four problems:

* **Double bookings.** Two people turn up for the same room or car, because nothing refuses the second booking.
* **Rules applied from memory.** "The car must be back an hour before the next trip", "the boardroom is for four hours at most", "the kitchen takes five orders a day": these rules live in people's heads and are applied unevenly.
* **No usage data.** Nobody can say how much a room or car is used, so the company cannot tell whether to convert a room or buy a second car.
* **Slow search for a vague need.** "A room for 8 tomorrow morning, and lunch for them" means checking several resources one by one.

Goals 1, 2, 5 and 6 address these four problems in order.

## Goals

1. **No double booking.** The database refuses any overlapping booking for the same resource and unit. That covers a car's turnaround gap and the same kitchen unit on the same day. The server alone assigns every booking's unit (`unit_no`: 0 for rooms and cars, a free unit for a kitchen); a client never supplies it. Measured by 20 concurrent `POST /api/bookings` requests for one slot: exactly one returns 201 and nineteen return 409.
2. **Refusals the user can act on.** Every refusal names the limit it hit: "Thames seats 8", "Main Kitchen takes 5 orders a day", "Pool Car 1 is booked then, or within its 60-minute gap between bookings". Measured by one test per refusal code that checks both the code and the message.
3. **Both journeys work first.** The employee and admin critical user journeys (CUJs 1 to 5, below) pass end to end through forms in CI, and CUJ 1 passes against the deployed app, before any assistant work starts.
4. **Easy to change.** Adding a rule means adding a resource field (entity, DTO, migration, form), one check, one refusal code and one test. Which rule fields each resource type uses is set in one place in Shared.
5. **Usage is visible.** Admins see utilisation for any period, using the formula under Utilisation. Measured by ratio tests, including one for a multi-day car booking.
6. **Vague requests become proposals.** The assistant turns a free-text request into pre-filled booking forms. It never books anything itself, and every conversation is traced. Measured by CUJ 6 passing against a scripted model stub; the eval cases run on demand against the real model.

## Non-Goals

* Real SSO and email. Production would use Microsoft Entra ID sign-in and send email through Microsoft Graph. Here, sign-in lists seeded users and notices go to an in-app outbox.
* More than one site or time zone. One company zone, Europe/London.
* Approval workflows, recurring bookings, and check-in with release of no-shows.
* Catering capacity in portions per day. Capacity here is orders per day; a kitchen-day portion counter is the upgrade path.
* Hard deletion of users or resources, and admins editing or cancelling other people's bookings one at a time.
* Assistant write tools, a custom agent loop, and evals in CI.
* Richer agent tracing with agent-flight-recorder (a separate trace-recording tool), and a self-improving loop in which the evals and a coding agent propose prompt and tool fixes.

## Open Questions

None. Platform assumptions that are not yet verified are listed under Risks & Mitigations, each with a fallback.

## Critical User Journeys (CUJs)

**Seed data.** Every CUJ starts from a fresh seed, with dates relative to today in Europe/London.

* **People:** Ana and Ben (employees; Ben has two future bookings) and Sam (admin), all on `sonicjoy-solutions.com`.
* **Every resource:** bookable up to 90 days ahead; rooms and the car for 15 minutes at least.
* **Thames:** meeting room, 8 seats, open 08:00 to 18:00, 4 hours at most.
* **Severn:** meeting room, 12 seats, open 08:00 to 18:00, with a booking by Ana in three days.
* **Pool Car 1:** car, 4 seats, open 07:00 to 19:00, 60-minute turnaround, booked tomorrow from 09:00 to 11:00.
* **Main Kitchen:** catering, 5 orders a day, 25 portions per order, open 08:00 to 16:00.
* **Past bookings:** a week of past bookings, including a three-day car booking, for utilisation.

### CUJ 1: Employee books from the overview

* **Given**: Ana is signed in, and Thames is free tomorrow from 14:00 to 15:00.
* **When**: she selects that slot on the overview, enters 6 attendees and confirms. She then orders 6 portions from Main Kitchen, delivered to Thames at 14:00.
* **Then**: both bookings appear in her bookings. Everyone sees the Thames slot as busy and "Main Kitchen: 4 of 5 orders left on this day" for tomorrow.
* **When**: Ben tries Thames from 14:30 to 15:30.
* **Then**: he gets "Thames is already booked then" (409) and a "Find alternatives" link that lists rooms free for that window.

### CUJ 2: A rule refuses a booking, with the reason

* **Given**: Ana is signed in.
* **When**: she tries Thames tomorrow from 10:00 to 11:00 for 12 attendees, then Thames from 15:00 to 18:30 for 6, then Pool Car 1 tomorrow from 11:30 to 13:00.
* **Then**: each is refused with its reason: "Thames seats 8" (422), "Thames closes at 18:00" (422), and "Pool Car 1 is booked then, or within its 60-minute gap between bookings" (409). Nothing is stored.

### CUJ 3: Admin sets up a user and a resource

* **Given**: Sam is signed in as admin.
* **When**: Sam adds Cara as an employee and adds car Pool Car 2 (a 60-minute gap, 3 days at most). Cara then signs in and tries to book Pool Car 2 for 4 days.
* **Then**: Cara can sign in. Her booking is refused with "Pool Car 2 can be booked for 3 days at most". History lists both additions under Sam's name.

### CUJ 4: Admin retires a resource and deactivates a leaver

* **Given**: the seed.
* **When**: Sam sets Severn's last day to tomorrow, then deactivates Ben.
* **Then**:
  * Ana's Severn booking is cancelled and she sees a notice.
  * Ben's future bookings are cancelled. Sam sees how many, and History lists them; Ben gets no notice, because he can no longer sign in.
  * Severn accepts no booking after tomorrow and still appears in utilisation.
  * Ben's open session is rejected on its next request.

### CUJ 5: Admin checks utilisation and history

* **Given**: the seed's past bookings.
* **When**: Sam opens Utilisation for last week, then History.
* **Then**: each resource shows booked hours over open hours as a percentage; Main Kitchen shows unit-days used over unit-days available. History shows who added, changed, retired, booked or cancelled what, and when.

### CUJ 6 (second priority): Employee books through the assistant

* **Given**: Ana is signed in.
* **When**: she types "a room for 8 tomorrow morning, and lunch for them at 12:30".
* **Then**: she gets two proposals: a room that is free tomorrow morning, and a Main Kitchen order of 8 portions delivered to it at 12:30. Each opens in the booking form, pre-filled, and nothing is booked until she confirms it.
* **When**: she replies "make it 10 people".
* **Then**: new proposals replace both. Confirming them books them, and each booking is linked to its proposal. The trace shows each step and its outcome. In CI, this journey runs against a scripted model stub.

## Technical Plan (Architecture)

### Components

```text
Browser: Blazor WebAssembly + Fluent UI (ResourceBooking.Client)
   |  JSON over HTTPS, auth cookie
   v
ASP.NET Core host (ResourceBooking)
   /api/* minimal API endpoint files (admin create/update and reports live here)
   BookingService ------> BookingRules.Check   (pure, no I/O)
     (create, edit, cancel, CancelFutureBookings, proposal confirmation)
   AvailabilityService    (free/busy, free resources; used by the overview,
                           "Find alternatives" and the propose tools)
   AssistantService ----> IChatClient (Microsoft.Extensions.AI):
                           FunctionInvocation -> TracingChatClient -> OpenAI
   |
   v
EF Core + AuditInterceptor --> PostgreSQL (exclusion constraint, checks)
```

`ResourceBooking.Shared` holds the DTOs, enums, refusal codes, the map from resource type to rule fields, and the validation attributes. Each write endpoint runs those attributes, and the request's own `IValidatableObject` rules, through `Validator` (`ApiResults.Invalid`) and returns a 400 validation problem before anything else; `DataAnnotationsValidator` runs the same attributes in the forms. The Blazor Web App template is used with WebAssembly interactivity. One ASP.NET Core project serves the API and the client from one origin. Prerendering is off, so components run only in the browser.

### Data model

Tables use snake_case (EFCore.NamingConventions). Enums are stored as text. Ids are UUIDv7 generated in code; seeded rows use fixed ids.

* **users**: `id`, `email` (stored lower-case, unique), `display_name`, `role` (`Employee` | `Admin`), `is_active`.
* **resources**:
  * `id`, `name` (unique), `type` (`MeetingRoom` | `Car` | `Catering`; fixed after creation).
  * `capacity`: seats, or for a kitchen the portions per order.
  * `units`: orders per day for a kitchen; 1 for any other resource.
  * `last_day`: null until the resource is retired.
  * Rule columns, where null means the rule is off: `opens_at` and `closes_at` (local times), `min_duration`, `max_duration`, `min_notice`, `max_days_ahead`, `turnaround`, `max_future_per_user`.
* **bookings**:
  * `id`, `resource_id`, `user_id`.
  * `unit_no`: 0, except for catering orders.
  * `title`: optional, and shown only to its owner (see [Visibility](#visibility)).
  * `starts_at`, `ends_at`, `blocked_until`.
  * `attendees`: portions, for catering.
  * `delivery_at`, `delivery_room_id`: catering only.
  * `status` (`Confirmed` | `Cancelled`), `cancel_reason`, `proposal_id`.

  One raw SQL block in the first migration adds five database rules:
  * `CHECK (ends_at > starts_at)`
  * `CHECK (blocked_until >= ends_at)`
  * `CHECK (unit_no >= 0)`
  * `CHECK (status IN ('Confirmed', 'Cancelled'))`
  * `EXCLUDE USING gist (resource_id WITH =, unit_no WITH =, tstzrange(starts_at, blocked_until, '[)') WITH &&) WHERE (status = 'Confirmed')`

  The exclusion constraint has to be raw SQL, because EF Core has no API for it; the checks sit beside it so all the rules are in one place. `btree_gist` is enabled with `HasPostgresExtension`.
* **audit_events**: `id`, `at`, `actor_id` (null when seeding), `entity_type`, `entity_id`, `action` (`Added` | `Modified`), `changes` (jsonb, mapping each field to `[old, new]`).
* **notices**: `id`, `user_id`, `booking_id`, `message`, `created_at`, `read_at`.
* **assistant_steps**: `id`, `conversation_id`, `user_id`, `seq`, `kind` (`UserMessage` | `ModelCall` | `ToolCall` | `Reply`), `payload` (jsonb, new content only), `input_tokens`, `output_tokens`, `duration_ms`, `at`.
* **proposals**:
  * `id`, `conversation_id`, `user_id`, `turn`.
  * `kind` (`Create` | `Change` | `Cancel`); `booking_id`, for a change or cancel.
  * `payload` (a booking request, as jsonb).
  * `status` (`Open` | `Confirmed` | `Superseded`), `changed_before_confirm`, `created_at`.

  A proposal still `Open` after 24 hours is treated as expired. That is worked out when the proposal is read; it is not stored.
* **token_usage**: `day` (local date), `tokens`.
* **data_protection_keys**: managed by `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`.

### Time and wire format

* **Time zone:** comes from config (`Booking:TimeZone`, default `Europe/London`).
* **Storage:** times are stored as UTC `timestamptz`. Ranges are half-open, so 10:00 to 11:00 and 11:00 to 12:00 do not clash.
* **Clock:** every "now" comes from an injected `TimeProvider`.
* **On the wire:** the API sends and receives company-local date-times without an offset (`2026-11-26T14:00`) and local dates (`2026-11-26`). The server converts them using the configured zone, so a browser in another zone still books the intended time. A local time that does not exist or is ambiguous (a daylight-saving change) returns 400.

### Booking request

`BookingRequest` (Shared) has these fields:

* `resourceId`
* `start` and `end`
* `attendees`, 1 to 1000 (portions for a catering order)
* `title` (optional), at most 100 characters
* `deliveryAt` and `deliveryRoomId` (catering only)
* `proposalId` (optional)

A room or car booking needs `start` and `end`, with the end after the start; a catering order needs `deliveryAt` instead. A request that breaks any of these is a 400 listing every field it refuses, not only the first. For a catering order, the server takes the day from `deliveryAt` and ignores `start` and `end`. It sets the booking's range to that local day, midnight to midnight. A request whose shape doesn't match the resource returns 400: a delivery time for a room or car, or no delivery time for a kitchen. So does a time sent with an offset (it must be company-local). `deliveryRoomId` must be an active meeting room. Cancelling or retiring that room does not cancel orders delivered to it; this gap is accepted.

### Rules

`BookingRules.Check(candidate, resource, context, now, zone)` returns every violated rule as a `(code, message)` pair. `context` carries the user's count of future bookings and, for a kitchen, the free unit (or none). The candidate carries the start, end, attendees and delivery time already converted to UTC. The method does no I/O, so each rule is tested without a database. Messages name the limit. `RuleFields.For(type)` in Shared lists the rule fields each type uses, and the admin form shows only those.

| Code | Meeting room | Car | Kitchen |
| --- | --- | --- | --- |
| `END_BEFORE_START` | end not after start | end not after start | always passes (the server sets a whole day) |
| `IN_PAST`, `TOO_SOON` (`min_notice`), `TOO_FAR_AHEAD` (`max_days_ahead`) | start | pick-up (start) | `deliveryAt` |
| `OUTSIDE_OPENING_HOURS` | start and end inside hours on one local day | pick-up and return each inside hours; may span days | `deliveryAt` inside hours |
| `TOO_SHORT`, `TOO_LONG` | duration | duration | not used |
| `OVER_CAPACITY` | attendees ≤ seats | passengers ≤ seats | portions ≤ portions per order |
| `RESOURCE_RETIRED` | any part after `last_day` | any part after `last_day` | day after `last_day` |
| `USER_LIMIT` (`max_future_per_user`) | the user's confirmed bookings on it with `ends_at > now`, not counting the one being edited | same | same |
| `KITCHEN_FULL` | not used | not used | no free unit that day ("Main Kitchen takes 5 orders a day") |

* **Boundaries:** `closes_at` is inclusive for an end or a return; a start, pick-up or delivery must be before it. A one-sided window (only `opens_at` or only `closes_at`) applies to every type.
* **No opening hours:** with null hours, no hours check runs, but a room booking still stays within one local day.
* **Rule changes:** apply to new bookings and edits only.

### Booking, editing and cancelling

The endpoints validate the request's shape before `BookingService` runs. `BookingService` then works in one transaction:

1. **Lock the resource row** with `SELECT ... FOR UPDATE`. Retirement and changes to `units` take the same lock.
2. **Lock the caller's user row** with `FOR SHARE`. If the user is missing or inactive, return 401. Deactivation takes `FOR NO KEY UPDATE` on that row. That lock conflicts with `FOR SHARE`, so a booking cannot commit for a user who is being deactivated. It does not conflict with PostgreSQL's foreign-key checks, so retirement and deactivation cannot deadlock.
3. **For an edit or a cancel, lock the booking row** with `FOR UPDATE`:
   * not the caller's: 404;
   * not `Confirmed`: 409 `NOT_CONFIRMED`;
   * already started: 422 `ALREADY_STARTED`.

   An edit keeps its resource. Moving to another resource means cancel and rebook.
4. **Load the context:** the caller's future count, not counting this booking. For a kitchen, find the lowest `unit_no` with no confirmed order that local day, again not counting this booking. Every other resource uses `unit_no` 0. The database refuses a negative `unit_no`; the upper bound rests on this step.
5. **Run `BookingRules.Check`.** Any violation returns 422, listing every violation.
6. **Set the derived fields:** `blocked_until = ends_at + turnaround`, and for catering, `unit_no`.
7. **Insert or update, then save.** If the database refuses an overlap (SQLSTATE `23P01`), return 409 `OVERLAP`. The message is built from the locked resource row: "Thames is already booked then", or for a resource with a gap, "Pool Car 1 is booked then, or within its 60-minute gap between bookings".
8. **Link a proposal, if one came with the request.** The booking is linked and the proposal marked `Confirmed` only if all four hold:
   * it is the caller's;
   * it is `Open`;
   * it is under 24 hours old;
   * its kind matches the endpoint (`Create` to `POST`; `Change` or `Cancel` to the same booking).

   The proposal row is read `FOR UPDATE SKIP LOCKED`, so a second request carrying the same proposal books unlinked rather than waiting (waiting could deadlock with the foreign-key lock a catering order takes on its delivery room). `changed_before_confirm` is set when the submitted values differ from the proposal's payload. If any check fails, the booking goes ahead unlinked. A proposal never makes a booking fail.

**Cancel** runs steps 1 to 3, then sets `status = Cancelled` and fills `cancel_reason`. Rows are never deleted, except by reseed.

**Lock order.** Every write takes row locks in one order: resources, then users, then bookings. A catering order locks its kitchen, then its delivery room (FOR KEY SHARE, re-reading the room's last day under it), then the caller; otherwise the insert's foreign key would take the room's lock last and could deadlock with an edit and a deactivation. Users and bookings are each locked in id order. A cascade's notices take key locks on their owners after the bookings; a key lock conflicts only with FOR UPDATE, which no user lock takes. `formal/AdminLocks.tla` model-checks this order with TLC.

**Cascades.** `BookingService.CancelFutureBookings(filter, reason)` locks the matching bookings in id order, re-checks that each is still confirmed, and changes them one at a time, because bulk updates bypass the audit interceptor. It writes a notice to each owner who is active and is not the person making the change.

* **Retirement:** locks the resource. A `lastDay` before today returns 422 `LAST_DAY_PAST`. A confirmed booking running across `lastDay` returns 422 `IN_USE`. Otherwise it sets `last_day` and cancels the bookings after it.
* **Deactivation:** locks the target and every active admin row with `FOR NO KEY UPDATE`. Removing the last active admin returns 422 `LAST_ADMIN`; demotion through `PUT` applies the same check. Deactivation then clears `is_active` and cancels the user's future bookings.
* **Units:** lowering a kitchen's `units` below a unit that holds a future order returns 422 `UNITS_IN_USE`.

### Audit

`AuditInterceptor`, an EF Core `SaveChangesInterceptor`, adds an `audit_events` row for every added or modified `User`, `Resource` and `Booking`. It does this inside the same `SaveChanges` call, so the event commits in the same transaction as the change. The actor is the signed-in user.

`HistoryLabels` turns field changes into words: `Status` changing to `Cancelled` reads "cancelled", `LastDay` being set reads "retired", and `IsActive` becoming false reads "deactivated". Bulk updates and raw SQL writes on these three tables are allowed only in migrations and in reseed. Each write path has a test that checks its audit row.

### Utilisation

`GET /api/reports/utilisation?from&to` covers the local dates from `from` to `to`, both included.

* **Open window:** for each resource and calendar day, the window runs from `opens_at` to `closes_at`, or 24 hours when those are null. Only days up to the resource's `last_day` count.
* **Booked time:** each confirmed booking's `[starts_at, ends_at)`, clipped to each day's open window, so the ratio never exceeds 100%. The turnaround gap does not count.
* **Kitchens:** unit-days used, over `units` times the number of days.

### API

Errors are Problem Details. Every 409, 422 and 429 has a `code` and a `message`; rule refusals list `violations: [{code, message}]`.

* **400:** malformed input, or an invalid local time.
* **401:** not signed in, or session rejected.
* **403:** not an admin.
* **404:** not found, or not yours.
* **409:** `OVERLAP`, `NOT_CONFIRMED`, `DUPLICATE` (an email or resource name already in use; emails compare in lower case).
* **422:** the rule codes above, plus `ALREADY_STARTED`, `IN_USE`, `LAST_DAY_PAST`, `LAST_ADMIN`, `UNITS_IN_USE`, `TURN_LIMIT`.
* **429:** `RATE_LIMITED`, `BUDGET_EXHAUSTED`.

| Method and path | Who | Result |
| --- | --- | --- |
| `GET /api/auth/sign-in-options`, `POST /api/auth/sign-in`, `POST /api/auth/sign-out`, `GET /api/auth/me` | anyone | Mock SSO: pick an active user and get a cookie. |
| `GET /api/availability?date&type` | signed in | Free/busy per resource for one local day, following [Visibility](#visibility). Kitchens report free orders out of units. |
| `GET /api/availability/month?year&month&type` | signed in | Used, available and share per calendar day for one type, totals only ([month view](month-view.md)). 400 for a bad month, year or type. |
| `GET /api/resources/free?type&start&end&attendees` | signed in | Resources free for a window, tested against `blocked_until`. |
| `GET /api/resources`, `GET /api/resources/{id}` | signed in | Active resources (admins also see retired ones). |
| `POST /api/resources`, `PUT /api/resources/{id}` | admin | 201 or 200. `PUT` cannot change `type` or `last_day`. |
| `POST /api/resources/{id}/retirement` `{lastDay}` | admin | 200 with the number of bookings cancelled. |
| `GET /api/bookings/mine?from&to`, `GET /api/bookings/{id}` | owner | The caller's bookings. |
| `POST /api/bookings` (`proposalId` optional) | signed in | 201 with `Location`. |
| `PUT /api/bookings/{id}` (`proposalId` optional) | owner | 200. |
| `DELETE /api/bookings/{id}?proposalId=` | owner | 204. Sets `Cancelled` and keeps the row. |
| `GET /api/users`, `GET /api/users/{id}`, `POST /api/users`, `PUT /api/users/{id}` | admin | `PUT` cannot change `is_active`; a `PUT` without `role` keeps the user's role. |
| `POST /api/users/{id}/deactivation` | admin | 200 with the number of bookings cancelled. |
| `GET /api/reports/utilisation?from&to` | admin | Ratio per resource. |
| `GET /api/history?entityType&entityId&from&to` | admin | `{items, more}`: up to 500 audit events, newest first, described in words; `more` is true when older matching events were left out. |
| `GET /api/notices`, `POST /api/notices/{id}/read` | signed in | The caller's notices. |
| `POST /api/assistant/messages` `{conversationId?, text}` | signed in | Reply text and any open proposals. |
| `GET /api/assistant/traces?from&to`, `GET /api/assistant/traces/{conversationId}` | admin | Traces and their steps. |
| `POST /api/admin/reseed` | admin | 204. Resets the demo data. 429 `RATE_LIMITED` with `Retry-After` when called again within the window (`Demo:ReseedWindowSeconds`, default 30, once across the app; 0 turns it off for end-to-end runs). |

### Visibility

What a caller sees of other people's bookings, amended by the [month view design](month-view.md):

| Where | Owner | Admin, someone else's booking | Employee, someone else's booking |
| --- | --- | --- | --- |
| Book grid (`GET /api/availability`) | title | name, no title | neither |
| Month view | totals only | totals only | totals only |
| Assistant tool results | own bookings | nothing of others' | nothing of others' |
| History and Traces | not shown | titles, as audit records | not shown |

### Access

* **Authentication:** cookies, with claims for the user id and role. An `Admin` policy guards the admin endpoints.
* **Per-request check:** `CookieAuthenticationEvents.OnValidatePrincipal` re-reads the user. It calls `RejectPrincipal()` if the user is missing or inactive, or if their role has changed. A deactivated or demoted session therefore fails on its next request.
* **No redirects:** `/api/*` returns 401 or 403 instead of redirecting to a sign-in page.
* **Cookie keys:** Data Protection keys are stored in PostgreSQL, so cookies survive a redeploy.

### Assistant

`AssistantService` builds its client in three layers. The innermost `IChatClient` comes from DI, so tests can register a scripted stub there. Around it sit `TracingChatClient`, then `UseFunctionInvocation`, with at most 5 iterations.

**Conversations.**

* A `conversationId` that belongs to someone else returns 404.
* History is rebuilt from the conversation's `assistant_steps`. A conversation holds at most 20 turns; the 21st message returns 422 `TURN_LIMIT`.
* At the start of each message, the server marks open proposals from earlier turns as `Superseded`. Proposals made in the same turn stay open together.
* The system prompt carries today's date in the company zone and the user's name.

**Trace steps.** Each kind of step has one writer:

* `AssistantService` writes `UserMessage` and `Reply`.
* `TracingChatClient` writes `ModelCall`: the new messages, the tool calls requested, tokens and duration.
* A wrapper around each `AIFunction` writes `ToolCall`: arguments, result, duration and any error.

**Tools.** Tools are created with `AIFunctionFactory` and always run as the signed-in user, never as a user named in a model argument:

* `find_free_resources`
* `get_my_bookings`
* `propose_booking`
* `propose_change`
* `propose_cancellation`

A propose tool runs the same checks as booking (`BookingService.PreviewAsync`: the request's validation, `BookingRules.Check` and the overlap test) without writing. If any fails, it tells the model why and stores nothing. Otherwise it stores an `Open` proposal for the current turn.

**Limits and failures.**

* **Rate limit:** 10 messages a minute per user (`Assistant:MessagesPerMinute`, 0 turns it off), with the ASP.NET Core rate limiter. Over it returns 429 `RATE_LIMITED`.
* **Token budget:** one global daily budget from config (`Assistant:DailyTokenBudget`), counted in `token_usage` and checked before each model call. Each call first reserves an upper bound (its input's UTF-8 bytes plus `Assistant:MaxOutputTokens`, default 800, which is also sent as the call's output cap) and settles to the reported usage afterwards; a call that fails or is abandoned keeps its reservation. Over the budget returns 429 `BUDGET_EXHAUSTED`.
* **Model timeout or error:** after 30 seconds (`Assistant:TimeoutSeconds`), or on any error, the panel shows a short reply that says so. The forms are unaffected.

**Key.** `OpenAI:ApiKey` and `OpenAI:Model` come from config and never reach the client.

**Evals.** `evals/cases.json` holds requests, a fixed clock and the expected proposal fields. `EvalTests`, marked `Category=Eval`, runs them against the real model on demand.

### Page design

Simple layouts built from stock Fluent UI components. All times are shown in the company zone, labelled "London time".

* **Shell.**
  * A top bar holds the app name, navigation by role, a notices badge and a user menu with Sign out.
  * Employees see Book, Month and My bookings. Admins also see Users, Resources, Utilisation, History and Traces.
  * Everyone lands on Book.
* **Sign-in.** A card lists the seeded users, grouped by role, and clicking one signs in. The card is labelled "Demo sign-in, standing in for company SSO".
* **Book (overview).** A date picker with previous-day and next-day buttons, and tabs for Rooms, Cars and Catering.
  * **Rooms and Cars:** a grid with one row per resource (name and seats) and one column per half-hour.
    * Closed hours are greyed out.
    * Other people's bookings show as "Booked"; admins see "Booked: {name}", never the title (see [Visibility](#visibility)).
    * The user's own bookings show in the accent colour, with their titles.
    * Clicking a free cell opens the booking form, pre-filled for one hour.
    * A multi-day car booking shows as busy across each day it covers.
  * **Catering:** one card per kitchen showing "{free} of {units} orders left on this day" and "Takes {units} orders a day, up to {portions} portions each", with an Order button.
  * **Assistant:** an "Ask assistant" button opens it as a side panel on the right, closed by default. Proposals appear in the panel as cards, and each card's Review button opens the same booking form. The grid refreshes after each confirmation.
* **Booking form (dialog).**
  * **Fields:** resource, date, start, end, attendees and an optional title. For catering: kitchen, delivery time, portions, and a delivery-room dropdown of active rooms.
  * **Errors:**
    * Field errors appear inline.
    * A 409 or 422 shows its message in a bar at the top of the form.
    * A 409 also offers "Find alternatives": resources of the same type free for that window, one click to switch.
  * **After booking:** a success toast. After a room booking, the toast offers "Add catering", pre-filled with the room and start time.
* **My bookings.** Upcoming and Past tabs, each a table. Edit opens the booking form; Cancel asks for confirmation first. The notices badge opens a drawer listing notices, where each can be marked read.
* **Admin pages.** Each page is a data grid with an Add button that opens a form dialog.
  * **Resources:** the form shows only the rule fields for the chosen type (`RuleFields.For`).
  * **Retire and Deactivate:** a confirmation dialog explains that future bookings will be cancelled and their owners notified, and the result reports how many were cancelled.
  * **Utilisation:** a date range and a table with a percentage and a CSS bar per resource; no chart library.
  * **History:** a table filtered by entity type and date range.
  * **Traces:** a list of conversations; opening one shows a timeline of its steps.
* **Narrow screens and accessibility.**
  * Desktop first. Under 768 px wide, the grid scrolls sideways with the resource column fixed, and the assistant panel opens full screen.
  * Every grid cell is a button with a label, for example "Thames, 14:00, free".
  * The grid works from the keyboard.
  * "Booked" is shown as text as well as colour.

### Deployment

* **Image:** one Dockerfile. `mcr.microsoft.com/dotnet/sdk:10.0` builds the app and `aspnet:10.0` (Debian, with time-zone data) runs it.
* **Start-up:** the app applies migrations, then seeds the database if it is empty.
* **Reseed:** the only bulk delete. In one transaction it empties every app table except `token_usage` and the Data Protection keys, then re-runs the seed with fixed ids. Seeded users therefore keep their sessions, and a session whose user no longer exists is rejected. Interrupting another visitor is an accepted cost.
* **Config:** comes from environment variables: `ConnectionStrings__Db`, `OpenAI__ApiKey`, `OpenAI__Model`, `Booking__TimeZone`, and the `Assistant__*` limits (`DailyTokenBudget`, `MessagesPerMinute`, `TimeoutSeconds`, `MaxTurns`, `MaxIterations`, `MaxOutputTokens`). Without `OpenAI__ApiKey` the assistant answers that it isn't set up, and the forms work as usual.
* **Railway:** runs one app service, pinned to one replica, and one PostgreSQL service. It deploys `main` after CI passes.

## Alternatives Considered

The options rejected during ideation are in [concepts_brief.md](concepts_brief.md), with their reasons: chat-only booking, a model that commits bookings, prompt-text skills, free-text types, hard deletion and a custom agent loop.

* **Kitchen units as separate resource rows** (the brainstorm's first version). Rejected by author: rules are copied onto N rows, retirement and the per-user cap break across rows, and it needs its own lock and unit picker.
* **A kitchen with an order count checked in code, or a kitchen-day portion counter.** Rejected by author: units reuse the one no-overlap constraint, which the database enforces per unit; the server picking the unit (step 4 above) is the only part left to code. The portion counter remains the upgrade path.
* **Rule rows with parameters instead of typed columns.** Rejected by author: parameters become untyped JSON with a parser per code, and the compiler no longer checks a rule from the database to the form.
* **Notices as a query over cancellations.** Rejected by author: it needs actor and read columns on bookings; a `notices` table is simpler.
* **A clash query in code with a `TURNAROUND` code.** Rejected by author: it is a second overlap check beside the constraint; the 409 is worded from the locked row instead.
* **Admins editing or cancelling single bookings.** Rejected by author: no screen needs it, and retirement and deactivation cover admin cancellations.
* **Booking edits by cancel and rebook, or by replacing the row.** Rejected by author: cancel and rebook can lose the slot in between; replacing needs a third status and a link column.
* **History from explicit audit calls, or from three sources** (booking rows, a booking changes table, and an audit table for users and resources). Rejected by author: explicit calls leave gaps when a call is forgotten; three sources make History a join.
* **A proposal outcome reported by a separate client call, or inferred later.** Rejected by author: the call is lost if the tab closes, and inference is a heuristic.
* **Serializable transactions, or accepting the retirement race.** Rejected by author: serializable needs 40001 handling on every path; accepting the race leaves bookings after a last day.
* **React and TypeScript for the client.** Rejected by author: with one language and shared DTOs, the compiler checks a new field from the database to the form.

---

## Non-Functional Requirements, Risks & Mitigations

* **Schedule (a short delivery window).** Mitigation: the build order, with its gate at step 9, and the cut list. Assistant work starts only after the gate.
* **Unverified platform assumptions.** Each is checked at the step where it first becomes possible, and each has a fallback:
  * **Railway connection string and port (step 1).** Build `ConnectionStrings__Db` in key=value form from the `PGHOST`, `PGPORT`, `PGUSER`, `PGPASSWORD` and `PGDATABASE` reference variables. Target port 8080, and enable wait-for-CI. Fallback: set the values manually.
  * **Time-zone data in the image (step 1).** Fallback: install `tzdata`.
  * **`btree_gist` on Railway (step 2).** Fallback: drop the constraint and run the overlap check as a query under the existing row lock.
  * **Request validation (step 6).** Settled: endpoints call `Validator` directly rather than relying on `AddValidation()` to discover DTOs in the Shared project.
  * **`WebApplicationFactory` on Kestrel serves the WebAssembly client, and Playwright can type into Fluent inputs (step 7).** Fallback: start the app with `dotnet run`, and target the inner `input`.
  * **Package majors** (Fluent UI, Microsoft.Extensions.AI.OpenAI, EFCore.NamingConventions for EF Core 10). Settled with `dotnet package search` in step 1.
* **Overlapping deploys.** Migrations on start are safe with one replica. They also stay backward-compatible, in case Railway briefly runs the old and new containers together.
* **Model failure.** Covered under Assistant: the panel replies with an error, and the forms keep working.
* **Public demo.** Anyone with the link can sign in as any seeded user, including an admin. The global token budget caps model spend, and reseed restores the data.
* **Secrets.** The OpenAI key lives in a Railway variable and in local user-secrets. It is never committed, logged or sent to the browser. The Data Protection keys that sign session cookies are stored unencrypted in the app's database, so anyone who can read the database can forge a session; production would protect them with a key vault or certificate.
* **Scale.** One office, tens of resources, hundreds of users. The row lock serialises bookings per resource only.

---

## Detailed Implementation Plan

**Paths.** Every path below is created unless marked (M).

* `Server/` means `src/ResourceBooking/`.
* `Client/` means `src/ResourceBooking.Client/`.
* `Shared/` means `src/ResourceBooking.Shared/`.
* `Tests/` means `tests/ResourceBooking.Tests/`.
* `E2E/` means `tests/ResourceBooking.E2E/`.

A bare file name sits in the same folder as the path before it.

Any step may also modify these files to register what it adds: `Server/Program.cs`, `Client/Program.cs`, `Server/Components/App.razor`, `Client/Layout/*.razor`, `Client/_Imports.razor` and `Server/appsettings.json`.

Steps 2 to 10 write their tests first and run them failing.

1. **Skeleton, deployed.**
   * **Generate:** `dotnet new blazor -n ResourceBooking --interactivity WebAssembly --all-interactive --empty -o src` creates `Server/` and `Client/`; check that the layout matches the legend above.
   * **Root files:** `ResourceBooking.slnx`; `global.json` (SDK 10.0.100, `rollForward: latestFeature`, `test.runner: Microsoft.Testing.Platform`); `Directory.Build.props` (nullable, `TreatWarningsAsErrors`); `Directory.Packages.props`; `dotnet-tools.json` (`dotnet-ef`); `.editorconfig` (`**/Migrations/*.cs` as generated code).
   * **Projects:** `Shared/ResourceBooking.Shared.csproj`, `Tests/ResourceBooking.Tests.csproj` and `E2E/ResourceBooking.E2E.csproj` (Microsoft.Testing.Platform: the .NET 10 SDK refuses xunit v3 in VSTest mode; the E2E project ignores the zero-tests exit code 8 until step 7).
   * **Build and deploy:** `Dockerfile`, `.github/workflows/ci.yml` and `Server/Endpoints/HealthEndpoints.cs` (`GET /healthz`). Turn prerendering off in `Server/Components/App.razor` (M).
   * **Packages:** `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`, `Microsoft.FluentUI.AspNetCore.Components`, `Microsoft.AspNetCore.Components.Authorization`, `Microsoft.Extensions.AI.OpenAI`. Test packages: `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `Microsoft.Extensions.TimeProvider.Testing`, `CsCheck`, `Microsoft.Playwright`.
   * **Then:** deploy to Railway and check the connection string, the port and time-zone data.
2. **Schema and seed.**
   * **Enums:** `Shared/Enums.cs`.
   * **Data:** `Server/Data/AppDb.cs`; entities `Server/Data/Entities/User.cs`, `Resource.cs`, `Booking.cs`, `AuditEvent.cs`, `Notice.cs`, `AssistantStep.cs`, `Proposal.cs`, `TokenUsage.cs`; `Server/Data/Migrations/`, created with `dotnet ef migrations add Init --output-dir Data/Migrations` and holding the raw SQL constraints and every table; `Server/Data/Seed.cs`, with fixed ids and the CUJ seed.
   * **Fixture:** `Tests/Infrastructure/DbFixture.cs`. One PostgreSQL container per test assembly (an xUnit v3 assembly fixture) is migrated once. Each test gets its own database, cloned from the migrated template.
   * **Tests:** `Tests/Database/ConstraintTests.cs`. An overlap is refused (`23P01`). A booking inside the turnaround gap is refused. The same kitchen unit on the same day is refused, and different units are allowed. 20 concurrent inserts give one saved row and nineteen `23P01` errors. The four CHECK constraints hold.
   * **Then:** check `btree_gist` on Railway.
3. **Rules.**
   * **Code:** `Shared/RuleCodes.cs`, `RuleFields.cs`, `Shared/Dtos/BookingRequest.cs` and `Server/Bookings/BookingRules.cs`.
   * **Example tests:** `Tests/Rules/BookingRulesTests.cs`. One test per code checks the code and the message. Boundary tests cover `closes_at` as inclusive, same-day room bookings, and catering times checked on `deliveryAt`.
   * **Property tests:** `BookingRulesProperties.cs`, with CsCheck. Any room request that `Check` accepts lies inside opening hours on one local day. Any accepted request ends after it starts and stays within `max_duration`. `Check` never throws.
4. **Sign-in, access and reseed.**
   * **Server:** `Server/Endpoints/AuthEndpoints.cs`, `AdminEndpoints.cs` (reseed) and `Server/Auth/CookieSetup.cs`. `CookieSetup.cs` covers `OnValidatePrincipal`, 401 and 403 on `/api/*`, the `Admin` policy, and Data Protection keys.
   * **Client:** `Client/Auth/MeAuthenticationStateProvider.cs`, which calls `/api/auth/me`, and `Client/Pages/SignIn.razor`.
   * **Fixture:** `Tests/Infrastructure/ApiFactory.cs`, a `WebApplicationFactory` with a `FakeTimeProvider` and a database per class.
   * **Tests:** `Tests/Api/AuthTests.cs`. 401 when signed out. 403 when an employee calls reseed. Deactivated, demoted and deleted-user sessions are rejected. Reseed restores the seed.
5. **Audit.**
   * **Code:** `Server/Data/AuditInterceptor.cs` and `Server/Reports/HistoryLabels.cs`.
   * **Tests:** `Tests/Database/AuditTests.cs`. Working through the DbContext, adding or modifying a `User`, `Resource` or `Booking` leaves one event with its actor and changes.
6. **Booking API.**
   * **Code:** `Shared/Dtos/BookingDtos.cs`, `AvailabilityDtos.cs`; `Server/Bookings/BookingService.cs`, `AvailabilityService.cs`.
   * **Endpoints:** `Server/Endpoints/BookingEndpoints.cs` (mine, by id, `POST`, `PUT`, `DELETE`); `AvailabilityEndpoints.cs` (availability, `resources/free`, `GET /api/resources` and `GET /api/resources/{id}`); `NoticeEndpoints.cs`.
   * **Tests:** `Tests/Api/BookingApiTests.cs` covers every booking status and code under API, plus:
     * Goal 1: 20 concurrent `POST`s give one 201 and nineteen 409s.
     * Six orders for a 5-unit kitchen give five 201s and one 422 `KITCHEN_FULL`.
     * A request carrying a `unitNo` for a room is booked on unit 0, so it still clashes with an overlapping booking; a sixth kitchen order carrying `unitNo: 7` still gets 422 `KITCHEN_FULL`.
     * The gap message.
     * An edit at the user's cap is allowed.
     * Owner-only access returns 404 to anyone else.
     * Proposal links: wrong owner, not `Open`, expired and wrong kind each book unlinked; `changed_before_confirm` is set.
     * Availability follows [Visibility](#visibility): an employee sees no other owner or title, and an admin sees names but no titles.
     * Every write path leaves its audit row.
7. **Employee screens and journeys.** Test tier `basic`: UI and wiring.
   * **Client:** `Client/Services/ApiClient.cs`, which maps Problem Details to messages; `Client/Pages/Overview.razor` and `Overview.razor.css`; `MyBookings.razor`; `Client/Components/BookingForm.razor` and `NoticesBadge.razor`.
   * **Fixture:** `E2E/AppFixture.cs`. The app runs on Kestrel with a Testcontainers database and the real clock. All end-to-end classes share one collection that does not run in parallel, and each class reseeds first.
   * **Tests:** `E2E/Cuj1BookFromOverviewTests.cs` and `Cuj2RuleRefusalTests.cs`.
   * **Then:** check that the Kestrel fixture serves the client and that Playwright can type into Fluent inputs.
8. **Admin API.** Test tier `high`: permissions, audit and data changes; its locks (LAST_ADMIN, cascade row locks, retirement) `critical`: concurrency.
   * **Endpoints:** `Server/Endpoints/UserEndpoints.cs` (list, by id, `POST`, `PUT`, deactivation); `ResourceEndpoints.cs` (`POST`, `PUT`, retirement); `ReportEndpoints.cs` (utilisation, history).
   * **Admin tests:** `Tests/Api/AdminApiTests.cs`:
     * Retirement: `IN_USE`, `LAST_DAY_PAST`, the cascade, notices, and the race with a concurrent booking.
     * Deactivation: the cascade with no notice to the inactive owner; `LAST_ADMIN`, including demotion and two admins deactivating each other.
     * `UNITS_IN_USE`.
     * `PUT` cannot change `type`, `last_day` or `is_active`.
   * **Utilisation tests:** `UtilisationTests.cs`. Ratios for a multi-day car, null opening hours, a retired resource, and kitchen unit-days.
9. **Admin screens and journeys, then the gate.** Test tier `basic`: UI and wiring.
   * **Client:** `Client/Pages/Admin/Users.razor`, `Resources.razor`, `Utilisation.razor` and `History.razor`.
   * **Tests:** `E2E/Cuj3AdminSetupTests.cs`, `Cuj4RetireAndDeactivateTests.cs` and `Cuj5UtilisationAndHistoryTests.cs`.
   * **Gate:** step 10 starts only when CI is green and CUJ 1 passes against the deployed URL.
10. **Assistant.** Test tier `standard`: tool calls go through validated rules; the global token budget cap `high`: a safety limit on spend.
    * **Server:** `Server/Assistant/AssistantService.cs`, `TracingChatClient.cs` (with `TracedFunction`), `AssistantTools.cs` and `TokenBudget.cs`; `Server/Endpoints/AssistantEndpoints.cs`.
    * **Client:** `Client/Components/AssistantPanel.razor` and `Client/Pages/Admin/Traces.razor`.
    * **Evals:** `evals/cases.json`, copied to the test output by `Tests/ResourceBooking.Tests.csproj` (M).
    * **Stub:** `Tests/Infrastructure/StubChatClient.cs`, which replays scripted tool calls.
    * **Assistant tests:** `Tests/Assistant/AssistantTests.cs`:
      * Tools run as the caller.
      * A busy slot is not proposed.
      * Proposals are superseded per turn.
      * Another user's conversation returns 404.
      * The turn cap holds.
      * 429 `BUDGET_EXHAUSTED` and 429 `RATE_LIMITED`.
      * Every trace step kind is written.
    * **Eval tests:** `EvalTests.cs`, with `[Trait("Category","Eval")]`; `EvalRunnerTests.cs` checks the runner on the stub, in CI.
    * **Budget (tier high):** `TokenBudgetModelTests.cs`, a stateful model of tokens per London day across midnight and both clock changes, plus concurrent charges and the endpoint's stop point.
    * **End to end:** `E2E/Cuj6AssistantTests.cs`, on the stub.
11. **Docs.** No tests. `README.md`: how to run it locally, what is mocked, known limits, and how AI tools were used. It links to this design.

**If time runs short,** cut in this order. Items 5 to 7 are the ones that can be cut before the gate.

1. The evals (`evals/cases.json` and `EvalTests`).
2. The trace page, reduced to a raw list of steps.
3. Assistant change and cancel proposals.
4. `changed_before_confirm`.
5. History in words (`HistoryLabels`), reduced to the raw audit list.
6. Booking edits (`PUT`), replaced by cancel and rebook.
7. The per-user cap (`USER_LIMIT`).

### Verification Plan

* **CI** (`ubuntu-latest`, which has Docker), in this order:
  1. `dotnet build -c Release`
  2. `dotnet format --verify-no-changes`
  3. `pwsh tests/ResourceBooking.E2E/bin/Release/net10.0/playwright.ps1 install --with-deps chromium`
  4. `dotnet test -c Release --no-build --filter-not-trait "Category=Eval"`
* **End-to-end user journeys:** Playwright runs one test class per CUJ (`E2E/Cuj1` to `Cuj6`) against the real app and database, in a real browser. They run in CI step 4 alongside the other tests. CUJs 1 to 5 must pass before assistant work starts, and CUJ 6 runs against the scripted model stub.
* **Locally:** the same commands. The tests need Docker.
* **Evals, on demand:** run `dotnet test --project tests/ResourceBooking.Tests --filter-trait "Category=Eval"` with `OpenAI__ApiKey` set.
* **Deployed check (manual, after Railway deploys):** set `E2E_BASE_URL` to the app's URL, then run `dotnet test --project tests/ResourceBooking.E2E --filter-class "*Cuj1*"`. The fixture reseeds first, signed in as Sam.
* **Coverage:** no percentage target. Every refusal code (checking code and message), every status and code under API, and every CUJ has a test.

---

*Drafted with the Elephant-Goldfish Model (EGM) design workflow: brainstorm, draft, then a five-stage review.*
