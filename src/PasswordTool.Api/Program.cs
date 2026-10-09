using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.EventLog;
using PasswordTool.Api.Contracts;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Hashers;
using PasswordTool.Core.Inspection;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Utilities;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter<EventLogLoggerProvider>(_ => false);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32768);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IPasswordHasherRegistry, PasswordHasherRegistry>();
builder.Services.AddSingleton<IPasswordHashInspector, PasswordHashInspector>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetFixedWindowLimiter("local-api", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddConcurrencyLimiter("kdf", options =>
    {
        options.PermitLimit = 2;
        options.QueueLimit = 0;
    });
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    var host = context.Request.Host.Host;
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress)
        || !(string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var hostAddress) && IPAddress.IsLoopback(hostAddress))))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();

var passwordApi = app.MapGroup("/api/password");

passwordApi.MapPost("/hash", (HashPasswordRequest request, IPasswordHasherRegistry registry) =>
{
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > PasswordHashLimits.MaxPasswordCharacters)
    {
        return Results.BadRequest(new { error = "Password is required and cannot exceed 4096 characters." });
    }

    if (string.IsNullOrWhiteSpace(request.AlgorithmName) || request.AlgorithmName.Length > 64)
    {
        return Results.BadRequest(new { error = "AlgorithmName is required." });
    }

    try
    {
        var hasher = registry.GetHasher(request.AlgorithmName);
        if (!hasher.IsRecommendedForPasswordStorage)
            return Results.BadRequest(new { error = "Educational algorithms cannot be used to create password hashes through this API." });
        var storedHash = hasher.HashPassword(request.Password);

        return Results.Ok(new HashPasswordResponse(hasher.AlgorithmName, storedHash));
    }
    catch (ArgumentException)
    {
        return Results.BadRequest(new { error = "Unsupported algorithm or password." });
    }
}).RequireRateLimiting("kdf");

passwordApi.MapPost("/verify", (VerifyPasswordRequest request, IPasswordHasherRegistry registry) =>
{
    if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > PasswordHashLimits.MaxPasswordCharacters)
    {
        return Results.BadRequest(new { error = "Password is required and cannot exceed 4096 characters." });
    }

    if (string.IsNullOrWhiteSpace(request.StoredHash) || request.StoredHash.Length > PasswordHashLimits.MaxStoredHashCharacters)
    {
        return Results.BadRequest(new { error = "StoredHash is required and cannot exceed 2048 characters." });
    }

    foreach (var descriptor in registry.GetAvailableHashers())
    {
        var hasher = registry.GetHasher(descriptor.AlgorithmName);
        if (hasher.VerifyPassword(request.Password, request.StoredHash))
        {
            return Results.Ok(new VerifyPasswordResponse(true));
        }
    }

    return Results.Ok(new VerifyPasswordResponse(false));
}).RequireRateLimiting("kdf");

passwordApi.MapPost("/inspect", (InspectPasswordHashRequest request, IPasswordHashInspector inspector) =>
{
    if (string.IsNullOrWhiteSpace(request.StoredHash) || request.StoredHash.Length > PasswordHashLimits.MaxStoredHashCharacters)
    {
        return Results.BadRequest(new { error = "StoredHash is required and cannot exceed 2048 characters." });
    }

    return Results.Ok(inspector.Inspect(request.StoredHash));
});

passwordApi.MapGet("/algorithms", (IPasswordHasherRegistry registry) =>
{
    return Results.Ok(registry.GetAvailableHashers());
});

app.Run();
