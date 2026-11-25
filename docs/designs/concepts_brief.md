# Concepts Brief: Resource Booking Portal

* **Author:** Joe Luo
* **Date:** 2026-11-25

## Overview

An internal portal where employees book meeting rooms, cars and catering, and admins manage what can be booked. Sign-in is a mocked company SSO with two roles, Employee and Admin. Employees book from a free/busy overview through one booking form. Admins use forms to manage users, resources and booking rules, see utilisation and history, retire resources and deactivate leavers; affected bookings are cancelled with a notice. The database refuses overlapping bookings for every resource; code checks every other rule. Second priority: an assistant (OpenAI through Microsoft.Extensions.AI) that reads data and proposes bookings, never commits them, and stores a trace of every conversation for evals.

Two constraints shape every decision: a short delivery window, and a codebase that stays small and easy to change.

Stack: .NET 10, ASP.NET Core minimal APIs, Blazor WebAssembly with Fluent UI Blazor, EF Core on PostgreSQL, hosted on Railway.

## Analysis

Seven review lenses agreed on these points, most serious first.

1. **Scope.** The forms journeys alone are about nine screens, seven rules and two cancellation cascades, and the free/busy grid is hand-built (Fluent UI Blazor has no scheduler). Resolved by the build gate and cut order (decision 10).
2. **Catering.** Units count orders, not portions. Accepted as a known limit (decision 6).
3. **Turnaround gap.** Checked in code, it lets two concurrent car bookings both pass. Resolved in the constraint (decision 5).
4. **Time.** Every day-based rule needs a day boundary, and the container runs in UTC. Resolved by one company time zone (decision 7).
5. **Notices and history.** One cancellation was stored three times (status, history event, notice); a deactivated leaver cannot read a notice; rule changes had no history.
6. **Access.** A deactivated user's cookie stays valid. Assistant tools that do not run as the caller can reveal who booked what. Anyone who opens the public demo can sign in as an admin and spend the model key.
7. **Stack facts.**
   - EF Core has no API for exclusion constraints, so the constraint is raw SQL in a migration, and tests must run migrations (`EnsureCreated` skips it).
   - Add `CHECK (ends_at > starts_at)`: an empty range overlaps nothing.
   - The hosted Blazor WebAssembly template was removed in .NET 8: use the Blazor Web App template with WebAssembly interactivity and prerendering off.
   - The function-invocation client already returns tool calls, results and token usage, which is most of a trace.
   - Check on day one: `btree_gist` on Railway's PostgreSQL, `WebApplicationFactory.UseKestrel()` for Playwright, Playwright typing into Fluent UI inputs, time-zone data in the container image.

## Goals vs. Non-Goals

**Goals**

- Both journeys work end to end through forms on the deployed app, covered by end-to-end tests in CI.
- The database refuses every double booking, including the car turnaround gap.
- Every other rule lives in one place in code. Adding a rule or a field is a small, local change.
- The assistant only proposes. The user confirms through the same form and endpoint.

**Non-Goals**

- Real SSO and real email.
- More than one time zone or site.
- Assistant write tools, a custom agent loop, evals in CI.
- Hard deletion of resources or users.
- Catering capacity counted in portions per day (upgrade path below).
- Future work: a dedicated agent-trace tool (agent-flight-recorder), and a self-improving loop where the evals and a coding agent propose prompt and tool fixes.

## Alternatives Weighed

- **Chat as the only way to book.** Rejected by author: slower than two clicks for a request the user can already state; models make errors on dates and rooms; every small change touches prompts and tools.
- **A model tool that commits or approves a booking.** Rejected by author: the user must confirm with a button.
- **Prompt-text "skills" to enforce booking flows.** Rejected by author: prompt text cannot enforce anything, so mandatory flows live in code.
- **Catering as a summed quantity cap guarded by a row lock.** Rejected by author: units let one database-enforced no-overlap rule cover every resource type.
- **A kitchen-day portion counter** (`CHECK (portions_used <= portions_cap)`). Rejected by author for now: the database would enforce the real portion limit, but every cancellation path must give portions back, and it is a second mechanism beside the no-overlap rule. Kept as the upgrade path.
- **Hard-deleting resources.** Rejected by author: it loses history and utilisation.
- **A custom agent loop or a larger agent framework.** Rejected by author: one assistant with a handful of tools.
- **Free-text resource types.** Rejected by author: they break reporting and typed rules.

