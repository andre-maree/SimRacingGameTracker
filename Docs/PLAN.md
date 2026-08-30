# GameTracker: Implementation Plan

This is the living implementation checklist for the GameTracker technical test. Steps are executed in order, with blocking dependencies documented.

## Status: In Progress

---

## Steps

### ✅ Step 1: Create documentation scaffold
- Create `Docs/PLAN.md`, `Docs/CHOICES AND REASONS.md`, `README.md`, `TODO.md`
- Seed TODO with refresh-token flow item

### ✅ Step 2: Create `GameTracker.Telemetry` library
- Target: `net10.0-windows`
- Interop-only shell (abstractions come later after Domain)
- Add to solution
- Note: `dotnet new classlib` rejects `-f net10.0-windows`; created as `net10.0` then retargeted in the csproj

### ✅ Step 3: Move R3E files to Telemetry library
- Move `R3E.cs` and `RaceRoomTelemetryService` from WPF project
- Re-namespace to `GameTracker.Telemetry.R3E`
- Make `Constant` and nested enums `public` (VERSION constants, Session, GameMode, SessionPhase)
- **Wider than planned:** all 20 `internal struct` types also had to become `public`, otherwise `Marshal.SizeOf<Shared>()` will not compile from the console assembly
- **Bug fixed:** old service declared the region as `"$R3E$"` (trailing `$`). Corrected by sourcing `Constant.SharedMemoryName` as the single point of truth

### ✅ Step 4: Wire console app for spike
- Retarget `net10.0` → `net10.0-windows`
- Add project reference to `GameTracker.Telemetry`
- Replace stock Program.cs
- Windows TFM avoids `CA1416` on `MemoryMappedFile.OpenExisting(string)`, which is Windows-only

### ✅ Step 5: Write spike code
- Open `$R3E` memory-mapped file
- Read VersionMajor/Minor from offsets 0/4 (struct-independent; those slots have never moved)
- Compare `Marshal.SizeOf<Shared>()` vs `Capacity` with page-rounding tolerance (`<= capacity` AND `delta < 4096`, never equality)
- Added Part C field sanity spot-check: a struct can be the right size but still misaligned, which the size check alone cannot catch
- Handle FileNotFoundException gracefully — verified, prints guidance and exits 1 rather than throwing
- Exit codes: 0 = match, 1 = region absent, 2 = layout mismatch

### ✅ Step 6: Run spike and record findings
**GATE CLEARED.** Spike run against a live RaceRoom practice session.
- Version reported by game: 3.5; expected by code: 3.5 — MATCH
- `Marshal.SizeOf<Shared>()`: 43,996 bytes; mapped capacity: 45,056 bytes; delta 1,060 bytes of page padding — consistent
- Part C field spot-check plausible across the board (sim time 155.067, SessionType 0, GameInMenus 0, ModelId 13394, LayoutId 7026, NumCars 1) — confirms alignment, not just size
- Decision: keep `R3E.cs` structs as-is and retain the strict version gate. No regeneration or version shim needed
- Numbers recorded in `CHOICES AND REASONS.md`

### ✅ Step 7: Create `GameTracker.Domain` library
**UNBLOCKED by step 6**
- Target: `net10.0`, zero framework references (no EF, no ASP.NET, no WPF)
- Entities: Game, Car, Track, Session, Stint, Lap, TelemetryRecord, AuditTrail, SyncMetadata, LapInputTelemetry
- Enums: SessionType, SyncOutcome, plus SessionEndReason and AuditAction (needed by the state machine and audit interceptor respectively)
- DTOs: `SyncDtos` (Game/Car/Track + paged `SyncChangesResponse` with tombstones), `TelemetryDtos` (session upload, batch request/response), `AuthDtos` (login request/response)
- `ExternalId` on Car/Track holds the R3E `ModelId`/`LayoutId` confirmed in step 6, so telemetry joins on values shared memory actually reports

### ✅ Step 8: Add telemetry abstractions
- `ITelemetrySource`, `TelemetryFrame`, `R3EValue`, `RecordingEvent` under `GameTracker.Telemetry/Abstractions`
- Reference Domain + Logging.Abstractions
- `TelemetryFrame` shaped against the struct confirmed in step 6 (interface 3.5): GameSimulationTime, GameInMenus, SessionType/Phase, ModelId, LayoutId, NumCars, CompletedLaps, LapDistanceFraction, LapTime current/best, cumulative sectors, CurrentLapValid, InPitlane/PitState, plus ThrottleRaw/BrakeRaw/SteerInputRaw for the per-lap input trace
- `R3EValue` centralises the `-1` "not available" sentinel. Every raw read goes through it, so an unavailable value can never be persisted as a real one — the spike output confirmed `-1` is a legitimate, frequent value
- `RecordingEvent` hierarchy covers all four brief scenarios: SessionStarted/SessionEnded(reason), StintStarted(outLap)/StintEnded, LapCompleted (emitted even when invalid, with null LapTime), PartialLapDiscarded
- `ITelemetrySource.ReadFramesAsync` deliberately does not terminate when the game closes; it pauses and resumes, so callers hold one subscription for the app lifetime

