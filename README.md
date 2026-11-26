# Resource booking

An internal web app for booking meeting rooms, pool cars and catering. Employees book from a day overview or by asking an assistant in plain words. Admins manage users and resources, retire rooms and deactivate leavers, and read utilisation, history and assistant traces. The server enforces every booking rule, and PostgreSQL refuses double bookings.

* **Live demo:** https://app-production-6da71d52.up.railway.app. Sign in as any seeded user: Ana and Ben are employees, Sam is an admin. The assistant answers only once an OpenAI key is configured there; until then it says it isn't set up.
* **Design:** [docs/designs/resource-booking.md](docs/designs/resource-booking.md) covers the problem, user journeys, data model, rules, API, lock order, assistant and the build plan. [docs/designs/month-view.md](docs/designs/month-view.md) adds the month view and narrows what admins see of other people's bookings.

## What it does

* **Book.** A grid of rooms and cars per half hour, and kitchens with free units per day. Clicking a free cell opens the booking form. A clash offers free alternatives, and a room booking offers "Add catering".
* **Month.** A calendar for one type at a time showing how busy each day is (share of open hours, or kitchen orders taken). Clicking a day opens Book on that date. Admins see who holds each booking on Book, never its title.
* **Rules.** Opening hours, notice, how far ahead, length, capacity, a turnaround gap for cars, and kitchen units. Each refusal says which rule failed.
* **My bookings.** Edit or cancel upcoming bookings, and read notices when an admin's change cancels one.
* **Admin.** Users, resources (each type shows only its rule fields), retirement and deactivation with cascading cancellations, utilisation, history and traces.
* **Assistant.** "A room for 8 tomorrow morning, and lunch at 12:30" becomes proposals. Each opens the booking form pre-filled, and nothing is booked until the user confirms. Every step is traced for admins, and spend is held to a daily token budget.

## Stack

.NET 10: ASP.NET Core minimal APIs serving a Blazor WebAssembly client built with Fluent UI. EF Core 10 on PostgreSQL. A `btree_gist` exclusion constraint stops overlapping bookings. Microsoft.Extensions.AI with OpenAI powers the assistant. It is hosted on Railway from the `Dockerfile`.

```text
src/ResourceBooking          server: endpoints, booking service and rules, audit, reports, assistant
src/ResourceBooking.Client   Blazor WebAssembly pages and components
src/ResourceBooking.Shared   DTOs, enums and validation shared by both
tests/ResourceBooking.Tests  unit, property, model and API tests against PostgreSQL in Docker
tests/ResourceBooking.E2E    Playwright journeys (CUJ 1 to 6) against the real app
formal/AdminLocks.tla        TLA+ model of the row-lock order
evals/cases.json             assistant eval cases
```

## Run it locally

You need the .NET 10 SDK and Docker.

```powershell
docker run -d --name booking-db -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:17-alpine
```

```powershell
$env:ConnectionStrings__Db = "Host=localhost;Port=5432;Database=booking;Username=postgres;Password=postgres"
```

```powershell
dotnet run --project src/ResourceBooking
```

Open http://localhost:5210. On start the app applies its migrations and, if the database is empty, seeds three users, four resources and some bookings. There is no reset button; to restore the seed, call `POST /api/admin/reseed` with an admin's session, as the end-to-end tests do (`tests/ResourceBooking.E2E/AppFixture.cs`).

The assistant needs an OpenAI key. Without one it replies that it isn't set up, and the forms work as usual:

```powershell
dotnet user-secrets set "OpenAI:ApiKey" "<your key>" --project src/ResourceBooking
```

### Settings

| Setting (environment variable) | Default | What it does |
|---|---|---|
| `ConnectionStrings__Db` | none | PostgreSQL connection string |
| `OpenAI__ApiKey`, `OpenAI__Model` | none, `gpt-4.1-mini` | the assistant's model; the key never reaches the browser |
| `Assistant__DailyTokenBudget` | 200000 | tokens all users may spend per London day |
| `Assistant__MessagesPerMinute` | 10 | per user; 0 turns the limit off |
| `Assistant__MaxOutputTokens` | 800 | the most tokens one model call may write |
| `Assistant__TimeoutSeconds` | 30 | how long one message may take |
| `Assistant__MaxTurns` | 20 | messages per conversation |
| `Assistant__MaxIterations` | 5 | rounds of tool calls per message |
| `Demo__ReseedWindowSeconds` | 30 | one reseed per window, shared by everyone; 0 turns the limit off |

