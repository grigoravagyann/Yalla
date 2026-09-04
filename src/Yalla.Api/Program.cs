using Serilog;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Configuration sources: the secret-free appsettings.json placeholder, then the environment's own
// file, then environment variables - which is how real values (connection strings, keys) arrive
// in a deployed environment.
var configuration = builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

// Logging is configured entirely from the Serilog section, so sinks and levels change per
// environment without a redeploy.
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.AddServices(configuration);
builder.AddYallaCors();

var app = builder.Build();

// Enriches log events with TraceId and RequestPath. Does NOT catch or log exceptions -
// UnifiedExceptionHandler is the single log point.
app.UseMiddleware<ErrorContextMiddleware>();

// The unified handler owns every environment. No developer exception page anywhere: it preempts
// the handler and leaks stack traces in non-local environments.
app.UseExceptionHandler();

// Development and Staging only.
app.UseYallaSwagger();

if (!app.Environment.IsDevelopment() && !app.Environment.IsStaging())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseYallaCors();

app.UseRouting();

// Production only by default; RateLimiting:Enabled overrides either way.
app.UseYallaRateLimiting();

// No controllers yet - the reservation, ordering and floor-plan APIs are later tasks. The MVC
// pipeline is wired so they only have to be added.
app.MapControllers();

app.MapYallaHealthChecks();

app.Run();