### ✅ Step 9: Remove WPF → BlazorServerApp reference
- Deleted the project reference (no WPF code referenced server types, so nothing broke — the reference was purely accidental coupling that would have dragged the whole server into the client output)
- Added references to Domain, Telemetry, RazorLibrary
- Added Microsoft.Extensions.Hosting
- Pinned Logging.Abstractions to 10.0.11 to match the rest of the solution's Microsoft.Extensions versions

### ✅ Step 10: Fix Radzen styling (server)
- RadzenTheme (material) in App.razor `<head>`, placed **before** `app.css` so app styles win
- `_content/Radzen.Blazor/Radzen.Blazor.js` before `</body>` — without it dialogs/notifications/tooltips fail silently at runtime with no compile error
- @using Radzen / Radzen.Blazor in `_Imports.razor`

### ✅ Step 11: Add _Imports.razor to RazorLibrary
- Radzen usings
- ASP.NET Components usings
- A Razor class library does not inherit the host app's `_Imports`, so it must resolve everything itself

### ✅ Step 12: Fix server Program.cs
- AddControllers() — **fixed latent startup crash:** `MapControllers()` was already called without it, which throws at runtime, not compile time
- AddRoles<IdentityRole>()
- Uncommented MapIdentityApi (under `/identity` group) / MapAdditionalIdentityEndpoints
- AddAuthorization with role policies (`AdminOnly`, `UserOrAdmin`)
- **Also missing:** `UseAuthentication()` / `UseAuthorization()` were absent from the pipeline entirely, so every `[Authorize]` would silently fail
- Added `AddBearerToken` for the WPF client. Order matters: `AddIdentityCookies()` returns `IdentityCookiesBuilder`, so bearer must be registered first (CS1929)

### ✅ Step 13: Model schema in ApplicationDbContext
- Added Domain project reference; DbSets for Games, Cars, Tracks, TelemetryRecords, AuditTrails
- Unique composite indexes on `(GameId, ExternalId)` for Cars and Tracks — `ExternalId` is the R3E `ModelId`/`LayoutId`, unique per game rather than globally
- Non-clustered indexes on `ServerVersion` for Games/Cars/Tracks so the sync cursor query is `O(log n)`
- Composite indexes on `TelemetryRecords(GameId, CarExternalId)` and `(GameId, TrackExternalId)`, plus `SessionId` and `UserId`
- `TelemetryRecord.Id` set `ValueGeneratedNever` — the GUID comes from the client, which is what makes batch upload idempotent
- FKs use `DeleteBehavior.Restrict`: catalogue removal is a soft-delete tombstone, so a cascade would silently destroy sync history
- `UserId` columns capped at 450 chars to match Identity's key length and stay index-eligible

### ✅ Step 14: Implement ServerVersion allocator
- `ISyncable` marker on Game/Car/Track (ServerVersion, IsDeleted, CreatedAtUtc, ModifiedAtUtc) so the interceptor has one contract to work against
- SQL `SEQUENCE` `ServerVersionSequence` declared via `HasSequence<long>` in `OnModelCreating`
- `ServerVersionInterceptor` (SaveChanges + SaveChangesAsync) stamps ServerVersion and timestamps
- **One round trip per SaveChanges, not per row:** `SELECT NEXT VALUE FOR seq FROM (VALUES ...)` allocates the whole batch at once, which matters for the catalogue seed
- **Deletes are converted to tombstones** (`State = Modified`, `IsDeleted = true`). A hard delete would leave every client holding a stale row forever, since nothing would ever tell them it vanished
- `CreatedAtUtc` explicitly marked not-modified on updates so it cannot be overwritten
- Sequence gaps on rolled-back transactions are expected and harmless: the cursor needs ordering, not contiguity
- Registered as a scoped interceptor on the DbContext in `Program.cs`

### ✅ Step 15: Add audit trail
- `AuditInterceptor` writes one `AuditTrails` row per changed entity, capturing the acting user from `ClaimTypes.NameIdentifier`
- Old/new state serialized to JSON **at capture time**, not at flush: after SaveChanges a deleted entry is detached and its values are unreadable
- Inserts audited in `SavedChanges` because store-generated keys do not exist until after the write
- Soft-deletes (Modified with `IsDeleted` flipped true) are recorded as `Delete`, not `Update`, so the audit log matches user intent
- `AuditTrail` rows themselves excluded from auditing, and `_pending` cleared before the nested SaveChanges, so the interceptor cannot recurse
- `SaveChangesFailed` clears pending state — a rolled-back transaction must not leave a phantom audit row
- Empty migration `AddAuditTriggers` adds `TR_Cars_Audit` / `TR_Tracks_Audit` as defence-in-depth for changes that bypass EF; trigger-written rows carry a `sql:` UserId prefix so they are separable from application rows
- **Required, not cosmetic:** `ToTable(tb => tb.HasTrigger(...))` on Cars/Tracks — the SQL Server provider writes via an OUTPUT clause, which SQL Server rejects on triggered tables
- Registered `AddHttpContextAccessor()` and the interceptor in `Program.cs`

