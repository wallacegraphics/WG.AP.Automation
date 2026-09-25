using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WG.AP.Integrations.Pace.Generated;

namespace WG.AP.Integrations.Pace;

public static class PaceServiceCollectionExtensions
{
    internal const string HttpClientName = "Pace";
    private const string PaceRestServicesPath = "/rpc/rest/services";

    public static IServiceCollection AddPaceIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PaceOptions>()
            .Bind(configuration.GetSection(PaceOptions.SectionName))
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps), $"{PaceOptions.SectionName}:BaseUrl must be an absolute HTTP(S) URL.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.UserName), $"{PaceOptions.SectionName}:UserName is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Password), $"{PaceOptions.SectionName}:Password is required.")
            .Validate(options => options.TimeoutSeconds > 0, $"{PaceOptions.SectionName}:TimeoutSeconds must be greater than 0.")
            .Validate(options => options.MaxAttempts >= 1, $"{PaceOptions.SectionName}:MaxAttempts must be 1 or greater.")
            .ValidateOnStart();

        services.AddTransient<PaceBasicAuthHandler>();
        services.AddTransient<PaceHttpLoggingHandler>();

        services.AddHttpClient(HttpClientName, (serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<PaceOptions>>().Value;
                httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            // The framework's default logging handlers only ever print the bare numeric status code
            // with no indication of what it means - removed here so PaceHttpLoggingHandler (which
            // reproduces the same four log lines, enriched with the status description) is the only
            // one logging this client's requests, rather than both firing and doubling every line.
            .RemoveAllLoggers()
            .AddHttpMessageHandler<PaceHttpLoggingHandler>()
            .AddHttpMessageHandler<PaceBasicAuthHandler>();

        services.AddTransient<IPaceClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<PaceOptions>>().Value;
            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

            return new PaceClient(httpClientFactory.CreateClient(HttpClientName))
            {
                BaseUrl = BuildPaceServiceBaseUrl(options.BaseUrl),
                // Default (false) reads the response as a stream and, on a JsonException, throws
                // ApiException with Response = string.Empty - discarding whatever Pace actually sent.
                // True reads it as a string first, so a deserialization failure still preserves the
                // real body for logging/alerting instead of an empty "Response: " that can't be diagnosed.
                ReadResponseAsString = true
            };
        });

        services.AddTransient<PaceBillBatchResolver>();
        services.AddTransient<IPaceInvoiceService, PaceInvoiceService>();

        return services;
    }

    internal static string BuildPaceServiceBaseUrl(string baseUrl)
    {
        var uri = new Uri(baseUrl, UriKind.Absolute);

        if (uri.AbsolutePath.TrimEnd('/').EndsWith(PaceRestServicesPath, StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString().TrimEnd('/');
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/') + PaceRestServicesPath,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString().TrimEnd('/');
    }
}
