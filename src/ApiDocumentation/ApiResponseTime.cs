using System.Globalization;

namespace QubeERP.ApiDocumentation;

public static class ApiResponseTime
{
    public const string MalaysiaTimeHeader = "X-Malaysia-Time";

    public static void UseMalaysiaResponseTime(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[MalaysiaTimeHeader] = DateTimeOffset.UtcNow
                    .ToOffset(TimeSpan.FromHours(8)).ToString("O", CultureInfo.InvariantCulture);
                return Task.CompletedTask;
            });
            return next(context);
        });
    }
}