### ✅ Step 16: Create EF migration and seeder
- Migrations `AddCatalogueSchema` (tables, indexes, sequence) and `AddAuditTriggers` (raw SQL)
- `DbSeeder` migrates then seeds: Admin/User roles, default admin, and the R3E catalogue from `r3e-data.json`
- Cars parsed from the `cars` map with the class name resolved via the `classes` map
- **Tracks seeded one row per layout, keyed on `LayoutId`** — shared memory reports the layout id, not the track id, so keying on the track would leave telemetry unable to join
- **Seeding is idempotent and change-aware:** unchanged rows are skipped entirely, because touching them would bump `ServerVersion` and force every client into a pointless full resync
- Admin password read from configuration (`Seed:AdminPassword`, i.e. user secrets/environment) and the account is skipped when absent — no guessable default, and no plaintext credential in `appsettings.json`

### ✅ Step 17: Build Radzen catalogue UI
- `CatalogueService` (scoped) exposes paged queries and mutations; `Cars.razor` / `Tracks.razor` grids plus `CarEditor` / `TrackEditor` dialogs
- **True server-side grid:** `Count` + `LoadData` with `args.Skip/Top/Filter/OrderBy` translated straight to SQL via Dynamic LINQ, so the ~4,000-row catalogue never lands in memory per render
- Reads are `AsNoTracking` — a Blazor Server circuit holds one scoped DbContext for the whole connection, so tracking every browsed row would leak for the session
- Tombstones (`IsDeleted`) filtered out of the UI; they exist for sync consumers only
- **Mutations authorized twice:** `AuthorizeView Roles="Admin"` hides the buttons, and `CatalogueService` re-checks the `AdminOnly` policy server-side — hiding a button is not access control
- Delete goes through `Remove`, which the ServerVersion interceptor rewrites into a tombstone, so clients are told about the deletion
- Editors bind to a **clone**, so cancelling a dialog cannot leave half-typed values in the grid
- Duplicate `(GameId, ExternalId)` is caught as `DbUpdateException` and surfaced as a notification: the unique index is the only guard that survives a concurrent editor
- Validation on required fields, max lengths matching the column limits, and positive R3E ids (a wrong `ModelId`/`LayoutId` silently orphans telemetry)
- Added `<RadzenComponents />` and a nav header to `MainLayout` — without the host component, dialogs and notifications no-op silently at runtime

### ✅ Step 18: Add POST /api/auth/login
- `AuthController` validates credentials via `SignInManager.CheckPasswordSignInAsync` and returns a `LoginResponse` (token, expiry, roles)
- `JwtTokenService` mints a 24-hour HMAC-SHA256 token carrying `NameIdentifier` (the stable Id the audit interceptor stamps), name, email and **role claims**, so `[Authorize(Roles = ...)]` needs no per-request database hit
- **Policy scheme `JwtOrCookie`:** forwards to JWT when an `Authorization: Bearer` header is present, otherwise to the Identity cookie — one app serves the Blazor UI and the API without either scheme interfering
- `AddBearerToken(IdentityConstants.BearerScheme)` retained because `MapIdentityApi` signs in with it; removing it would break those endpoints at runtime, not compile time
- **Unknown email and wrong password return the identical 401** — distinguishing them would make the endpoint an account enumeration oracle. Lockout returns 423
- `lockoutOnFailure: true` so Identity's brute-force protection covers API logins, not just the browser flow
- `ClockSkew` cut to 30s; the 5-minute default silently extends every token's lifetime
- `Jwt:Key` is **absent from appsettings.json** by design — user secrets/environment only. A missing key throws in *every* environment, development included: a per-startup ephemeral key would silently sign every client out on each restart. A key shorter than 32 bytes is refused as well, since HMAC-SHA256 needs at least as much key material as its output

### ✅ Step 19: Add GET /api/sync/changes
- `SyncController` returns Games/Cars/Tracks with `ServerVersion > since`, ordered ascending, projected straight to DTOs (no entity materialisation)
- Tombstones included: soft-deleted rows are returned with `IsDeleted = true` so clients purge instead of keeping a row that silently vanished server-side
- **Shared-cursor reconciliation:** the three tables are paged independently, so if any of them truncates, the page is trimmed to the *lowest* truncated version. Publishing a higher cursor would skip rows the other tables still owe the client, and they would never be requested again
- `NextVersion` is the highest version actually returned, and falls back to `since` when the page is empty — the client can never advance past data it did not receive
- `take` clamped to 500 so a client cannot ask the server to materialise the whole catalogue
- `[Authorize(Policy = "UserOrAdmin")]`: the catalogue is readable by any authenticated user, editable only by Admin
- Guard on negative `since` returns 400 rather than silently treating it as a full sync

### ✅ Step 20: Add POST /api/telemetry/batch and /api/sessions
- `SessionsController` upserts the session header; `TelemetryController` accepts batched lap records. Migration `AddSessions` adds the server-side `Sessions` table
- **UserId is stamped from the token's `NameIdentifier`, never taken from the payload** — otherwise a client could file laps against another account
- Re-posting a session updates only the closing fields (`EndedAtUtc`, `EndReason`); identity fields are fixed at session start. A mismatched owner returns 403, so a forged or colliding client GUID cannot overwrite someone else's session
- Telemetry batches are idempotent on the client GUID: existing ids are counted as `Duplicates` instead of inserted twice, which is what makes a retry after a dropped connection safe
- Duplicates **within** a single batch are collapsed first — a client retrying mid-flush can legitimately repeat a record, and EF would otherwise throw on the duplicate key
- An empty GUID is rejected rather than server-generated: silently assigning an id would destroy idempotency for that record
- Batch size capped at 500; `[Authorize(Policy = "UserOrAdmin")]` on both endpoints
- `Session.Stints` is ignored server-side — stints and laps live in the client SQLite store, the server keeps the header plus per-lap `TelemetryRecords`

