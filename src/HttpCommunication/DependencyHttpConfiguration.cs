using System.Net;
using Microsoft.Extensions.Options;

namespace QubeERP.HttpCommunication;

internal static class DependencyHttpConfiguration
{
    public static IHttpClientBuilder ConfigureDependency(this IHttpClientBuilder builder, string dependency,
        bool retryProductLookups = false)
    {
        builder.Services.AddOptions<DependencyHttpSettings>(dependency)
            .BindConfiguration($"HttpClients:{dependency}")
            .Validate(settings => settings.RequestTimeoutSeconds is >= 1 and <= 30
                && settings.ConnectTimeoutSeconds is >= 1 and <= 10
                && settings.ConnectTimeoutSeconds <= settings.RequestTimeoutSeconds
                && settings.MaxRetries is >= 0 and <= 3
                && settings.RetryDelayMilliseconds is >= 10 and <= 1000,
                "HTTP timeouts or retry limits are outside the supported bounds.")
            .ValidateOnStart();

        builder.ConfigureHttpClient((services, client) => client.Timeout = TimeSpan.FromSeconds(
            services.GetRequiredService<IOptionsMonitor<DependencyHttpSettings>>().Get(dependency).RequestTimeoutSeconds));
        builder.ConfigurePrimaryHttpMessageHandler(services => new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(services.GetRequiredService<IOptionsMonitor<DependencyHttpSettings>>()
                .Get(dependency).ConnectTimeoutSeconds)
        });
        if (retryProductLookups)
        {
            builder.AddHttpMessageHandler(services => new ProductLookupRetryHandler(
                services.GetRequiredService<IOptionsMonitor<DependencyHttpSettings>>().Get(dependency),
                services.GetRequiredService<ILogger<ProductLookupRetryHandler>>()));
        }
        return builder;
    }

    private sealed class ProductLookupRetryHandler(DependencyHttpSettings settings,
        ILogger<ProductLookupRetryHandler> logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var readOnly = request.Method == HttpMethod.Get && path.StartsWith("/api/products/", StringComparison.Ordinal)
                || request.Method == HttpMethod.Post && path == "/api/products/lookup";
            if (!readOnly || settings.MaxRetries == 0)
                return await base.SendAsync(request, cancellationToken);

            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var copy = CopyRequest(request, body);
                try
                {
                    var response = await base.SendAsync(copy, cancellationToken);
                    if (!IsTransient(response.StatusCode) || attempt == settings.MaxRetries)
                        return response;
                    logger.LogWarning("Product lookup retry {Retry}/{MaxRetries} after HTTP {StatusCode}",
                        attempt + 1, settings.MaxRetries, (int)response.StatusCode);
                    response.Dispose();
                }
                catch (HttpRequestException exception) when (attempt < settings.MaxRetries && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Product lookup retry {Retry}/{MaxRetries} after {FailureType}",
                        attempt + 1, settings.MaxRetries, exception.GetType().Name);
                }
                catch (OperationCanceledException) when (attempt < settings.MaxRetries && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Product lookup retry {Retry}/{MaxRetries} after dependency timeout",
                        attempt + 1, settings.MaxRetries);
                }
                var delay = settings.RetryDelayMilliseconds * (1 << attempt)
                    + Random.Shared.Next(settings.RetryDelayMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

        private static HttpRequestMessage CopyRequest(HttpRequestMessage request, byte[]? body)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers)
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (body is not null)
            {
                copy.Content = new ByteArrayContent(body);
                foreach (var header in request.Content!.Headers)
                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return copy;
        }
    }
}

internal sealed class DependencyHttpSettings
{
    public int ConnectTimeoutSeconds { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 5;
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMilliseconds { get; set; } = 100;
}
