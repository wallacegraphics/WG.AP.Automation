using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Email;
using WG.AP.Invoice.Abstractions;
using WG.AP.Invoice.AI;
using WG.AP.Processor;
using WG.AP.Processor.Logging;

// The content root is what the appsettings providers resolve their paths against, and it defaults
// to the current working directory - which is wrong for both ways this app actually runs. A CLI
// `dotnet run` from the repo root reads no appsettings file at all, and a scheduled task with no
// "Start in" field set runs from C:\Windows\System32. Neither looks like a path problem: both
// surface as "required config is missing" on a process nobody is watching, for values that are
// sitting correctly in a file that was never opened. The appsettings files are copied next to the
// executable, so anchor there rather than to wherever the process happened to be launched from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Developer-local overrides, layered last so they win. The dev connection string carries a SQL
// login and password, and every appsettings*.json is tracked by git, so the secret-bearing values
// live here instead. Optional and gitignored - absent on CI and on the server, where configuration
// arrives by other means.
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Development.local.json", optional: true, reloadOnChange: false);
}

builder.Services
    .AddOptions<MailboxOptions>()
    .Bind(builder.Configuration.GetSection(MailboxOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.TenantId), $"{MailboxOptions.SectionName}:TenantId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ClientId), $"{MailboxOptions.SectionName}:ClientId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ClientSecret), $"{MailboxOptions.SectionName}:ClientSecret is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.MailboxUser), $"{MailboxOptions.SectionName}:MailboxUser is required.")
    // An empty mailbox id would key every row and the delta cursor on Guid.Empty, which works right
    // up until a second mailbox is added and the two silently share a namespace.
    .Validate(options => options.MailboxId != Guid.Empty, $"{MailboxOptions.SectionName}:MailboxId is required — the mailbox's Entra object id.")
    .Validate(options => options.IsTestMailbox, $"{MailboxOptions.SectionName}:IsTestMailbox must be true — this process moves mail and must never target the live AP inbox.")
    .ValidateOnStart();

builder.Services
    .AddOptions<MailboxSyncStateOptions>()
    .Bind(builder.Configuration.GetSection(MailboxSyncStateOptions.SectionName));

builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), $"{DatabaseOptions.SectionName}:ConnectionString is required.")
    .Validate(options => options.MaxAttempts >= 1, $"{DatabaseOptions.SectionName}:MaxAttempts must be 1 or greater.")
    .ValidateOnStart();