### ✅ Step 21: Scaffold WPF Blazor host
- Switched the WPF project to `Microsoft.NET.Sdk.Razor` (keeping `UseWPF`) — the plain SDK does not compile `.razor` files, so the components would never have been discovered
- `Components/Routes.razor` + `Layout/MainLayout.razor` + `Pages/Home.razor`, with `AdditionalAssemblies` pointing at the Razor library so shared pages are routable (omit it and those routes 404 with no error)
- `MainWindow.xaml` hosts a full-window `BlazorWebView`; `Services` assigned in code-behind, not bound in XAML, because the WebView resolves its root component during `InitializeComponent` before a binding would evaluate
- `App.xaml.cs` rebuilt on `HostApplicationBuilder` (configuration, logging, hosted-service lifetimes for the later telemetry poller and sync worker) with `AddWpfBlazorWebView`, dev tools under `#if DEBUG`, and a 5-second graceful `StopAsync` so recording state can flush on exit
- Radzen Dialog/Notification/Tooltip/ContextMenu services registered manually — the desktop equivalent of `AddRadzenComponents()`
- `LocalDataPath` (`%LOCALAPPDATA%/GameTracker`) created at startup for the SQLite database and DPAPI token
- Fixed `index.html`: created the missing `_content/GameTrackerRazorLibrary/css/app.css` it referenced, and ordered the Radzen theme before it so app styles win
- `wwwroot` marked `CopyToOutputDirectory` — BlazorWebView serves the host page from disk at runtime

### ✅ Step 22: Add client SQLite ClientDbContext
- `GameTrackerWpfClientApp/Data/ClientDbContext.cs` — the client is a cache, not a second source of truth
- Catalogue rows (Games/Cars/Tracks) use `ValueGeneratedNever`: ids are replicated from the server, never generated locally
- `IsDeleted` is ignored client-side — the sync service deletes the local row on a tombstone, so a retired car can never linger in the offline browser
- Unique `(GameId, ExternalId)` on Cars/Tracks mirrors the server so a replayed sync page cannot duplicate a row
- Sessions → Stints → Laps → LapInputTelemetry cascade locally (unlike the server, which restricts) because local recording data is genuinely owned by its session
- `TelemetryRecord.UploadedAtUtc` added as a client-only marker (`Ignore`d on the server, where every row is uploaded by definition); indexed as `(UploadedAtUtc, RecordedAtUtc)` so the upload worker can drain the pending queue in recording order
- `SyncMetadata.EntityName` unique — one cursor row per synced collection
- Registered in `App.xaml.cs` DI against `%LOCALAPPDATA%/GameTracker/gametracker.db` (Program Files is not writable by a standard user, and per-user data must not be shared between Windows accounts), with `MigrateAsync()` run before `StartAsync` so no component can query an empty schema
- `InitialClientSchema` migration generated under `Data/Migrations`

### ✅ Step 23: Implement DPAPI token storage
- `ITokenStore` + `StoredToken` (`Services/Authentication/ITokenStore.cs`) — an interface because the DPAPI implementation is Windows-only and untestable on a build agent
- `DpapiTokenStore` encrypts with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` to `%LOCALAPPDATA%/GameTracker/token.dat`: a JWT is a bearer credential, so plaintext on disk would let any other user on the machine impersonate the account
- Application-specific entropy scopes the ciphertext to GameTracker; the key itself is managed by Windows, so no key material lives in the repo
- Writes go to `token.dat.tmp` then `File.Move(overwrite: true)` — a crash mid-write must not leave a half-written blob that fails to decrypt on every subsequent launch
- A corrupt/foreign-profile token is logged and deleted rather than thrown: the worst case is one extra sign-in, whereas throwing would break startup permanently
- `StoredToken.IsExpired` applies a 30-second skew, since a token that passes the check locally can still be rejected after network latency and clock drift
- `AuthenticationState` is a **singleton** so the UI and the background sync/upload workers share one sign-in state; it raises `SignedIn`/`SignedOut` events rather than navigating, because a handler runs off the UI thread
- `AuthenticationHandler` (`DelegatingHandler`) attaches the bearer header centrally — a forgotten header at one call site would otherwise produce intermittent 401s — skipping requests that already set `Authorization` so login itself goes out unauthenticated
- On `401` the handler clears the token and signs out: the server has the final say (revocation, key rotation), and this stops background workers retrying a credential that can never succeed
- `AuthenticationService` posts to `api/auth/login` and persists the result; server base address is configuration-driven via `appsettings.json` (`https://localhost:7157/`, matching the server's launch profile)
- Token restored during `OnStartup` before the window shows, so a returning user is not flashed the login screen

