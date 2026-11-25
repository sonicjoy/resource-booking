using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
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

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ResourceBooking.Client._Imports).Assembly);

app.Run();

public partial class Program;