// FileStorageOptionsValidator replaces the usual inline .Validate() lambda: "not empty" is not the
// guarantee that matters for a UNC share. It checks the root exists and is genuinely writable, which
// needs real I/O and distinct messages per failure, so it lives beside the options class instead.
builder.Services
    .AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetSection(FileStorageOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<FileStorageOptions>, FileStorageOptionsValidator>();

builder.Services
    .AddOptions<SqlLoggerOptions>()
    .Bind(builder.Configuration.GetSection(SqlLoggerOptions.SectionName));

builder.Services
    .AddOptions<FileLoggerOptions>()
    .Bind(builder.Configuration.GetSection(FileLoggerOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), $"{FileLoggerOptions.SectionName}:Directory is required.")
    .Validate(options => options.LogFilesRetentionDays >= 0, $"{FileLoggerOptions.SectionName}:LogFilesRetentionDays must be 0 (disable) or greater.")
    .ValidateOnStart();

builder.Services
    .AddOptions<OllamaOptions>()
    .Bind(builder.Configuration.GetSection(OllamaOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), $"{OllamaOptions.SectionName}:BaseUrl is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Model), $"{OllamaOptions.SectionName}:Model is required.")
    .ValidateOnStart();

builder.Services.AddSingleton(serviceProvider =>
{
    var mailboxOptions = serviceProvider.GetRequiredService<IOptions<MailboxOptions>>().Value;
    var credential = new ClientSecretCredential(mailboxOptions.TenantId, mailboxOptions.ClientId, mailboxOptions.ClientSecret);
    return new GraphServiceClient(credential, GraphMailboxProcessor.GraphScopes);
});

builder.Services.AddSingleton<GraphMailboxProcessor>();
builder.Services.AddSingleton<IMailSource>(serviceProvider => serviceProvider.GetRequiredService<GraphMailboxProcessor>());
builder.Services.AddSingleton<IMailSender>(serviceProvider => serviceProvider.GetRequiredService<GraphMailboxProcessor>());
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<ProcessingRunRepository>();
builder.Services.AddSingleton<MailMessageRepository>();
builder.Services.AddSingleton<MailAttachmentRepository>();
builder.Services.AddSingleton<InvoiceRepository>();
builder.Services.AddSingleton<ClientRepository>();
builder.Services.AddSingleton<ExtractionPromptRepository>();
builder.Services.AddSingleton<ApplicationLogRepository>();
builder.Services.AddSingleton<AttachmentFileStore>();

// Both stores are registered concretely so the dual store can hold each. The database is
// authoritative; the file is a safety net for the first week after cutover, because a delta link that
// silently fails to save means a full inbox re-delivery on every run until someone notices. Once the
// two have agreed for a week, drop DualMailboxSyncStateStore, FileMailboxSyncStateStore and
// MailboxSyncStateOptions, and register SqlMailboxSyncStateStore directly.
builder.Services.AddSingleton<SqlMailboxSyncStateStore>();
builder.Services.AddSingleton<FileMailboxSyncStateStore>();
builder.Services.AddSingleton<IMailboxSyncStateStore, DualMailboxSyncStateStore>();
builder.Services.AddSingleton<MailboxSyncProcessor>();

builder.Services.AddHttpClient<OllamaClient>((serviceProvider, httpClient) =>
{
    var ollamaOptions = serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value;
    httpClient.BaseAddress = new Uri(ollamaOptions.BaseUrl);
    httpClient.Timeout = TimeSpan.FromSeconds(ollamaOptions.TimeoutSeconds);
});
builder.Services.AddSingleton<IInvoiceFieldExtractor, PdfInvoiceFieldExtractor>();

builder.Services.AddSingleton<APProcessor>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Three sinks, and the file one is not redundant: it is the only one that still works when the
// database is what is broken, and the only one that cannot vanish with a rolled-back transaction.
// The database sink defaults to Warning while the file stays at Information, so the file keeps the
// full narrative of a single run and the database keeps what is worth querying across runs.
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
builder.Services.AddSingleton<ILoggerProvider, SqlLoggerProvider>();

// Everything from here on can fail before APProcessor ever gets a chance to run its own
// try/catch (e.g. required Mailbox config missing throws OptionsValidationException the moment
// IOptions<MailboxOptions>.Value is touched, whether that's here or during DI construction of
// GraphMailboxProcessor/MailboxSyncProcessor/APProcessor below) — this must never surface as an
// unhandled exception, since nothing is watching the console for a scheduled-task run.
ILogger? logger = null;

try
{
    using var host = builder.Build();
    logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WG.AP.Processing");

    // This looks redundant next to the .ValidateOnStart() calls above, and is not: ValidateOnStart
    // registers a hosted service that runs the validators from host.StartAsync(), and this host is
    // never started - it resolves APProcessor and awaits it directly. Without this line only
    // MailboxOptions is ever checked, and only by accident, because the Development log block below
    // reads its .Value. Everything else validated lazily on first resolution, so a typo in
    // Database:ConnectionString surfaced only after auth, folder creation and a delta fetch had
    // already happened. IStartupValidator is the service ValidateOnStart registers, so calling it
    // here covers all seven options types and any added later, with no list to keep in sync.
    // Placed after the logger exists so a failure is written to the file and database sinks.
    host.Services.GetRequiredService<IStartupValidator>().Validate();

    var environment = host.Services.GetRequiredService<IHostEnvironment>();
    var mailboxOptions = host.Services.GetRequiredService<IOptions<MailboxOptions>>().Value;

    if (environment.IsDevelopment())
    {
        logger.LogInformation(
            "Mailbox config check (Development): Section={Section}, TenantIdSet={TenantIdSet}, ClientIdSet={ClientIdSet}, ClientSecretSet={ClientSecretSet}, MailboxUserSet={MailboxUserSet}, IsTestMailbox={IsTestMailbox}.",
            MailboxOptions.SectionName,
            !string.IsNullOrWhiteSpace(mailboxOptions.TenantId),
            !string.IsNullOrWhiteSpace(mailboxOptions.ClientId),
            !string.IsNullOrWhiteSpace(mailboxOptions.ClientSecret),
            !string.IsNullOrWhiteSpace(mailboxOptions.MailboxUser),
            mailboxOptions.IsTestMailbox);

        // The mailbox id is logged in full, unlike the secrets above: it is not sensitive, and it is
        // the key every stored row and the delta cursor hang off - so if it is ever wrong, seeing the
        // actual value is what makes that discoverable.
        logger.LogInformation("Mailbox id in use: {MailboxId} ({MailboxUser}).", mailboxOptions.MailboxId, mailboxOptions.MailboxUser);
    }

    var apProcessor = host.Services.GetRequiredService<APProcessor>();
    await apProcessor.ProcessInvoicesAsync(CancellationToken.None);
}
catch (Exception exception)
{
    if (logger is not null)
    {
        logger.LogError(exception, "WG.AP.Processing failed to start.");
    }
    else
    {
        // The logger itself couldn't be created — this is the only path in the app with no log
        // sink available yet, so it's the one place a plain console write is the correct fallback.
        Console.Error.WriteLine($"WG.AP.Processing failed to start before logging was available: {exception}");
    }

    Environment.ExitCode = 1;
}