## Tests

Docker must be running: Testcontainers starts a PostgreSQL container for each test project. Build first, then install the browser the end-to-end journeys use (this needs PowerShell 7, `pwsh`):

```powershell
dotnet build
```

```powershell
pwsh tests/ResourceBooking.E2E/bin/Debug/net10.0/playwright.ps1 install chromium
```

```powershell
dotnet test --filter-not-trait "Category=Eval"
```

This is what CI runs on pull requests and on pushes to `main`. It covers:
* unit and property tests of the rules;
* stateful model tests of user administration and the token budget;
* API tests against the real database;
* the Playwright journeys.

Further checks run on demand:

* **Journeys against a deployed app:** set `E2E_BASE_URL` to its address. Each journey resets the deployed app's data first, and a deployed app allows one reset per 30 seconds (`Demo__ReseedWindowSeconds`), so run journeys one at a time, at least 30 seconds apart. The assistant journeys need the scripted model, so they skip.
* **Assistant evals against the real model:** set `OpenAI__ApiKey`, then run `dotnet test --project tests/ResourceBooking.Tests --filter-trait "Category=Eval"`.
* **Mutation testing:** run `dotnet tool restore` once, then `cd tests/ResourceBooking.Tests` and `dotnet stryker -t mtp -m "**/Assistant/TokenBudget.cs"`. Use any file pattern for `-m`. Add `--msbuild-path` if Visual Studio's MSBuild is picked up.
* **The lock-order model:** needs Java and `tla2tools.jar` from the TLA+ tools release. Run `java -cp tla2tools.jar tlc2.TLC -config formal/AdminLocks.cfg formal/AdminLocks.tla`. It checks for deadlock and five invariants. Six switches in the config each add a known mistake, and the checker catches every one.

## What is mocked

* **Single sign-on.** The sign-in page lists the seeded users and signs in whoever is clicked. It is labelled as standing in for company SSO. Sessions are cookie-based, and every API call is checked on the server.
* **Notifications.** These are in-app notices only, with no email.
* **The model, in tests and CI.** A scripted stub plays back tool calls, so the assistant's journeys and limits run without a key.

## Known limits

* Anyone with the demo link can sign in as the admin. Reseed restores the data, and the token budget caps model spend.
* The budget is shared by everyone. One visitor can use the day's budget, which turns the assistant off (not the forms) until London midnight.
* Two messages sent at once to one conversation share a turn, so both sets of proposals stay open.
* The keys that sign session cookies are stored unencrypted in the database. Production would protect them with a key vault or certificate.
* One replica. Migrations run on start.
* The company time zone is London throughout: the client, the assistant's prompt and the seed assume it.
* Every grid cell is a labelled button, but keyboard-only and screen-reader use of the whole page hasn't been walked through yet.

## How AI tools were used

The app was built with Claude Code as a pair programmer, working through a fixed process:

* **Design.** A brainstorm with the developer led to a design doc. Fresh reviewer agents then critiqued the doc for scope, design, comprehension and implementation readiness, and the developer approved it before any code was written.
* **Tests first.** Each plan step has a test tier set by its risk. Rules got property tests. User administration and the token budget got stateful model tests. Locking got the TLA+ model, which found a real deadlock in an earlier lock order. Mutation testing measured how strong the tests are.
* **Review before merge.** From the database schema on, every step went through independent correctness, security and simplicity reviews by fresh agents that saw only the spec and the diff. Each set of fixes was reviewed again. Risky items went to the developer as explicit decisions.
* **The assistant itself.** It uses OpenAI through Microsoft.Extensions.AI. The model only proposes, and code validates every booking. Deterministic checks such as rules, overlaps and permissions never depend on the model.
