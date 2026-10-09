using AIAgentDemo.Core;
using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Web.Components;
using AIAgentDemo.Web.Services;
using Microsoft.AspNetCore.Localization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// Multi-agent pipeline (agents, tools, cost tracking, SQLite store) + the human-in-the-loop gateway.
builder.Services.AddSingleton<PendingApprovals>();
builder.Services.AddSingleton<IApprovalGateway>(services => services.GetRequiredService<PendingApprovals>());
builder.Services.AddAgentDesk(builder.Configuration);
builder.Services.PostConfigure<StorageOptions>(storage =>
    storage.DataDirectory = Path.Combine(builder.Environment.ContentRootPath, storage.DataDirectory));

// Agent, chat and tool spans plus token/cost metrics, exported when an OTLP endpoint is set
// (e.g. the .NET Aspire dashboard: OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing.AddSource(Telemetry.SourceName).AddOtlpExporter())
        .WithMetrics(metrics => metrics.AddMeter(Telemetry.SourceName).AddOtlpExporter());
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

string[] cultures = ["en", "es"];
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(cultures[0])
    .AddSupportedCultures(cultures)
    .AddSupportedUICultures(cultures));

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Language switch: stores the culture cookie, then returns to the same page (local URLs only).
app.MapGet("/culture/{culture}", (string culture, string? returnUrl, HttpContext http) =>
{
    if (cultures.Contains(culture))
    {
        http.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
    }

    var isLocal = returnUrl is ['/', ..] && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal);
    return Results.LocalRedirect(isLocal ? returnUrl! : "/");
});

await app.Services.InitializeAgentDeskAsync();
app.Run();
