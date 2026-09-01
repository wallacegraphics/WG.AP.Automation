using Azure.Identity;
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

var builder = Host.CreateApplicationBuilder(args);

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

builder.Services
    .AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetSection(FileStorageOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.RootDirectory), $"{FileStorageOptions.SectionName}:RootDirectory is required — invoice attachments are kept for 7 years and the database only stores paths relative to it.")
    .ValidateOnStart();

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

