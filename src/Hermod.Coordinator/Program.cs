using Hermod.Coordinator.Components;
using Hermod.Coordinator.Configuration;
using Hermod.Coordinator.Services;
using Hermod.Core.Configuration;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi.Models;
using Vault42.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Persist Data Protection keys on disk so antiforgery tokens survive restarts.
var keysPath = Path.Combine(AppContext.BaseDirectory, "data", "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("Hermod");

// Per-request LocalApi client; base URI set in Routes.razor / App.razor.
builder.Services.AddHttpClient("LocalApi");
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("LocalApi"));

builder.Services.AddHermodInfrastructure(builder.Configuration);

// Bound once up here so early auth setup can read typed sections
// (AuthSettings.AdminRole / SuperAdminRole feed into Policies.Register).
// Re-used further down for ProtocolTranslators, CORS, Dev gates, etc.
var hermodSettings = builder.Configuration.GetSection("Hermod").Get<HermodSettings>() ?? new HermodSettings();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.WriteIndented = true;
    });

// Hermod:Security:AuthBypass=true — test/dev profiles only. Every
// request authenticates as admin+operator+viewer with no token. Skips
// the vault42 cold-start JWKS fetch entirely so the Coord runs without
// a vault42 deployment in the namespace. NEVER set in prod.
var authBypass = builder.Configuration.GetValue("Hermod:Security:AuthBypass", false);
var vaultAuthority = builder.Configuration["Vault42:Authority"] ?? "http://localhost:8080";

// Defense-in-depth: refuse to start if AuthBypass leaks into a Production
// runtime. Any image where ASPNETCORE_ENVIRONMENT=Production must reach
// auth via the real Vault42 path; CI / copy-paste / env-var pollution can
// no longer silently disable authentication.
if (authBypass && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "Hermod:Security:AuthBypass is enabled but ASPNETCORE_ENVIRONMENT=Production. " +
        "AuthBypass is a test-only feature and must never be active in a Production runtime. " +
        "Either unset Hermod:Security:AuthBypass or change ASPNETCORE_ENVIRONMENT.");
}

if (authBypass)
{
    builder.Services.AddAuthentication(Hermod.Coordinator.Authorization.AuthBypassHandler.SchemeName)
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, Hermod.Coordinator.Authorization.AuthBypassHandler>(
            Hermod.Coordinator.Authorization.AuthBypassHandler.SchemeName, _ => { });
}
else
{
    var vaultRequireHttps = builder.Configuration.GetValue("Hermod:Security:VaultRequireHttps", true);
    builder.Services.AddAuthentication(VaultDefaults.AuthenticationScheme)
        .AddVault(options =>
        {
            options.Authority = vaultAuthority;
            options.RequireHttpsMetadata = vaultRequireHttps;
            options.ValidateFingerprint = false;
        });
}

