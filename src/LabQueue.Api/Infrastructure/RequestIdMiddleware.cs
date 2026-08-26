using Serilog.Context;

namespace LabQueue.Api.Infrastructure;

/// <summary>
/// Gives every request a stable id, echoes it back on the response, and pushes it
/// onto the Serilog log context so an error response can be traced to its log lines.
/// An inbound X-Request-Id is honoured so an id assigned upstream survives.
/// </summary>
public sealed class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = context.TraceIdentifier;
        }

        context.Items[HeaderName] = requestId;
        context.Response.Headers[HeaderName] = requestId;

        using (LogContext.PushProperty("RequestId", requestId))
        {
            await next(context);
        }
    }
}

public static class RequestIdMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestId(this IApplicationBuilder app)
        => app.UseMiddleware<RequestIdMiddleware>();

    public static string RequestId(this HttpContext context)
        => context.Items[RequestIdMiddleware.HeaderName] as string ?? context.TraceIdentifier;
}
