using System.Net.WebSockets;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kelvin.Core;
using Kelvin.Core.Simulated;
using Kelvin.Hardware;
using Kelvin.Service;

// --port N runs on another port (e.g. a demo next to the installed service). The desktop app
// always talks to the default port.
var portArg = Array.IndexOf(args, "--port");
var Port = portArg >= 0 && portArg + 1 < args.Length && int.TryParse(args[portArg + 1], out var parsedPort) ? parsedPort : 5577;

var elevated = OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
// --demo: simulated hardware with no conflict scanning or "simulated" notice, for screenshots.
var demo = args.Contains("--demo");
var simulate = demo || args.Contains("--simulate") || Environment.GetEnvironmentVariable("KELVIN_SIMULATE") == "1" || !elevated;

// Installed, Kelvin runs as a Windows service under LocalSystem, which gives it the rights that fan
// control needs. Its working directory is then System32, so content comes from the exe's folder.
var asService = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = asService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "KelvinService");
// Stopping must be quick: installers and Windows wait on it, and fans are handed back to the BIOS
// as part of it. Live connections close the moment shutdown starts, so this is only a backstop.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
// Loopback only. Kelvin controls hardware, so it is never reachable from the network.
builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

var settingsPath = Environment.GetEnvironmentVariable("KELVIN_SETTINGS")
    ?? (asService ? SettingsStore.MachinePath() : SettingsStore.DefaultPath());
var store = new SettingsStore(settingsPath);
// Demo mode never touches real history: it starts from five weeks of made-up data instead.
var thermal = new ThermalHistory(demo ? null : ThermalHistory.PathNextTo(settingsPath));
if (demo) thermal.SeedDemo();
IFanProvider fanHw = simulate ? new SimulatedFanProvider() : new LhmFanProvider();
IRgbProvider rgbHw = simulate ? new SimulatedRgbProvider() : new OpenRgbProvider();
var fans = new FanEngine(fanHw, store);
var lighting = new LightingEngine(rgbHw, store, () => fans.Sensors);
// With real hardware, Kelvin runs OpenRGB itself so lighting works without the user opening it.
var openRgbHost = simulate ? null : new OpenRgbHost();
var runtime = new Runtime(fanHw, rgbHw, fans, lighting, store, elevated, openRgbHost, thermal, demo);

builder.Services.AddSingleton(runtime);
builder.Services.AddHostedService(_ => runtime);
// The desktop app loads the UI from its own origin, so it's the only cross-origin caller allowed.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(DesktopOrigins.All)
    .WithHeaders("Content-Type", "X-Kelvin")
    .WithMethods("GET", "PUT", "POST")));

var app = builder.Build();
app.UseWebSockets();
app.UseCors();

// Browsers let any website send requests to localhost. Reject foreign hosts (DNS rebinding) and
// require a custom header on writes, which a cross-site page can't add without a CORS preflight
// that Kelvin never approves.
app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) &&
        !HttpMethods.IsOptions(ctx.Request.Method) && ctx.Request.Headers["X-Kelvin"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/state", (Runtime rt) => rt.Snapshot());
api.MapGet("/report", (Runtime rt) => rt.Report());

api.Map("/live", async (HttpContext ctx, Runtime rt, IHostApplicationLifetime life) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    // WebSockets ignore CORS, so check the page that opened the socket ourselves.
    var origin = ctx.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !DesktopOrigins.IsAllowed(origin, Port)) { ctx.Response.StatusCode = 403; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var json = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
    // End when the client goes away or the service starts shutting down, whichever comes first.
    using var done = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, life.ApplicationStopping);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    try
    {
        while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(done.Token))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(rt.Snapshot(), json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, done.Token);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    if (ws.State == WebSocketState.Open)
    {
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Kelvin is stopping", closeTimeout.Token); }
        catch { /* the app reconnects on its own */ }
    }
});