// Vault API HttpClient for login/refresh/logout proxying.
// ConfigurePrimaryHttpMessageHandler wires the internal-CA pinned +
// client-cert handler when Hermod:Security:InternalCAPath/ClientCertPath/
// ClientKeyPath are all set (prod overlays). Falls back to the default
// HttpClientHandler in dev compose / kind. A partial config — only some
// of the three paths set, or a path pointing at a nonexistent file —
// throws inside the factory so a typo in the prod overlay can never
// silently downgrade outbound TLS.
builder.Services.AddHttpClient("Vault42Api", client =>
{
    client.BaseAddress = new Uri(vaultAuthority.TrimEnd('/'));
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.ConfigurePrimaryHttpMessageHandler(sp =>
    Hermod.Coordinator.Configuration.InternalTlsHandlerFactory.TryBuild(
        hermodSettings.Security,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Hermod.Coordinator.InternalTls"))
    ?? new HttpClientHandler());

// Deny-by-default: endpoints without [AllowAnonymous] require auth.
// Three RBAC policies (viewer ⊂ operator ⊂ admin) registered alongside.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    Hermod.Coordinator.Authorization.Policies.Register(options, hermodSettings.Auth);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(ConfigureSwagger);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// HSTS: 1-year max-age. Preload + IncludeSubDomains intentionally OFF:
// with our internal-CA self-signed cert, Chrome would refuse to render
// any page (cert warning becomes non-bypassable when Preload is set).
// Real prod CA-signed deploy can flip both back on via
// Hermod__Hsts__Preload env override later.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = false;
    options.Preload = false;
});

// Kestrel: TLS 1.3 only + cert hot-reload. The ServerCertificateSelector
// runs on every TLS handshake; we re-read the cert file when its mtime
// changes, so a Secret rotation lands on the next connection without
// any pod restart. New connections see the new cert; existing TLS
// sessions hold the old one until they reconnect (TLS resumption is
// invalidated naturally by the cipher suite refresh).
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    // Don't advertise "Server: Kestrel" — version-fingerprint reduction.
    o.AddServerHeader = false;
    // Bound the request body so a single 10 GB POST can't OOM the pod.
    // Rules + device states are tiny KB-scale payloads; 4 MiB is plenty.
    o.Limits.MaxRequestBodySize = 4 * 1024 * 1024;

    var certPath = builder.Configuration["Kestrel:Certificates:Default:Path"]
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
    var keyPath = builder.Configuration["Kestrel:Certificates:Default:KeyPath"]
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__KeyPath");
    if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(keyPath))
    {
        return;
    }
    var lock_ = new object();
    DateTime lastMtime = DateTime.MinValue;
    System.Security.Cryptography.X509Certificates.X509Certificate2? cached = null;
    o.ConfigureHttpsDefaults(h =>
    {
        // Hardened: TLS 1.3 only — no downgrade to 1.2 on this listener.
        // CA5398 flags hardcoded SslProtocols as a forward-compat smell; in
        // a hardened deploy the floor is the point. Suppression is local.
#pragma warning disable CA5398
        h.SslProtocols = System.Security.Authentication.SslProtocols.Tls13;
#pragma warning restore CA5398
        h.ServerCertificateSelector = (_, _) =>
        {
            var mtime = File.GetLastWriteTimeUtc(certPath);
            lock (lock_)
            {
                if (cached is null || mtime > lastMtime)
                {
                    cached = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    lastMtime = mtime;
                }
                return cached;
            }
        };
    });
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuthenticationStateProvider, Vault42AuthStateProvider>();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHealthChecks();

var (proxyRoutes, proxyClusters) = TranslatorProxyRegistration.Build(hermodSettings.ProtocolTranslators);
// Replace the default IForwarderHttpClientFactory with one that pins
// internal-CA + presents the coord client cert (when configured).
// AddReverseProxy()'s default registration is added via TryAddSingleton,
// so a prior AddSingleton wins.
builder.Services.AddSingleton<Yarp.ReverseProxy.Forwarder.IForwarderHttpClientFactory,
    Hermod.Coordinator.Configuration.InternalTlsForwarderHttpClientFactory>();
builder.Services.AddReverseProxy().LoadFromMemory(proxyRoutes, proxyClusters);

// AllowAnyOrigin+AllowAnyHeader would whitelist the Authorization
// header from any site — allowlist Hermod:Security:AllowedCorsOrigins.
var corsOrigins = hermodSettings.Security.AllowedCorsOrigins;