## Key Decisions & Staging for Next Steps

### Decided

1. **Access.** Mocked SSO; only users an admin has added can sign in; roles Employee and Admin. The active flag is checked on every request, and the last admin cannot be deactivated.
2. **Visibility.** Employees see free/busy and their own bookings. Admins see who booked what, and the history.
3. **Rules.** Resource types are a fixed list. Rules are typed columns on each resource; which ones apply depends on the type. One pure `BookingRules.Check` returns rule codes. Rule changes apply to new bookings only.
4. **One booking form** for both paths. Confirming is always a user click that calls `POST /api/bookings`.
5. **No overlaps.** An exclusion constraint over `resource_id` and `tstzrange(starts_at, blocked_until, '[)')` for bookings that are not cancelled, where `blocked_until = ends_at + turnaround gap` (zero except for cars). Utilisation counts up to `ends_at`.
6. **Catering.** Each kitchen unit is an ordinary Catering resource whose capacity is its portion cap, booked for a whole local day. The server tries free units in order and moves to the next on a constraint violation. The overview shows "3 of 5 free". Each order has a delivery time and a room.
7. **Time.** One company IANA time zone in config (Europe/London); UTC `timestamptz` with half-open ranges; opening hours as local times; an injected `TimeProvider`. Room bookings stay within opening hours on one day. Car pick-up and return fall within opening hours, and a car booking may span days.
8. **Retiring and leavers.** A resource gets a last day and is then archived. Deactivating a leaver blocks sign-in. Both cancel the affected future bookings, with notices in an in-app outbox.
9. **Assistant.** Built-in function-invocation loop; read and propose tools only, running as the caller; a spend limit, a rate limit and a low iteration cap. Every conversation is traced; the eval set runs on demand.
10. **Build order with a gate.**
    1. Skeleton deployed: host, CI, migrations, sign-in.
    2. Schema and seed data.
    3. Booking service, with Testcontainers tests.
    4. Employee screens.
    5. Admin screens.
    6. Gate: both journeys pass end to end on the deployed app.
    7. Assistant.

    If time runs short, cut in this order: the eval re-run set; the trace page (down to a raw list); assistant change and cancel proposals; the "changed" outcome; booking edits (cancel and rebook instead); the per-user cap; property tests beyond the rule checker; charts.

### Open for the design draft (raised by review, not yet decided)

- **History and notices.** Booking rows as their own history (cancelled at, by whom, why); an audit table written explicitly for users, resources and rules; notices as a query over cancellations; a summary to the admin after a deactivation.
- **Retirement details.** The last day must be today or later; refuse only if a running booking crosses it; one `CancelFutureBookings(filter, reason)` for retirement and deactivation; lock the resource row against a concurrent new booking, or accept the race.
- **API errors.** Problem Details: 400 for malformed input, 422 for rule violations with codes, 409 for an overlap (SQLSTATE `23P01`).
- **Proposal outcome.** How a confirmed booking records the proposal it came from.
- **Booking edits.** `PUT`, so a change cannot lose its slot, or cancel and rebook.
- **Smaller points.** A generated user id with the email kept unique; "cancelled" in place of "released"; utilisation as booked hours over open hours, and unit-days for catering.

## Reviewer Status

| Lens | Status |
| --- | --- |
| egm-design | completed |
| egm-codebase-details | completed |
| egm-zoom-out | completed |
| egm-comprehension | completed |
| egm-implementation-readiness | completed |
| egm-structural | completed |
| egm-inventive-catalyst | completed |