api.MapPut("/fans", (FanSettings body, Runtime rt) =>
{
    if (!rt.Fans.Fans.Any(f => f.Id == body.FanId && f.Controllable)) return Results.NotFound(new { error = "That fan isn't controllable." });
    try
    {
        var clean = body.Clone();
        clean.FixedPercent = Math.Clamp(clean.FixedPercent, 0, 100);
        clean.MinPercent = Math.Clamp(clean.MinPercent, 0, 60);
        clean.ResponseSeconds = Math.Clamp(clean.ResponseSeconds, 1, 30);
        if (clean.Mode == FanMode.Curve) clean.Points = Curves.Sanitize(clean.Points);
        store.Mutate(s => { s.Fans.RemoveAll(f => f.FanId == clean.FanId); s.Fans.Add(clean); });
        return Results.Ok(clean);
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/fans/calibrate", (Runtime rt) => rt.StartCalibration()
    ? Results.Accepted()
    : Results.Conflict(new { error = "Calibration is already running." }));

api.MapPut("/lighting", (LightingSettings body, Runtime rt) =>
{
    try
    {
        RgbColor.FromHex(body.Color);
        body.Devices ??= new();
        foreach (var d in body.Devices.Values)
        {
            d.Zones ??= new();
            foreach (var o in d.Zones.Values.Prepend(d)) { RgbColor.FromHex(o.Color); RgbColor.FromHex(o.Color2); }
        }
        // Keep the file tidy: drop entries that just follow the effect.
        foreach (var d in body.Devices.Values)
            foreach (var z in d.Zones.Where(z => z.Value.Mode == ColorMode.Follow).Select(z => z.Key).ToList()) d.Zones.Remove(z);
        foreach (var id in body.Devices.Where(d => d.Value.Mode == ColorMode.Follow && d.Value.Zones.Count == 0).Select(d => d.Key).ToList())
            body.Devices.Remove(id);
    }
    catch (FormatException ex) { return Results.BadRequest(new { error = ex.Message }); }
    body.Brightness = Math.Clamp(body.Brightness, 0, 1);
    body.Speed = Math.Clamp(body.Speed, 0, 1);
    store.Mutate(s => s.Lighting = body);
    rt.Lighting.Invalidate();
    return Results.Ok(body);
});

api.MapPut("/safety", (SafetySettings body) =>
{
    body.CriticalCpuTemp = Math.Clamp(body.CriticalCpuTemp, 70, 95);
    body.CriticalGpuTemp = Math.Clamp(body.CriticalGpuTemp, 70, 100);
    store.Mutate(s => s.Safety = body);
    return Results.Ok(body);
});

api.MapPut("/alerts", (AlertSettings body) =>
{
    body.Rules ??= [];
    if (body.Rules.Count > 10) return Results.BadRequest(new { error = "Up to 10 alerts are supported." });
    foreach (var r in body.Rules)
    {
        if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 32) r.Id = Guid.NewGuid().ToString("N")[..8];
        if (!double.IsFinite(r.Threshold) || r.Threshold is < 40 or > 110)
            return Results.BadRequest(new { error = "Alert temperatures must be between 40 and 110°C." });
        r.SustainSeconds = double.IsFinite(r.SustainSeconds) ? Math.Clamp(Math.Round(r.SustainSeconds), 0, 600) : 10;
    }
    store.Mutate(s => s.Alerts = body);
    return Results.Ok(body);
});

api.MapPost("/alerts/test", (Runtime rt) => Results.Ok(rt.Alerts.Test()));

api.MapPost("/recommendations/apply",(RecommendationRequest req, Runtime rt) => rt.Apply(req.Id) ? Results.Ok() : Results.NotFound());
api.MapPost("/conflicts/close", (RecommendationRequest req, Runtime rt) =>
{
    var result = rt.CloseConflict(req.Id);
    if (result is null) return Results.NotFound(new { error = "Kelvin doesn't know that app." });
    return result.Closed ? Results.Ok(result) : Results.UnprocessableEntity(new { error = result.Message });
});

api.MapPost("/recommendations/apply-all", (Runtime rt) => Results.Ok(new { applied = rt.ApplyAll() }));
api.MapPost("/recommendations/dismiss", (RecommendationRequest req) =>
{
    store.Mutate(s => { if (!s.DismissedRecommendations.Contains(req.Id)) s.DismissedRecommendations.Add(req.Id); });
    return Results.Ok();
});

// Lets the desktop app quit the service gracefully, which runs the shutdown path that hands
// every fan back to the BIOS.
api.MapPost("/shutdown", (IHostApplicationLifetime life) =>
{
    life.StopApplication();
    return Results.Accepted();
});

// Last line of defence: if the process dies unexpectedly, hand fans back to the BIOS.
AppDomain.CurrentDomain.ProcessExit += (_, _) => fans.RestoreAll();
AppDomain.CurrentDomain.UnhandledException += (_, _) => fans.RestoreAll();

app.Logger.LogInformation("Kelvin is running at http://127.0.0.1:{Port} ({Mode})", Port, simulate ? "simulated hardware" : "real hardware");
app.Run();

record RecommendationRequest(string Id);

static class DesktopOrigins
{
    // Where Tauri serves the bundled UI from, on Windows and other platforms.
    public static readonly string[] All = ["http://tauri.localhost", "https://tauri.localhost", "tauri://localhost"];

    public static bool IsAllowed(string origin, int port) =>
        All.Contains(origin) || origin == $"http://127.0.0.1:{port}" || origin == $"http://localhost:{port}";
}