builder.Services.AddCors(options =>
{
    options.AddPolicy("HermodUi", policy => policy
        .WithOrigins(corsOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

// Fetch the initial Vault JWKS so the first request doesn't pay a cold-start hit.
// Skipped when AuthBypass=true: no vault42 deployment, JWKS fetch would hang.
if (!authBypass)
{
    await app.Services.UseVaultAuthenticationAsync();
}

// Swagger doubles as the primary diagnostic surface; gate it on Dev:Endpoints
// so prod-like profiles can turn it off without needing ASPNETCORE_ENVIRONMENT.
if (app.Environment.IsDevelopment() && hermodSettings.Dev.Endpoints)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Hermod API v1");
        c.RoutePrefix = "api-docs";
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Only enable HTTPS redirection in production where an HTTPS port is set.
if (!app.Environment.IsDevelopment() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORT")))
{
    app.UseHttpsRedirection();
}

app.UseCors("HermodUi");

// Cookie → bearer translator. Browser sessions hold the JWT in the
// HttpOnly hermod_token cookie (set by AuthProxyController on login);
// the JwtBearer / Vault42 scheme only inspects Authorization headers.
// This middleware copies the cookie value into the header so [Authorize]
// endpoints accept the cookie-only browser request without exposing the
// token to JS. Runs BEFORE UseAuthentication so the scheme sees it.
app.Use(async (context, next) =>
{
    if (string.IsNullOrEmpty(context.Request.Headers.Authorization))
    {
        var token = context.Request.Cookies["hermod_token"];
        if (!string.IsNullOrEmpty(token))
        {
            context.Request.Headers.Authorization = $"Bearer {token}";
        }
    }
    await next();
});

// When Dev:Endpoints is off, 404 /mock/** before Blazor routing resolves.
if (!hermodSettings.Dev.Endpoints)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mock", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 404;
            return;
        }
        await next();
    });
}

// CSRF gate for cookie-authenticated /api/* mutations. A browser CANNOT
// forge Origin/Referer cross-origin (the headers are set by the browser
// itself on every fetch), so requiring them to match Request.Host blocks
// the classic "evil.example posts to bank.com with the user's cookies"
// pattern without forcing every endpoint to manage an antiforgery token.
//
// Skipped when the request authenticates via Authorization: Bearer with
// no cookie — those are programmatic clients (CLI, tests, integrations)
// where CSRF doesn't apply because the browser ambient-credential model
// isn't in play.
app.Use(async (context, next) =>
{
    if (authBypass)
    {
        // No real auth in play — there's no ambient credential to forge.
        await next();
        return;
    }

    var method = context.Request.Method;
    var isMutation = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
                  || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
    var isApi = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
    if (!isMutation || !isApi)
    {
        await next();
        return;
    }

    var hasCookie = context.Request.Cookies.ContainsKey("hermod_session")
                 || context.Request.Cookies.ContainsKey("hermod_token");
    if (!hasCookie)
    {
        // Pure bearer-token call — no ambient browser credentials to abuse.
        await next();
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();
    var referer = context.Request.Headers.Referer.ToString();
    var host = context.Request.Host.Value ?? string.Empty;

    static bool Matches(string headerValue, string host)
    {
        if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(host)) return false;
        if (!Uri.TryCreate(headerValue, UriKind.Absolute, out var uri)) return false;
        // Compare host:port — Authority strips userinfo + path.
        var headerAuthority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return string.Equals(headerAuthority, host, StringComparison.OrdinalIgnoreCase);
    }

    if (!Matches(origin, host) && !Matches(referer, host))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("CSRF: cross-origin or missing Origin/Referer for cookie-authenticated mutation");
        return;
    }

    await next();
});

app.UseAuthentication();

// Audit log for /admin/* — must sit BETWEEN UseAuthentication (so
// context.User is populated) and UseAuthorization (so a 403 short-
// circuit doesn't skip the audit line). Records sub + roles + method +
// path + status + elapsed for every admin-gateway request, including
// denials. Goes to logger "Hermod.Audit.Admin" which inherits the
// "Hermod" namespace's Information level — Loki/Grafana can scrape on
// the literal "AUDIT admin" prefix.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    sw.Stop();
    var sub = context.User?.FindFirst("sub")?.Value
            ?? context.User?.Identity?.Name ?? "anonymous";
    var roles = string.Join(",", context.User?.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value) ?? []);
    var auditLogger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Hermod.Audit.Admin");
    auditLogger.LogInformation(
        "AUDIT admin sub={Sub} roles={Roles} method={Method} path={Path} status={Status} elapsed_ms={Elapsed}",
        sub, roles, context.Request.Method, context.Request.Path.Value, context.Response.StatusCode, sw.ElapsedMilliseconds);
});

