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
// --laptop: simulate a gaming laptop (fans owned by its controller, fan modes instead).
var laptopSim = args.Contains("--laptop");
var simulate = demo || laptopSim || args.Contains("--simulate") || Environment.GetEnvironmentVariable("KELVIN_SIMULATE") == "1" || !elevated;

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
IFanProvider fanHw = simulate ? new SimulatedFanProvider(laptopSim) : new LhmFanProvider();
IRgbProvider rgbHw = simulate ? new SimulatedRgbProvider() : new OpenRgbProvider();
var fans = new FanEngine(fanHw, store);
var lighting = new LightingEngine(rgbHw, store, () => fans.Sensors);
// With real hardware, Kelvin runs OpenRGB itself so lighting works without the user opening it.
var openRgbHost = simulate ? null : new OpenRgbHost();
// Laptop fan modes: the vendor's (HP Omen, ASUS, Lenovo) where available, else Windows power modes.
IFanModeProvider? fanModes = laptopSim ? new SimulatedFanModes() : simulate ? null : FanModeDetector.Detect();
var runtime = new Runtime(fanHw, rgbHw, fans, lighting, store, elevated, openRgbHost, thermal, fanModes, demo);
_ = runtime.Profiles; // set up default profiles before the API takes requests

builder.Services.AddSingleton(runtime);
builder.Services.AddHostedService(_ => runtime);
// The desktop app loads the UI from its own origin, so it's the only cross-origin caller allowed.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(DesktopOrigins.All)
    .WithHeaders("Content-Type", "X-Kelvin")
    .WithMethods("GET", "PUT", "POST", "DELETE")));

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
        clean.SmoothSeconds = Math.Clamp(clean.SmoothSeconds, 0, 120);
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

