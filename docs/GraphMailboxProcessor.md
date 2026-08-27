# Graph Mailbox Adapter

Reference doc for the Microsoft Graph mailbox integration built for **SDP-178**. Covers what it does, how it authenticates, what each method is for, how it's configured, how it runs today, and how it's tested.

## What it is

`GraphMailboxProcessor` (`WG.AP.Email\GraphMailboxProcessor.cs`) is the mail-boundary adapter between this solution and a Microsoft 365 mailbox via Microsoft Graph. Per SDP-178, its job is strictly to **surface** mailbox state and mail-movement primitives to a downstream consumer — it does not decide what an attachment means, whether a message "succeeded," or which folder a message belongs in. Those are SDP-157's decisions (see [Explicit non-scope](#explicit-non-scope-sdp-157) below).

## Where the pieces live

| Piece | Location |
|---|---|
| `IMailSource`, `IMailSender` contracts | `WG.AP.Core\Abstractions\IMailSource.cs`, `IMailSender.cs` |
| DTOs: `MailMessageSummary`, `MailAttachmentSummary`, `MailDestinationFolder`, `MailSendRequest` | `WG.AP.Core\Abstractions\` |
| Graph-backed implementation | `WG.AP.Email\GraphMailboxProcessor.cs` (implements both interfaces) |
| Configuration | `WG.AP.Email\MailboxOptions.cs` |
| Console host / DI wiring | `WG.AP.Processing\Program.cs` |
| Tests | `WG.AP.Tests\Email\FakeGraphHandler.cs`, `GraphMailboxProcessorTests.cs` |

`WG.AP.Core` has no dependency on `Microsoft.Graph`/`Azure.Identity` — the abstractions are plain POCOs/interfaces so a future SDP-157 consumer can depend on the contract without pulling in Graph.

## Authentication

- **App-only (client credentials)**, not delegated/user sign-in — this is meant to run unattended. `Program.cs` builds a `ClientSecretCredential(tenantId, clientId, clientSecret)` and passes it to `GraphServiceClient` with the `.default` scope (`GraphMailboxProcessor.GraphScopes`).
- **Every Graph call sends `Prefer: IdType="ImmutableId"`** (`ApplyImmutableId`, applied per-request). This matters because Graph's default `message.id` changes when a message moves folders, and this adapter moves mail (`MoveMessageAsync`). Without the immutable id, a downstream duplicate-detection key built from the message id would break the moment a message got filed into `Processed`/`Errors`/`NeedsReview`.
- **Entra ID prerequisites** (not code, must exist before this runs against a real mailbox):
  - An app registration with `Mail.ReadWrite` + `Mail.Send` **application** permissions, admin-consented.
  - An application access policy scoping that app to just the test mailbox (never the live AP inbox).

## What each method does

All on `GraphMailboxProcessor`, implementing `IMailSource`/`IMailSender`:

- **`ValidateAuthAsync`** — a smoke test: reads one message id from the inbox to confirm the credential and permissions actually work. Throws if Graph returns nothing.
- **`EnsureFoldersExistAsync`** — resolves the Inbox folder id, lists its child folders, and creates whichever of `Processed`, `Errors`, `NeedsReview` don't already exist. Populates the internal `_folderIds` cache that `MoveMessageAsync` depends on — must be called before any move.
- **`EnumerateInboxAsync`** — pages through the Inbox (50 at a time, following `@odata.nextLink` via `.WithUrl(...)`), yielding a `MailMessageSummary` per message (id, received time, sender address, subject, attachment metadata) as an `IAsyncEnumerable`. For each message with attachments, it makes a follow-up call to list attachment metadata (name/size/content-type) — but never downloads attachment bytes here.
- **`GetMessageAsync`** — fetches a single message by id, independent of which folder it's currently in. This is what makes the "id survives a move" proof possible: fetch a message's id, move it, then re-fetch it by that same id.
- **`GetAttachmentContentAsync`** — fetches one attachment's bytes on demand. Only inline `ContentBytes` (small/typical attachments) are supported today; if Graph returns no inline bytes (large attachment), it logs a warning and returns an empty array rather than guessing at a `$value` stream implementation (see [Known follow-ups](#known-follow-ups)).
- **`MoveMessageAsync`** — moves a message into one of the three destination folders (via `_folderIds`) and returns the post-move id, which — thanks to the immutable-id header — is the same as the pre-move id.
- **`SendMailAsync`** — sends a plain-text notification email via Graph's `sendMail` action. Used today for SDP-178's "send a test notification" proof; intended for SDP-157's failure notifications and later reporting.

## Configuration

`MailboxOptions` (bound from the `Mailbox` config section):

| Field | Purpose |
|---|---|
| `TenantId`, `ClientId`, `ClientSecret` | Entra ID app registration credentials |
| `MailboxUser` | The mailbox to operate on (UPN/email) |
| `IsTestMailbox` | **Must be `true`.** `Program.cs` calls `.ValidateOnStart()` with a validator that fails startup if this isn't explicitly `true` — a hard guard against ever pointing this adapter at the live AP inbox, since it moves mail into subfolders. |

Three `appsettings*.json` files in `WG.AP.Processing`, all starting with empty `TenantId`/`ClientId`/`ClientSecret`/`MailboxUser` placeholders:
- `appsettings.json` (base) — `IsTestMailbox: false`.
- `appsettings.Development.json` — `IsTestMailbox: true` (the only one pre-set to pass validation).
- `appsettings.Production.json` — `IsTestMailbox: false`.

`WG.AP.Processing\Properties\launchSettings.json` sets `DOTNET_ENVIRONMENT=Development` so an IDE debug run (F5) picks up `appsettings.Development.json` — without it, `IHostEnvironment.EnvironmentName` defaults to `Production` and the wrong file loads silently.

## How it runs today

`WG.AP.Processing` is a plain console app, not a long-running service. `Program.cs` does one pass and exits: build the host → validate auth → ensure folders exist → enumerate the inbox, logging a line per message → log a summary count. It's meant to be invoked on a recurring schedule by something external (e.g. Windows Task Scheduler), not run as a Windows Service/`BackgroundService` — each run is independent and crash-safe, and there's no process supervision to own.

Run locally: `dotnet run --project WG.AP.Processing`.

## Testing

`WG.AP.Tests\Email\FakeGraphHandler.cs` is a `HttpMessageHandler` that routes requests by URL/method match, letting tests build a `GraphServiceClient` against a fully faked Graph backend — no real tenant, credentials, or network access needed. `GraphMailboxProcessorTests.cs` covers SDP-178's six done-criteria directly:

1. Enumerate N messages with attachment metadata (`EnumerateInboxAsync_ReturnsMessagesWithAttachmentMetadata`)
2. Fetch an attachment's bytes and confirm the length matches (`GetAttachmentContentAsync_ReturnsBytesMatchingReportedSize`)
3. Move a message, then re-read it by its pre-move id — the immutable-id proof (`MoveMessageAsync_PreservesTheImmutableId_AndTheMessageIsReadableByItAfterTheMove`)
4. Auto-create all three folders against an empty mailbox (`EnsureFoldersExistAsync_CreatesAllThreeFolders_WhenMailboxHasNone`)
5. Send a notification (`SendMailAsync_PostsToTheSendMailEndpoint`)
6. `dotnet test` green — all of the above, run via `dotnet test WG.AP.Tests/WG.AP.Tests.csproj`

## Explicit non-scope (SDP-157)

This adapter deliberately does **not**: save attachments to storage, write ledger rows, detect statements, or decide whether a message goes to `Processed` vs `Errors` vs `NeedsReview`. It touches no database and writes no files. All of that consumption logic belongs to SDP-157, which is meant to depend on `IMailSource`/`IMailSender` rather than on `GraphMailboxProcessor` directly.

## Known follow-ups

- **Large attachments**: `GetAttachmentContentAsync` only handles inline `ContentBytes`. Attachments large enough that Graph omits inline content (streamed via `/attachments/{id}/$value` instead) currently log a warning and return an empty byte array — not yet implemented.
- **`xunit` → `xunit.v3`**: `WG.AP.Tests` still uses `xunit` 2.x, which is deprecated in favor of `xunit.v3` (a different package id and test host, not a version bump) — a separate migration, not done yet.
- **`.github/workflows/unit-tests.yml`**: was deleted from disk outside of any change made while building this adapter; still uncommitted either way (restore or finalize the deletion) as of this writing.