app.UseAuthorization();
app.UseAntiforgery();

// /healthz is the k8s anonymous probe; /health is the auth-gated Blazor dashboard.
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapHealthChecks("/healthz/ready").AllowAnonymous();

// Prometheus scrape: counters only (no secrets). Anonymous only when
// Dev.Endpoints is on (dev loops + matrix harness); in prod the same
// Vault42 JWT the UI sends is required, so Prometheus authenticates
// via bearer_token_file pointing at a scraper service-account JWT.
if (hermodSettings.Features.MetricsEndpoint)
{
    var metricsEndpoint = app.MapGet("/metrics", (HermodMetrics metrics) => Results.Text(
        metrics.Render(),
        contentType: "text/plain; version=0.0.4; charset=utf-8"));
    if (hermodSettings.Dev.Endpoints)
    {
        metricsEndpoint.AllowAnonymous();
    }
    else
    {
        metricsEndpoint.RequireAuthorization();
    }
}

app.MapControllers();

// YARP translator proxies (/proxy/zigbee, /proxy/lora, /proxy/bluetooth)
// cannot take the global [Authorize] fallback because AddVault only
// reads Authorization: Bearer headers, and a same-origin iframe
// embed doesn't get to set headers on its top-level load — the
// browser sends cookies only. So the proxy gate accepts any of the
// credentials the browser is actually holding after login:
//   - hermod_token cookie (non-HttpOnly JWT, set by Login.razor for
//     SSR reads and JS fetch)
//   - hermod_session cookie (HttpOnly opaque id, set by AuthProxyController)
//   - Authorization: Bearer (programmatic clients and the test fixtures)
// Anonymous reaches the dashboards is the thing we block; every logged-in
// user is fine. Earlier a hermod_session-only check tripped the WS
// upgrade and iframe load when the browser had only the JWT cookie
// (the 401 we hit on /proxy/zigbee/ during testing).
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/proxy", StringComparison.OrdinalIgnoreCase))
    {
        var cookies = context.Request.Cookies;
        var hasCookieCred = cookies.ContainsKey("hermod_session") || cookies.ContainsKey("hermod_token");
        var hasBearer = context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (!hasCookieCred && !hasBearer)
        {
            context.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});

// MapReverseProxy WITHOUT .AllowAnonymous(): per-route AuthorizationPolicy
// metadata (set by /admin/* routes in TranslatorProxyRegistration) needs
// to actually run; AllowAnonymous would suppress it. /proxy/* routes
// have no AuthorizationPolicy and the custom cookie-or-bearer middleware
// above gates them; /admin/* routes carry AuthorizationPolicy = Admin
// which the auth pipeline enforces against the JWT roles claim.
app.MapReverseProxy();

// Static assets + Blazor framework endpoints stay anonymous: the SignalR
// circuit at /_blazor/* needs to bootstrap BEFORE login (the login page
// itself is a Blazor component). Per-page enforcement is done via the
// [Authorize] attribute on each component + AuthorizeRouteView in
// Routes.razor — pages without [Authorize] are explicitly opt-in
// public (Login, Error, NotFound have [AllowAnonymous]).
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

app.Run();

static void ConfigureSwagger(Swashbuckle.AspNetCore.SwaggerGen.SwaggerGenOptions c)
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Hermod IoT Translator API",
        Version = "v1",
        Description = "REST API for the Hermod Universal IoT Translator",
        Contact = new OpenApiContact { Name = "Hermod Project" }
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
}
