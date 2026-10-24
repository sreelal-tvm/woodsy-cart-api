using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using WoodsyCart.Api.Hosting;
using WoodsyCart.Infrastructure;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

// As a Windows service the working folder is C:\Windows\System32, so settings and files are read from the app's own folder.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});
builder.Host.UseWindowsService(o => o.ServiceName = "WoodsyCartApi");

var webRoot = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(Path.Combine(webRoot, "media"));
builder.Services.AddInfrastructure(builder.Configuration, webRoot);
// A failing background job (sync, photo clean-up) is logged — it must never shut the API down.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Encrypts payment provider secrets stored in the database. Keys live in App_Data/keys — back this folder up;
// losing it means re-entering gateway secrets in Admin → Payment integrations.
builder.Services.AddDataProtection()
    .SetApplicationName("WoodsyCart")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

// robots.txt, sitemap.xml and link previews (served by SeoController, or written as files for IIS / Plesk)
builder.Services.AddScoped<WoodsyCart.Api.Seo.SeoBuilder>();
builder.Services.Configure<WoodsyCart.Api.Seo.SeoOptions>(builder.Configuration.GetSection("Seo"));
builder.Services.AddHostedService<WoodsyCart.Api.Seo.SeoFilesWriter>();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Configure Jwt:Key (at least 32 characters) in appsettings or user secrets.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidateAudience = true, ValidAudience = builder.Configuration["Jwt:Audience"],
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin", "Staff"));

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("Content-Disposition")));

builder.Services.AddTrafficControl();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "WoodsyCart API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
    c.CustomSchemaIds(t => t.FullName?.Replace('+', '.'));
});

var app = builder.Build();

app.UseCloudflareClientIp();
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        AppException a => (a.StatusCode, a.Message),
        _ => (500, "Something went wrong. Please try again.")
    };
    if (status == 500) app.Logger.LogError(ex, "Unhandled error");
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { message });
}));

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DbSeeder>().SeedAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseResponseCompression();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webp"] = "image/webp";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    // Photos never change under the same name (uploads get new names, re-cleaned photos new versions), so browsers
    // and Cloudflare may keep them for a year.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/media") ? "public,max-age=31536000,immutable" : "public,max-age=2592000"
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseOutputCache();
app.UseCatalogCacheEviction();
app.MapControllers();
app.MapGet("/", () => app.Environment.IsDevelopment() ? Results.Redirect("/swagger") : Results.Text("WoodsyCart API"));
// The API's own address: keep crawlers out of the JSON endpoints but let them see product photos.
app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /api/\nAllow: /media/\n", "text/plain"));

app.Run();
