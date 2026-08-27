# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

- Build the solution: `dotnet build WG.AP.Automation.slnx`
- Run all tests: `dotnet test WG.AP.Tests/WG.AP.Tests.csproj`
- Run a single test: `dotnet test WG.AP.Tests/WG.AP.Tests.csproj --filter "FullyQualifiedName~GraphMailboxProcessorTests.MoveMessageAsync_PreservesTheImmutableId_AndTheMessageIsReadableByItAfterTheMove"`
- Run the console app: `dotnet run --project WG.AP.Processing` (uses `appsettings.Production.json` unless `DOTNET_ENVIRONMENT=Development` is set — an IDE F5 run picks up `Development` via `WG.AP.Processing/Properties/launchSettings.json`; a plain CLI `dotnet run` does not)
- CI (`.github/workflows/unit-tests.yml`) runs on PRs into `development`/`master`/`main`: restores, builds, and tests only `WG.AP.Tests.csproj` in Release config.

**Known repo state:** `WG.AP.Email/Class1.cs` is leftover scaffold code that does not compile (calls to methods that don't exist on `IMailSource`, invalid syntax) and currently blocks `dotnet build`/`dotnet test` for the whole solution. The repo owner has explicitly asked for it to be left in place — they'll delete it themselves. Do not delete or "fix" it. If you need to verify a build/test run, temporarily rename it aside (e.g. `Class1.cs` → `Class1.cs.disabled`), run the build/tests, then restore the exact file and confirm via `git status` that no diff was introduced.

## Architecture

This is a .NET 10 solution (`WG.AP.Automation.slnx`) automating AP (accounts payable) invoice intake from a mailbox. Only the mailbox intake slice (SDP-178) is implemented; invoice parsing/consumption (SDP-157) is future work.

**Projects:**
- `WG.AP.Core` — dependency-free abstractions only (`Abstractions/IMailSource.cs`, `IMailSender.cs`, plus DTOs: `MailMessageSummary`, `MailAttachmentSummary`, `MailDestinationFolder`, `MailSendRequest`, `MailboxDeltaResult`, `IMailboxSyncStateStore`). No reference to `Microsoft.Graph`/`Azure.Identity` — downstream consumers depend on the contract, not the Graph SDK.
- `WG.AP.Email` — the Graph-backed implementation: `GraphMailboxProcessor` (implements both `IMailSource` and `IMailSender`), `MailboxSyncProcessor` (delta-sync orchestrator), `MailboxOptions`.
- `WG.AP.DataAccess` — currently holds only `FileMailboxSyncStateStore` (a JSON-file-backed `IMailboxSyncStateStore`) and its options. No database/EF Core exists anywhere in this repo yet; that's deliberate, not an oversight — see the note below.
- `WG.AP.Processing` (assembly/namespace `WG.AP.Processor`, project folder `WG.AP.Processing` — the names don't match) — the entry point. A console app hosted via `Host.CreateApplicationBuilder`; `Program.cs` wires up all DI and config; `APProcessor` is the top-level orchestrator; `Logging/` holds a custom file-logging provider.
- `WG.AP.Invoice`, `WG.AP.Reporting`, `WG.AP.Integrations.Pace` — empty scaffolds for future work, already wired with project references (`Invoice`/`Reporting` → `Core` + `DataAccess`; `Integrations.Pace` → `Core`) but no implementation yet.
- `WG.AP.Tests` — xunit tests referencing every other project. `Email/FakeGraphHandler.cs` fakes the Graph HTTP layer (routes by method + URL match) so Graph-dependent tests run with no real tenant, credentials, or network access.

**The mailbox pipeline** (`Program.cs` → `APProcessor` → `MailboxSyncProcessor` → `GraphMailboxProcessor` → `IMailboxSyncStateStore`):

1. `Program.cs` binds `MailboxOptions`/`MailboxSyncStateOptions`/`FileLoggerOptions`, builds a `GraphServiceClient` from app-only `ClientSecretCredential`, and registers `GraphMailboxProcessor` as both `IMailSource` and `IMailSender`, `FileMailboxSyncStateStore` as `IMailboxSyncStateStore`, `MailboxSyncProcessor`, and `APProcessor`.
2. `APProcessor.ProcessInvoicesAsync` is the single entry point: `ValidateAuthAsync` (auth smoke test) → `EnsureFoldersExistAsync` (creates `Processed`/`Errors`/`NeedsReview` under Inbox if missing) → `MailboxSyncProcessor.GetNewMessagesAsync` (delta-fetch only new/changed mail) → per-message logging → `MailboxSyncProcessor.CommitAsync` (persist the new delta link) — all inside one top-level try/catch that sets `Environment.ExitCode = 1` on failure, since this runs unattended under Windows Task Scheduler with no process supervisor.
3. Delta sync: `GraphMailboxProcessor.GetInboxDeltaAsync` calls Graph's mail delta query (`.../mailFolders('inbox')/messages/delta()`), paginating via `OdataNextLink` until a page carries `OdataDeltaLink`, and drops tombstoned (`@removed`) entries. **The new delta link is committed only after the caller has finished handling the whole batch, not immediately after fetching** — so a crash mid-processing re-delivers the same batch next run instead of silently losing it. Don't move that commit earlier without preserving this property.
4. State persistence: `FileMailboxSyncStateStore` writes one JSON file per mailbox user (temp file + atomic `File.Move`) under `MailboxSyncStateOptions.DataDirectory` (defaults next to the exe — losing this file just triggers a harmless full resync, which is why it's fine for it to live somewhere that a redeploy could wipe).

**Auth & immutable ids:** app-only (client credentials), not delegated — meant to run unattended. Every Graph request adds a `Prefer: IdType="ImmutableId"` header (`GraphMailboxProcessor.ApplyImmutableId`) because a message's default id changes when it moves folders; without the immutable id, both the move-then-reread guarantee and future dedup logic would break the moment a message got filed into a destination folder.

**Logging:** `Program.cs` registers a console provider *and* a custom `WG.AP.Processor.Logging.FileLoggerProvider`, so every `ILogger<T>` call anywhere in the app is written both to the console and to a daily text file under `%ProgramData%\WG.AP.Automation\Logs\` (path/level configurable via the `FileLogging` config section) — this exists because the process runs unattended with no console to watch. `FileLogger.Log` swallows its own I/O failures so a broken log can never take down mailbox processing. **When adding new code that talks to an external system** (Graph, a future DB, a future integration), follow the established pattern in `GraphMailboxProcessor.cs`: wrap the call in try/catch, `logger.LogError(exception, "<message with the relevant id(s) — mailbox/message/attachment/etc.>")`, then rethrow, so the file log always shows exactly where a failure happened. Note the one wrinkle: C# disallows `yield return` inside a `try` block that has a `catch`, so an `IAsyncEnumerable` method (like `EnumerateInboxAsync`) needs its Graph calls split into non-iterator helper methods that each do their own try/catch/log/rethrow.

**Config:** `MailboxOptions`, `MailboxSyncStateOptions`, and `FileLoggerOptions` all follow the same shape — bound via `IOptions<T>` from a named `SectionName`, with `appsettings.{Environment}.json` overrides. `MailboxOptions.IsTestMailbox` must be `true`, enforced by `.ValidateOnStart()` in `Program.cs` — a hard startup guard against ever pointing this adapter at the live AP inbox, since it moves mail into subfolders.

**Scope boundary (SDP-178 vs SDP-157):** this pipeline only surfaces mailbox state/movement primitives — it does not parse invoices, decide success/failure, or move messages into `Processed`/`Errors`/`NeedsReview` yet. That consumption logic is future work (SDP-157) meant to depend on `IMailSource`/`IMailSender`, not on `GraphMailboxProcessor` directly.

See `docs/GraphMailboxProcessor.md` for a deeper reference on the Graph adapter (method-by-method behavior, Entra ID prerequisites, known follow-ups like large-attachment streaming) — it predates the delta-sync and file-logging work described above, so treat it as background on the original adapter rather than the current end-to-end pipeline.
