using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using ResourceBooking.Auth;
using ResourceBooking.Components;
using ResourceBooking.Data;
using ResourceBooking.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var companyZone = TimeZoneInfo.FindSystemTimeZoneById(builder.Configuration["Booking:TimeZone"] ?? "Europe/London");
builder.Services.AddSingleton(companyZone);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddDbContext<AppDb>(options =>
    AppDb.Configure(options, builder.Configuration.GetConnectionString("Db")));

builder.Services.AddAppAuthentication(builder.Environment);
builder.Services.AddApiGuards();
builder.Services.AddProblemDetails();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Safe while the app runs as a single replica (see the design's Risks section).
await using (var scope = app.Services.CreateAsyncScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();
    await Seed.SeedIfEmptyAsync(db, services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<TimeZoneInfo>(), CancellationToken.None);
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Pages get the not-found page; API calls keep their own status code.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), pages =>
    pages.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthEndpoints();

// Every /api endpoint requires a signed-in user unless it opts out with AllowAnonymous,
// and refuses a state-changing request from another site.
var api = app.MapGroup("/api").RequireAuthorization().RequireSameOrigin();
api.MapAuthEndpoints();
api.MapAdminEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ResourceBooking.Client._Imports).Assembly);

app.Run();

public partial class Program;