// Screen sync: the desktop app asks whether it should be capturing, and sends small color grids.
api.MapGet("/lighting/screen", (Runtime rt) => Results.Ok(new { active = rt.ScreenSyncActive }));
api.MapPut("/lighting/screen", (ScreenFrame frame, Runtime rt) =>
{
    if (!rt.ScreenSyncActive) return Results.Ok(new { active = false });
    byte[] rgb;
    try { rgb = Convert.FromBase64String(frame.Data ?? ""); }
    catch (FormatException) { return Results.BadRequest(new { error = "Data must be base64." }); }
    return rt.Lighting.Screen.Submit(frame.Width, frame.Height, rgb, DateTimeOffset.Now)
        ? Results.Ok(new { active = true })
        : Results.BadRequest(new { error = "Width x height x 3 bytes of RGB, at most 64 x 64." });
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

// ---- Profiles
api.MapPut("/profiles/active", (RecommendationRequest req, Runtime rt) =>
{
    try { rt.Profiles.SetManual(req.Id); return Results.Ok(); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
});

api.MapPut("/profiles/automation", (ProfileAutomation body) =>
{
    var ids = store.Read(s => s.Profiles.Select(p => p.Id).ToHashSet());
    if (!ids.Contains(body.DefaultProfileId)) return Results.BadRequest(new { error = "Pick a default profile." });
    if (body.GameProfileId is { } g && !ids.Contains(g)) return Results.BadRequest(new { error = "The game profile doesn't exist." });
    if (body.NightProfileId is { } n && !ids.Contains(n)) return Results.BadRequest(new { error = "The night profile doesn't exist." });
    if (!TimeOnly.TryParse(body.NightStart, out _) || !TimeOnly.TryParse(body.NightEnd, out _))
        return Results.BadRequest(new { error = "Night times must look like 23:00." });
    body.GameProcesses = (body.GameProcesses ?? [])
        .Select(p => p.Trim()).Where(p => p.Length is > 0 and <= 100 && p.IndexOfAny(['/', '\\']) < 0)
        .Select(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
    store.Mutate(s => s.Automation = body);
    return Results.Ok(body);
});

api.MapPost("/profiles", (ProfileRequest req) =>
{
    var name = (req.Name ?? "").Trim();
    if (name.Length is 0 or > 40) return Results.BadRequest(new { error = "Give the profile a name of up to 40 characters." });
    if (store.Read(s => s.Profiles.Count) >= 12) return Results.BadRequest(new { error = "Up to 12 profiles are supported." });
    var id = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    if (id.Length == 0) id = "profile";
    var taken = store.Read(s => s.Profiles.Select(p => p.Id).ToHashSet());
    for (var i = 2; taken.Contains(id); i++) id = $"{id.TrimEnd("0123456789".ToCharArray())}{i}";
    var profile = store.Read(s => new Profile
    {
        Id = id, Name = name, Fans = ProfileEngine.Copy(s.Fans), Lighting = ProfileEngine.Copy(s.Lighting),
        FanMode = s.Profiles.FirstOrDefault(p => p.Id == s.ActiveProfileId)?.FanMode,
    });
    store.Mutate(s => s.Profiles.Add(profile));
    return Results.Ok(new { profile.Id, profile.Name });
});

api.MapPut("/profiles/{id}", (string id, ProfileRequest req, Runtime rt) =>
{
    if (!store.Read(s => s.Profiles.Any(p => p.Id == id))) return Results.NotFound(new { error = "That profile doesn't exist." });
    var name = req.Name?.Trim();
    if (name is { Length: 0 or > 40 }) return Results.BadRequest(new { error = "Profile names can be up to 40 characters." });
    if (req.FanMode is { } m && rt.FanModes?.Modes.Any(x => x.Id == m) != true) return Results.BadRequest(new { error = "This PC doesn't have that fan mode." });
    store.Mutate(s =>
    {
        var p = s.Profiles.Single(x => x.Id == id);
        if (name is not null) p.Name = name;
        if (req.FanMode is not null) p.FanMode = req.FanMode;
    });
    if (req.FanMode is not null && store.Read(s => s.ActiveProfileId) == id) rt.SetFanMode(req.FanMode);
    return Results.Ok();
});

api.MapDelete("/profiles/{id}", (string id) =>
{
    var (count, automation) = store.Read(s => (s.Profiles.Count, s.Automation));
    if (count <= 1) return Results.BadRequest(new { error = "Keep at least one profile." });
    if (automation.DefaultProfileId == id || automation.GameProfileId == id || automation.NightProfileId == id)
        return Results.BadRequest(new { error = "This profile is used by automatic switching. Pick another one there first." });
    store.Mutate(s =>
    {
        s.Profiles.RemoveAll(p => p.Id == id);
        if (s.ActiveProfileId == id) s.ActiveProfileId = s.Automation.DefaultProfileId;
    });
    return Results.Ok();
});

// ---- Home Assistant (MQTT)
api.MapPut("/mqtt", (MqttRequest req) =>
{
    var host = (req.Host ?? "").Trim();
    if (host.Length > 255 || host.Any(char.IsWhiteSpace) || host.Contains('/'))
        return Results.BadRequest(new { error = "Enter just the broker's address, like 192.168.1.20 or homeassistant.local." });
    if (req.Port is < 1 or > 65535) return Results.BadRequest(new { error = "The port must be between 1 and 65535." });
    static bool TopicOk(string? t) => !string.IsNullOrWhiteSpace(t) && t.Length <= 100 && t.IndexOfAny(['#', '+', ' ']) < 0;
    if (!TopicOk(req.BaseTopic) || !TopicOk(req.DiscoveryPrefix))
        return Results.BadRequest(new { error = "Topics can't be empty or contain spaces, # or +." });
    if (req.Enabled && host.Length == 0) return Results.BadRequest(new { error = "Enter your broker's address." });
    store.Mutate(s =>
    {
        var m = s.Mqtt;
        m.Enabled = req.Enabled; m.Host = host; m.Port = req.Port; m.UseTls = req.UseTls;
        m.Username = (req.Username ?? "").Trim();
        m.BaseTopic = req.BaseTopic!.Trim('/'); m.DiscoveryPrefix = req.DiscoveryPrefix!.Trim('/');
        m.AllowProfileControl = req.AllowProfileControl;
        // null keeps the saved password; "" clears it.
        if (req.Password is not null) m.PasswordProtected = HomeAssistantBridge.Protect(req.Password);
    });
    return Results.Ok();
});

// ---- Laptop fan modes (the choice is saved into the active profile)
api.MapPut("/fan-mode", (RecommendationRequest req, Runtime rt) =>
{
    if (rt.FanModes?.Modes.Any(m => m.Id == req.Id) != true) return Results.BadRequest(new { error = "This PC doesn't have that fan mode." });
    try { rt.SetFanMode(req.Id, saveToProfile: true); return Results.Ok(); }
    catch (Exception ex) { return Results.Problem(detail: $"The laptop didn't accept that mode: {ex.Message}", statusCode: 502); }
});

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
record ProfileRequest(string? Name, string? FanMode);
record MqttRequest(bool Enabled, string? Host, int Port, bool UseTls, string? Username, string? Password,
    string? BaseTopic, string? DiscoveryPrefix, bool AllowProfileControl);

static class DesktopOrigins
{
    // Where Tauri serves the bundled UI from, on Windows and other platforms.
    public static readonly string[] All = ["http://tauri.localhost", "https://tauri.localhost", "tauri://localhost"];

    public static bool IsAllowed(string origin, int port) =>
        All.Contains(origin) || origin == $"http://127.0.0.1:{port}" || origin == $"http://localhost:{port}";
}

public sealed record ScreenFrame(int Width, int Height, string? Data);
