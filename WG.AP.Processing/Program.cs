using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using WG.AP.Core.Abstractions;
using WG.AP.DataAccess;
using WG.AP.Email;
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
    .Validate(options => options.IsTestMailbox, $"{MailboxOptions.SectionName}:IsTestMailbox must be true — this process moves mail and must never target the live AP inbox.")
    .ValidateOnStart();

builder.Services
    .AddOptions<MailboxSyncStateOptions>()
    .Bind(builder.Configuration.GetSection(MailboxSyncStateOptions.SectionName));

builder.Services
    .AddOptions<FileLoggerOptions>()
    .Bind(builder.Configuration.GetSection(FileLoggerOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Directory), $"{FileLoggerOptions.SectionName}:Directory is required.")
    .Validate(options => options.LogFilesRetentionDays >= 0, $"{FileLoggerOptions.SectionName}:LogFilesRetentionDays must be 0 (disable) or greater.")
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
builder.Services.AddSingleton<IMailboxSyncStateStore, FileMailboxSyncStateStore>();
builder.Services.AddSingleton<MailboxSyncProcessor>();
builder.Services.AddSingleton<APProcessor>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();

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

