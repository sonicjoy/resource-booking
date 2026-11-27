# Month view and booking visibility

*   **Author:** Joe Luo
*   **Status:** Approved
*   **Date:** 2026-11-26

--------------------------------------------------------------------------------

## Overview

This design changes an approved behaviour: admins stop receiving the titles of other people's bookings on the Book grid, and see the holder's name there instead. It also adds a month view: a calendar for one resource type at a time (meeting rooms, cars or catering) where each day shows how busy that type is, and clicking a day opens the Book grid at that date and type.

It amends the visibility statements in [resource-booking.md](resource-booking.md#data-model) and reuses its [Utilisation](resource-booking.md#utilisation) formula.

## The Problem

The Book grid shows one day at a time. An employee planning next week steps through each day to find a quiet one. An admin who needs to know who holds Thames on a given day has no screen that says so: the grid shows other people's bookings as "Booked" to everyone, admins included.

The API and the page also disagree. For each booking on the requested day and type, `GET /api/availability` already sends admins the holder's name and the title ([AvailabilityService.cs](../../src/ResourceBooking/Bookings/AvailabilityService.cs), `DayAsync`). The page does not render them, but they are in the network response. The approved design allows it (the `title` field: "shown only to the owner and to admins"). Its API table and Book bullets say otherwise: other people's blocks carry "no owner and no title" and show "with no name".

## Goals

*   **A month calendar per type** at `/month`, with previous and next month. Weeks start on Monday, and past days are shown muted.
*   **A busy share per day, shown as text.** For meeting rooms and cars it is booked hours ÷ open hours across the type's resources. For catering it is orders ÷ units. A kitchen's `Units` is the number of orders it takes per local day. A day with no open hours or no units shows "Closed".
*   **One formula with the Utilisation report.** Both use the same per-resource, per-day calculation.
*   **Clicking a day** opens the Book grid at that date and type.
*   **One visibility rule for the Book grid, applied on the server.** A title goes only to its owner, and a holder's name goes only to admins.
*   **Kitchen capacity in words.** Today the Catering tab reads "Main Kitchen: 4 of 5 free", which doesn't say 5 of what. It becomes "Main Kitchen: 4 of 5 orders left on this day", and the line under it becomes "Takes 5 orders a day, up to 25 portions each, delivered 08:00 to 16:00."

## Non-Goals

*   Titles in History and Traces. Admins still see them there, as audit records.
*   Names or titles in assistant tool results. The tools are unchanged, and a test guards them.
*   Listing a kitchen's orders on the Book grid.
*   Several types on one calendar, or a rolling window.
*   Showing whether a day can still be booked (minimum notice, how far ahead). The share describes bookings, and Book reports the rule when someone tries.
*   Schema or migration changes.

## Open Questions

None. The author can overrule four defaults:

1. An admin's own bookings show the title, like any owner's.
2. Resources with unset opening hours count the whole day, as the report does today.
3. Only the share is shown, with no "still free" figure.
4. The endpoint shape in §3.

## Critical User Journeys (CUJs)

Seed: Ana and Ben are employees, and Sam is an admin. Dates are relative to today.

### CUJ 1: Ana finds a quiet week

*   **Given**: Ana has meeting-room bookings on two days next month, and none in that month's second week.
*   **When**: she opens Month, keeps Meeting rooms selected, and clicks Next.
*   **Then**:
    *   the booked days show a share as text, such as "45% of room hours booked";
    *   the second week's days show "0% of room hours booked";
    *   clicking a day opens Book on that date with Meeting rooms selected.

### CUJ 2: Sam sees who holds Thames

*   **Given**: Ana holds Thames tomorrow, 10:00–11:00, titled "Board prep".
*   **When**: Sam clicks tomorrow in Month.
*   **Then**: Book opens on tomorrow, and the cells for that booking read "Booked: Ana". The response has Ana's name and no title.

### CUJ 3: Ben sees no one else's details

*   **Given**: the same booking.
*   **When**: Ben opens that day.
*   **Then**: the cells read "Booked", and the response has neither a name nor a title. Ben's own bookings still show their titles.

## Technical Plan (Architecture)

```text
Month.razor ──GET /api/availability/month──► endpoint ──► Utilisation.ByDayAsync ◄── Utilisation.ForAsync ◄── GET /api/reports/utilisation
    │ click a day
    ▼
Overview.razor (/?date&type) ──GET /api/availability──► AvailabilityService.DayAsync ──► BookingVisibility.Project
```

### 1. Visibility

`BookingVisibility.Project` decides the `Mine`, `Title` and `Owner` fields of each `BusyBlock`:

| Caller | Title | Owner |
| --- | --- | --- |
| The booking's owner | the title | null (`Mine` is true) |
| An admin, someone else's booking | null | the holder's display name |
| An employee, someone else's booking | null | null |

`GET /api/availability` is the only endpoint that returns other people's bookings. The other read paths are safe for different reasons:
- `/api/bookings/mine` and `/api/bookings/{id}` return only the caller's own bookings.
- The month endpoint returns totals only.
- The assistant's tools use `FreeAsync`, which returns resources, and the caller's own bookings.

History and Traces are admin audit views and stay as they are (see Non-Goals).

### 2. The shared formula

`Utilisation.ByDayAsync(from, to, type, ct)` returns the resources and one `(ResourceId, Day, Used, Available)` row per resource-day, covering every day up to the resource's `LastDay`. It takes over the rules `ForAsync` holds today:

*   **Meeting rooms and cars:**
    *   The window runs from opening to closing time, or the whole local day when either is unset. This matches the code today, and the approved design's "24 hours when those are null".
    *   Used is booked hours clipped to the window. The turnaround gap (the buffer kept free after a booking) does not count.
    *   A booking that crosses midnight counts on each day it touches.
*   **Kitchens:** Used is that day's orders, capped at `Units`, and Available is `Units`.
*   **Always:**
    *   Cancelled bookings never count.
    *   Days run midnight to midnight in the company time zone, so DST days have 23 or 25 hours.
    *   Used and Available are hours for rooms and cars, and orders for kitchens.

`ForAsync` keeps listing every resource, including one retired before `from`, which gets a row with Available 0 and a null ratio. It sums each resource's rows from `ByDayAsync`. Its output is unchanged. `UtilisationTests` must pass as they are, plus one new case for a resource retired before `from`. Each call makes one query for resources and one for bookings.

### 3. Month endpoint

`GET /api/availability/month?year=2026&month=12&type=MeetingRoom` is open to any signed-in user. It returns `MonthOccupancy(Year, Month, Type, Days)`, with one `DayShare(Date, Used, Available, Share)` per calendar day:
- `Share` is ΣUsed ÷ ΣAvailable across the type's resources;
- it is null when ΣAvailable is 0;
- the server owns the null rule, so the client doesn't recompute it.

A month outside 1–12, a year outside 2000–2100, or an unknown type returns 400 Problem Details.

### 4. Client

*   **Month page (`Month.razor`)**, with a "Month" nav link after Book for everyone:
    *   Controls: type tabs, the month name, and Previous and Next.
    *   A seven-column grid. Each day links to `/?date=yyyy-MM-dd&type=MeetingRoom`.
    *   **Text and labels:**
        *   Each day's text and accessible name give the date and the share, rounded to a whole percent: "45% of room hours booked", "45% of car hours booked", or "3 of 5 kitchen orders taken".
        *   A day with a null share shows "Closed".
    *   **Shading:** four bands (under 25%, 25–49%, 50–74%, 75% and over), explained in a legend.
    *   Today is marked. The type and month stay in the query string (`/month?type=Car&year=2026&month=12`).
*   **Book grid (`Overview.razor`):**
    *   It reads `date` and `type` from the query string, falling back to today and Meeting rooms.
    *   When a block has an owner, the cell reads "Booked: {Owner}".
    *   The page doesn't check roles: the server decides what each caller receives.

### 5. Design amendment

[resource-booking.md](resource-booking.md) gets a Visibility table (role × Book grid, month view, assistant, History and Traces). Four places that state the rule point to it:
- the `title` field;
- the `GET /api/availability` row;
- the Book (overview) bullets;
- the employee availability line in Testing.

Its API table gains the month row, and its Shell nav gains Month. The kitchen wording from Goals replaces "n of m free" in its CUJ 1, its `GET /api/availability` row and its Catering card bullet. The test that asserts an admin receives another user's title is reversed. This is the deliberate change this design makes.

### 6. Rules the tests check

*   **The visibility table:** each row is one case.
*   **Assistant tool results:** no result carries another user's name or title, whether Ana or Sam is the caller.
*   **The formula:**
    *   Used ≤ Available on every resource-day.
    *   Splitting a range gives the same sums as computing it whole.
    *   Edge cases: a midnight-crossing car booking, a resource retired mid-month and one retired before `from`, a zero-hour window, the March and October DST months, and cancelled bookings.

## Alternatives Considered

*   **Rolling window from today.** Rejected by author: it has no fixed month boundary to line up with the monthly report, and admins lose last month.
*   **All types on one calendar.** Rejected by author: room hours, car hours and kitchen orders can't be added together, and the cells get crowded.
*   **Admins keep receiving titles.** Rejected by author: titles can name people or private topics. A name is enough to know who holds a slot, so the cost of reversing one field and one test is worth paying.
*   **A day list under the calendar.** Rejected by author: it would be a second place that renders a day and applies the rule, and no one can book from it.
*   **Catering in portions.** Rejected by author: there is no daily portion limit to divide by, while orders per day is the limit the system enforces.
*   **A count of resources with any free hour, instead of the share of hours.** Rejected by author: it doesn't match the Utilisation report, and the Book grid one click away answers "can I still get one?".
*   **Separate formula classes.** Rejected by author: a new `Occupancy` class and a `MonthService` would wrap logic that already lives in `Utilisation`, whose tests cover it.

## Non-Functional Requirements, Risks & Mitigations

*   **Privacy:**
    *   Admins stop receiving titles on the Book grid and keep names. Nothing is added for employees.
    *   The month totals could already be worked out from the Book grid, so the month endpoint is open to every signed-in user.
    *   **Accepted:** History and Traces still show titles to admins.
*   **Performance:** each month request makes two queries. At this scale (tens of resources, hundreds of users, per [resource-booking.md](resource-booking.md#non-functional-requirements-risks--mitigations)) it needs no index or cache.
*   **Misreading:**
    *   A pooled share can hide that one large room is taken, and unset opening hours make a day look quieter.
    *   **Mitigation:** the label says what is counted, and the Book grid is one click away.
*   **Rollout:**
    *   One deploy, with no migration.
    *   A client still cached from the old version keeps working. It never rendered owners, and the new endpoint is used only by the new page.
    *   Rolling back sends titles to admins again.

--------------------------------------------------------------------------------

## Detailed Implementation Plan

Tiers set test depth: `basic` covers examples and edge cases, `standard` adds properties, and `high` adds mutation testing. Each step's tests are written first and seen failing. The exceptions are step 2, a guard over unchanged code, and the UI steps, whose failing tests are the E2E cases in step 6.

1.  **Visibility.** Tier `high`: this is a permission rule, and it reverses an approved behaviour.
    *   Create `src/ResourceBooking/Bookings/BookingVisibility.cs`: `public static class BookingVisibility`, with `(bool Mine, string? Title, string? Owner) Project(Booking b, Guid callerId, bool isAdmin)`. `b.User` must be loaded.
    *   Use it in `AvailabilityService.DayAsync`. Update the `BusyBlock` summary in `src/ResourceBooking.Shared/Dtos/BookingDtos.cs`.
    *   Tests: `tests/ResourceBooking.Tests/Rules/BookingVisibilityTests.cs` (one table-driven case per row), and three caller cases in `tests/ResourceBooking.Tests/Api/BookingApiTests.cs` under "availability and free resources". Reverse its admin-title assertion.
    *   Stryker: `cd tests/ResourceBooking.Tests; dotnet stryker -t mtp -m "**/Bookings/BookingVisibility.cs"` must reach at least 80%.
2.  **Assistant guard.** Tier `high`, test only.
    *   In `tests/ResourceBooking.Tests/Assistant/AssistantTests.cs`, copy `Tools_see_only_the_callers_bookings`, scripting the stub model with `Call(...)`.
    *   Target Ben's seeded "Site visit" car booking. Run as Ana and as Sam.
    *   Calls:
        *   `find_free_resources` (Car, the booking's window);
        *   `get_my_bookings`;
        *   `propose_booking` on that car in an overlapping window;
        *   `propose_change` and `propose_cancellation` with Ben's booking id.
    *   Assert that no `ToolCall` step payload contains "Site visit" or a `"Ben"` JSON string.
3.  **Shared formula.** Tier `standard`: a calculation behind two screens.
    *   In `src/ResourceBooking/Reports/Utilisation.cs`, add `Task<(IReadOnlyList<Resource> Resources, IReadOnlyList<ResourceDay> Days)> ByDayAsync(DateOnly from, DateOnly to, ResourceType? type, CancellationToken ct)` and rewrite `ForAsync` over it. `public sealed record ResourceDay(Guid ResourceId, DateOnly Day, double Used, double Available)` sits in the same file, in `ResourceBooking.Reports`, outside the class.
    *   Tests: `tests/ResourceBooking.Tests/Api/UtilisationTests.cs` gains the edge cases from §6, the retired-before-`from` case, and CsCheck properties. The properties use 0–20 generated confirmed and cancelled bookings on Thames, Pool Car 1 and Main Kitchen, inserted and then deleted per sample, with `Utilisation` resolved from a scope.
    *   Stryker on `**/Reports/Utilisation.cs` must reach at least 80%.
4.  **Month endpoint.** Tier `standard`.
    *   Map `GET /availability/month` (under `/api`) in `src/ResourceBooking/Endpoints/AvailabilityEndpoints.cs`, with parameters `(int year, int month, ResourceType type, Utilisation utilisation, CancellationToken ct)`. It groups `ByDayAsync` rows by date.
    *   Bad input returns `ApiResults.Problem(ApiError.BadRequest("Give a month from 1 to 12 and a year from 2000 to 2100"))`.
    *   Add `MonthOccupancy` and `DayShare` to `BookingDtos.cs`.
    *   Tests: `tests/ResourceBooking.Tests/Api/MonthTests.cs`:
        *   every day of the month is present;
        *   null for a closed day;
        *   the sums match `ForAsync` for the same days (a wiring check);
        *   400 cases, including an unknown type;
        *   401 when signed out;
        *   the same body for Ana and Sam.
5.  **Client.** Tier `basic`: UI plumbing.
    *   **Month page:** create `src/ResourceBooking.Client/Pages/Month.razor` and `Month.razor.css`, with `@attribute [Authorize]`. `CompanyClock` gives today.
    *   **API client:** add `MonthAsync(int year, int month, ResourceType type)` to `src/ResourceBooking.Client/Services/ApiClient.cs`, built like `AvailabilityAsync`.
    *   **Nav:** add `<NavLink href="month">Month</NavLink>` after Book in `src/ResourceBooking.Client/Layout/MainLayout.razor`.
    *   **Book grid**, in `Overview.razor`:
        *   `[SupplyParameterFromQuery(Name = "date")] string? DateQuery` and `[SupplyParameterFromQuery(Name = "type")] string? TypeQuery` (the page already has a `Type` property) are parsed with `DateOnly.TryParseExact(..., "yyyy-MM-dd")` and `Enum.TryParse<ResourceType>`. MeetingRoom, Car and Catering map to the tabs "rooms", "cars" and "catering";
        *   the values are read in `OnParametersSetAsync`, which reloads only when they differ from `date` and `activeTab`. Invalid or missing values fall back to today and rooms;
        *   tab and day changes call `NavigationManager.NavigateTo(..., replace: true)`;
        *   a booked cell with an owner reads "Booked: {Owner}", with the aria-label "{resource}, {time}, booked by {Owner}" (the format `Pages.Cell` locates);
        *   the kitchen lines read as in Goals. The two `Cuj1BookFromOverviewTests` assertions on "4 of 5 free" change to the new text, which is a deliberate change to an approved test.
6.  **E2E.** Tier `basic`.
    *   `tests/ResourceBooking.E2E/Cuj7MonthViewTests.cs` covers CUJs 1–3, continuing the project's CUJ numbering.
    *   Bookings are created through the API, with dates from `Pages.LondonToday()`.
    *   Add `DayLink(DateOnly)` to `tests/ResourceBooking.E2E/Pages.cs`, and reuse `GoToAsync` and `Cell`.
7.  **Docs.** Tier `basic`: review only.
    *   The §5 amendment to `docs/designs/resource-booking.md`.
    *   A Month line after the Book bullet in `README.md`'s feature list.

### Verification Plan

*   **CI commands:**
    *   `dotnet build -c Release`
    *   `dotnet format --verify-no-changes --no-restore`
    *   `dotnet test -c Release --no-build --filter-not-trait "Category=Eval"`

    These need Docker, and the Playwright browsers installed as the README describes.
*   **Stryker:** the two runs above.
*   **Live, after deploy:**
    *   As Sam: Month → a day → Book shows names, and the response has no titles.
    *   As Ben: no names.