### ✅ Step 24: Implement CatalogueSyncService
- `Services/Sync/CatalogueSyncService.cs` pulls `GET /api/sync/changes` into the SQLite mirror
- `SemaphoreSlim(1,1)` taken with `WaitAsync(0)` — overlapping callers (startup, timer, manual refresh) are *turned away* rather than queued, since two runs would share one cursor and race on the same rows, and a second immediate sync has nothing new to fetch
- Paging loops on `HasMore`, carrying `NextVersion` forward as `since`; `MaxPagesPerRun` caps the loop so a malformed `HasMore` cannot spin forever
- One transaction **per page**, not per run: a single long write transaction would block local recording writes to the same SQLite file for the whole catalogue pull
- `LastSyncedVersion` is written inside the same transaction as the rows and only committed with them — if the process dies mid-sync the cursor still points at the last fully-applied page, so the next run re-requests it. Advancing first would lose those rows permanently, because the server only ever returns rows *above* the cursor
- A single `"Catalogue"` cursor, matching the server's shared version sequence and common page cutoff
- Games are upserted and saved before Cars/Tracks in the same page, otherwise a child row could hit a foreign key its parent has not yet satisfied
- Tombstones **delete** the local row (and, for a deleted Game, its cars/tracks explicitly, since catalogue relations do not cascade locally) rather than setting a flag — `IsDeleted` is not even mapped client-side
- `HttpRequestException` is logged at Information and returned as a failed-but-unremarkable result: offline is this application's normal state, not an error
- A `401` short-circuits the run — the auth handler has already cleared the token, so retrying is pointless
- `ApiClientNames.GameTrackerApi` named client shares the authenticated pipeline; a typo'd name would otherwise yield a handler-less client that fails as a mystery 401

### ✅ Step 25: Build offline catalogue browser UI
- `ICatalogueReader` + `CataloguePage<T>` in `GameTrackerRazorLibrary/Catalogue/` — the seam that lets one set of grids serve two very different hosts, with the components never learning which database (or machine) is answering
- Read-only by design: editing stays server-only, so the client cannot be tricked into treating a local change as authoritative
- `CarCatalogueGrid` / `TrackCatalogueGrid` moved into the Razor library, keeping `Count` + `LoadData` so only one page is ever fetched regardless of backing store
- Row actions are a `RenderFragment<T>` parameter rather than baked in: the server passes admin edit/delete buttons, the desktop client passes nothing
- `LocalCatalogueReader` (client) queries the SQLite mirror with **no** `IsDeleted` filter — tombstones were applied as real deletions during sync, so a locally present row is live by definition
- Client uses `IDbContextFactory` for a short-lived context per query; a long-lived grid sharing one context would keep every browsed page tracked for the window's lifetime
- `ServerCatalogueReader` adapts the existing `CatalogueService` (a separate adapter because C# cannot overload on return type, and because the service's admin mutation surface does not belong in a shared read-only contract)
- Server `Cars.razor`/`Tracks.razor` rewritten onto the shared grids, removing the duplicated column and paging code
- Client pages at `/catalogue/cars` and `/catalogue/tracks`, with empty text that tells the user to sync rather than a bare "no records"
- Header gained a sync button, disabled while running so a second click visibly does nothing rather than silently hitting the service's overlap guard
- `System.Linq.Dynamic.Core` added to the client so Radzen's filter/sort expressions execute in SQLite instead of in memory

### ✅ Step 26: Implement version gate and SharedMemoryTelemetrySource
- `SharedMemoryVersionGate` validates `(3, 5)` from the step 6 spike, sourced from `Constant.VersionMajor/VersionMinor` rather than re-typed literals
- The gate exists because the failure it prevents is **silent**: `Marshal.PtrToStructure` succeeds against *any* bytes, so a changed layout yields plausible-but-wrong lap times rather than an exception. No data beats corrupt data that gets uploaded and trusted
- Major mismatch → reject (layout restructured, no offset is trustworthy). Minor *lower* → reject (game predates fields we read). Minor *higher* → accept with a log note, since R3E appends fields and our prefix still reads correctly
- Size check is **page-tolerant**, never equality: Windows rounds the view up to a 4 KiB page, exactly as the spike measured (43,996-byte struct in a 45,056-byte view = 1,060 bytes of padding). Rejects only when the view is smaller than the struct, or has ≥ one full page of slack
- `SharedMemoryTelemetrySource` implements `ITelemetrySource` with a **60 Hz** poll — matched to the game's update rate, since faster only re-reads identical bytes and slower risks missing the frame a lap rolls over
- **1s** reconnect retry while disconnected: the region only exists during a session, so "not connected" is the normal idle state and a 60 Hz spin on `OpenExisting` would burn CPU for nothing
- Endless stream by design: it pauses and resumes rather than terminating, so consumers need no restart logic of their own
- Version failure logs at **Error** once and latches `_incompatibleReported`, avoiding 60 identical fatal messages per second; disconnecting on rejection means switching to a compatible build re-runs the gate cleanly
- Mapping funnels every value through `R3EValue` so the `-1` sentinel cannot become a real reading, with two deliberate exceptions: `Gear` uses `-2` for unavailable (`-1` is reverse), and `InPitLane` defaults to false so a missing reading keeps a stint open instead of inventing a pit stop
- Uses `ThrottleRaw`/`BrakeRaw`/`SteerInputRaw` — a driver input trace wants the inputs before assists
- Registered as a singleton in the WPF host because it owns the memory-mapped handle

### ✅ Step 27: Implement SessionStateMachine
- `GameTracker.Telemetry/Recording/SessionStateMachine.cs` — a **pure** state machine: no database, no clock of its own, no I/O, so the awkward cases (restart, quit mid-lap, pit cycle) are testable without RaceRoom running
- The core difficulty is that RaceRoom announces nothing: there is no "session started" flag, so every transition is *inferred* from the few dependable signals
- **Monotonic-time restart guard**: `GameSimulationTime` decreasing by more than 0.5s means a restart. This is the case that silently corrupts data if missed — laps from the new run would otherwise be appended to the old session — and simulation time is the only reliable signal, since car and track are unchanged across a restart. The tolerance absorbs the occasional stale frame
- **Latched lap validity**: R3E reports validity for the lap *in progress* and often clears a cut before the line, so one invalid frame condemns the whole lap, which matches driver expectation
- **Pit-driven stint boundaries**: pit entry ends the stint, pit exit starts the next one flagged as an out-lap. `_currentLapTouchedPits` keeps in/out laps from ever being presented as flying laps
- **Menu/disconnect close path**: a menu frame ends the session as `Abandoned`; `Disconnect()` is a separate entry point because a disconnect is the *absence* of frames and cannot be inferred from one
- A lap in progress at session end is emitted as `PartialLapDiscarded`, never saved with a guessed time — a truncated lap looks real in a results table, which is worse than an explicit gap
- Lap times come from `LapTimePreviousSelf`/`SectorTimesPreviousSelf` (added to `TelemetryFrame` in this step): at 60 Hz the *current* lap time is always sampled shortly before the line and under-reports by up to a frame
- `_lastCompletedLaps` is anchored to the game's own counter on session start, so joining a session already in progress does not replay laps that were already driven
- `Process` returns a list because one frame can legitimately produce several events — a restart closes a lap, a stint and a session, then opens new ones

### ✅ Step 28: Implement SessionRecorder pipeline
- `GameTrackerWpfClientApp/Services/Recording/SessionRecorder.cs` — a `BackgroundService` splitting the poll loop from persistence with a **bounded** `Channel<TelemetryFrame>`
- The split exists because SQLite writes and Brotli compression are slow *and jittery*: on the poll loop a single blocking write skips the frame a lap rolls over on, which is exactly the frame that matters and is unrecoverable
- Capacity **600** (~10s at 60 Hz) with `FullMode = DropOldest`: unbounded would turn a stalled disk into unbounded memory growth over a long stint, and dropping a stale sample costs a gap in an input trace while blocking the producer costs a whole lap
- `SingleReader`/`SingleWriter` are set because both are true, letting the channel take its cheaper fast path
- Persistence uses a **short-lived context per event batch** from `IDbContextFactory`: the recorder outlives every session, so one long-lived change tracker would accumulate every lap in memory
- The transaction is scoped to **one frame's events**, matching the state machine's own atomic unit — a restart therefore cannot commit the new session without also closing the old one
- The `Lap`, its `LapInputTelemetry` blob and the queued `TelemetryRecord` are written in the **same transaction**: writing the upload row separately would let a crash between the two leave a lap that is never uploaded and has nothing indicating it is missing
- Write failures are logged and swallowed, never fatal — the driver is still on track, and abandoning the stream would lose every *remaining* lap as well as the failed one
- Producer completion drains the channel, then `Disconnect()` is called with `CancellationToken.None` so the closing write survives shutdown, when the stopping token is already cancelled
- Input samples are buffered only while a stint is genuinely open, so menu and garage frames cannot leak into a trace; `PartialLapDiscarded` clears the buffer and writes nothing
- UI notification is throttled to **500ms**, since the frame stream would otherwise re-render the same status 60 times a second — with a deliberate bypass on lap completion, the one event the user is actually waiting for
- `GameTracker.Telemetry/Recording/LapInputTraceBuffer.cs` implements the Part 5 encoding: **columnar** float32 (all throttle, then brake, then steering) → Brotli, plus an uncompressed **min/max decimated** ~500-sample preview
- Columnar rather than interleaved because pedal traces are smooth and autocorrelated; keeping like values adjacent is what lets Brotli reach tens of KB
- Min/max per bucket rather than every-Nth sampling, because plain sampling drops the brief full-throttle and full-brake spikes that are the entire point of the trace
- Little-endian written explicitly rather than via `BitConverter`, so a blob written on the desktop and read on the server does not depend on the architecture that touched it

### ✅ Step 29: Implement background uploader
- `GameTrackerWpfClientApp/Services/Upload/TelemetryUploadService.cs` — a `BackgroundService` draining `LocalTelemetry` and its parent sessions to the server
- Upload is deliberately **outside** the recording path: a lap is durable in SQLite the instant it is driven, so losing connectivity delays publication but never data
- Retries are safe only because both endpoints are **idempotent on the client-generated GUID**. The ambiguous failure — a request that reached the server and committed, but whose response was lost — is indistinguishable from a real failure on the client, so retrying must always be correct
- **Sessions are upserted before their laps**, since laps are stored against a session id and the reverse order would briefly expose laps whose parent session does not exist
- The session is **re-posted every drain** rather than marked "sent once": the endpoint is an upsert, and a session uploaded mid-run must later publish its closing details
- `UploadedAtUtc` is stamped **only after a 2xx**, and server-reported **duplicates count as delivered** — a duplicate is positive confirmation that an earlier attempt committed, so leaving it queued would retry it forever
- Backoff doubles from **5s**, **capped at 5 minutes**: uncapped doubling would leave the client sulking for hours after a brief server restart
- Success resets the backoff; idle polling is **30s**
- Skips entirely while signed out, since requests certain to 401 only burn the backoff window and spam the log
- 401 stops the cycle without retry (the auth handler has already cleared the token); other 4xx logs at Warning because a rejected payload will never succeed on retry and needs a human
- The loop swallows all exceptions by design — it is the only thing that ever clears the local queue, so letting it die would silently strand every lap recorded from that point on
- Batches of **500** to match the server limit, oldest first so a backlog publishes in the order it was driven; a short batch ends the drain

### ✅ Step 30: Build recorded-sessions desktop UI
- `GameTrackerWpfClientApp/Components/Pages/Sessions.razor` — Radzen master/detail: a sessions grid with single selection driving a lap grid below, both visible at once so a summary can be read against its laps
- `GameTrackerWpfClientApp/Services/Recording/RecordedSessionReader.cs` projects flat rows rather than handing entities to the grid, because car and track *names* live in the catalogue mirror and are joined on external id, not navigation properties — doing it once here avoids a per-row lookup during render
- Lap counts and best lap are aggregated **in SQL**: an endurance session holds hundreds of laps the summary row would otherwise materialise just to count
- Best lap considers **valid laps only** — an invalid lap is not a lap time
- `HasInputTrace` is an existence check, never a blob load, so opening a session does not pull megabytes of compressed input traces
- Null lap and sector times render as **"—"**, never `0:00.000`: a missing time and a zero time mean very different things, and a zero would read as a real (absurd) result
- Invalid laps are flagged **twice** — a Danger badge and a dimmed row — because a badge alone is easy to miss when scanning a column of times. Pit laps carry their own badge so an in/out lap is never mistaken for a slow flying lap
- Per-session and per-lap **upload badges** surface the offline backlog rather than letting it accumulate silently
- Live recorder status comes from `SessionRecorder.StatusChanged` rather than a timer: a poll would either lag a completed lap or spin for nothing. The handler marshals through `InvokeAsync` since it fires on the recorder's consumer task
- `IDisposable` unsubscribes — the recorder outlives every page, so a leaked handler would pin the component and render into a dead context
- Selection is re-established **by id** after a refresh, since reloading produces fresh row instances and reference-based selection would silently clear
- Missing catalogue rows fall back to `Car #id` / `Track #id`: the session is still valid data, and the id beats an empty cell
- Times are converted to local for display while remaining UTC in storage

### ✅ Step 31: Add structured logging
- `GameTrackerBlazorServerApp/Middleware/RequestLoggingScopeMiddleware.cs` wraps every request in a scope carrying `RequestId`, `RequestPath` and `UserId`
- Registered as **middleware, not per controller**, so it covers endpoints added later and cannot be forgotten — and placed **after** `UseAuthentication`, since the user claim does not exist on the principal before that
- The server serves concurrent clients, so lines from different requests interleave; without a correlation key a failed batch cannot be reconstructed from the noise, and "which user" is the first question asked when a client reports missing laps
- `TraceIdentifier` is reused as the key rather than a new GUID, because ASP.NET Core already surfaces it in error responses — linking a user-visible failure straight to the log
- Logs the **user id, never the email**: log files are copied around far more casually than the database
- Timing is scoped to `/api` only; Blazor static assets and circuit polling would otherwise bury the lines that matter. Level is 5xx→Error, 4xx→Warning, else Information, so a level filter alone does the triage
- `GameTrackerWpfClientApp/Services/Logging/FileLoggerProvider.cs` — a rolling file sink under `%LOCALAPPDATA%/GameTracker/logs`, beside the database
- A file sink is **mandatory** for the desktop app specifically: it has no console, so a crash or stalled upload on a user's machine otherwise leaves no evidence whatsoever
- Hand-rolled rather than adding Serilog — the requirement is one rolling text file, and a logging framework plus sink packages is a large dependency for that
- Rolls **daily by date-stamped filename**, which needs no rename or lock, with 14-day retention swept on startup (best-effort: a locked old file must not block startup)
- Writes on a dedicated `IsBackground` thread at `BelowNormal` priority — a pool thread parked for the process lifetime starves everything else, and diagnostics must never compete with the 60 Hz poll loop
- Queue is **bounded at 2048 with `TryAdd`**: a stalled disk must not exhaust memory or block the recording consumer task, and dropping diagnostics beats taking down the app producing them
- The write loop swallows exceptions by design — a throwing logger turns a disk problem into an application failure, with nowhere left to report it
- `ISupportExternalScope` is implemented so scopes render into the file; without them the flat log cannot tie a lap-upload failure back to its session
- Scopes added at both hot spots: `SessionId` around recorder persistence, and a per-batch `UploadBatchId` in the uploader, since a partially-successful upload spans several lines
- Global handlers for `AppDomain.UnhandledException`, `DispatcherUnhandledException` and `TaskScheduler.UnobservedTaskException`, attached only once the logger exists; the unobserved-task case is the failure that otherwise goes completely unnoticed in a background worker
- EF Core command logging pinned to Warning in both hosts: at Information it logs every SQL statement, which would drown a per-lap write path

### ✅ Step 32: Implement Part 5 capture and storage
- Capture and encoding landed with the recorder in step 28 (`LapInputTraceBuffer`); this step added the **decoder** (`GameTracker.Telemetry/Recording/LapInputTraceDecoder.cs`) and verified the format end-to-end
- Decoder lives beside the encoder deliberately, so the byte layout is defined in exactly one place — a format whose read and write sides can drift apart fails *silently*, decoding garbage as plausible-but-wrong floats rather than throwing
- **Measured the format instead of assuming it, and the original design was wrong.** A synthetic 90s lap at 60 Hz (5,400 samples/channel, 64,800 bytes raw) compressed to only **48,126 bytes — 74% of raw**, nowhere near the "tens of KB" the plan claimed
- Root cause: `float32` mantissa low bits are effectively random. A pedal sensor has nothing like 24 bits of real precision, so those bits stored pure noise, which is incompressible by construction and dominated the payload. Columnar ordering was necessary but not sufficient
- Fix: **quantise to 16 bits and delta-encode** before Brotli → **11,990 bytes (18.5% of raw), a 4× improvement**. Quantisation step is ~1.5e-5, about two orders of magnitude finer than any real input device
- Deltas exploit 60 Hz sampling: consecutive values barely differ, so differences cluster near zero, which is the distribution Brotli encodes well. Encoder and decoder both use wrapping `ushort` arithmetic, making it lossless with respect to the quantised series
- Verified: round-trip max error **1.53e-5** (exactly the quantisation step), `TimeAt` correct, min/max decimation confirmed to preserve full-throttle and full-brake spikes, and an oversized declared sample count returns null rather than decoding garbage
- Preview stays **float32 and uncompressed** — at ~500 samples/channel it is 6 KB, so quantising it would save little while adding a second format to maintain
- `DecodePreview` vs `DecodeFull` split keeps the default render path free of any decompression; `RecordedSessionReader.GetInputTraceAsync` makes full resolution **opt-in** and projects away the unwanted blob column so it is never transferred
- A corrupt or truncated blob falls back to the preview rather than showing nothing, since the two are stored independently
- `Docs/CHOICES AND REASONS.md` corrected: the disproven estimate is replaced with the measured figures and an explicit note that the original reasoning was wrong

### ✅ Step 33: Render input-telemetry plot
- `InputTraceProjector` flattens a `DecodedInputTrace` into chart points, decimating to at
  most 1,200 plotted points because an SVG chart cannot resolve more points than pixels
- Preview traces have no meaningful sample rate (they are min/max decimated, so unevenly
  spaced in time), so they are plotted against lap-progress percentage while full traces
  are plotted against elapsed seconds
- `InputTraceChart` binds the cheap preview by default and only decompresses the blob when
  the user opts into full resolution, keeping the default render path allocation-free
- Two-lap overlay reuses the same channel hues at lower stroke weight so the channel stays
  identifiable and the lap is distinguished by weight
- A warning is shown when the two overlaid traces ended up on different axes (possible when
  a corrupt blob falls back to its preview), since comparing them would be misleading
- `Sessions.razor` gained lap selection and an overlay toggle to drive the chart

### ✅ Step 34: Complete documentation
- README updated with clean-checkout commands, verified API routes, and project structure
- Corrected the README's stale Part 5 claims: it still described raw `float32` + Brotli and
  "~90,000 rows saved per lap"; both were replaced with the measured quantised-delta figures
- Documented the Part 5 plotting decisions in CHOICES AND REASONS, including why a decimated
  preview cannot use a real time axis
- Added the quantisation loss (1.5e-5) and lap-progress overlay alignment as explicit known
  weaknesses rather than leaving them implicit
- TODO.md item 3 (two-lap overlay) marked implemented; remaining distance-alignment limitation
  recorded instead
- Removed the empty root `plan.md`, which duplicated `Docs/PLAN.md` in name only
- Added a documentation map so the four docs' roles are unambiguous

---

## Blocking Dependencies

- Step 7 (Domain) blocked until step 6 (spike findings) complete
- Step 8 (telemetry abstractions) depends on step 7 (Domain entities)
- All subsequent work depends on Domain being available

---

## Decision Table for Step 6

| Spike Result | Action |
|---|---|
| 3/5, size fits | Hardcode Supported = (3, 5), proceed |
| Different version, size fits | Accept known-good list, spot-check fields |
| Size mismatch | Re-copy R3E.cs from upstream, re-run spike |

---

Last updated: Step 1
