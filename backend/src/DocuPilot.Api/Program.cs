using DocuPilot.Services;
using DocuPilot.Infrastructure;
using Serilog;

// Bootstrap logger — used until the host's Serilog config takes over. Anything
// logged here (startup failures, restore-time crashes) still ends up in stdout.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("DocuPilot.Api starting up");

    var builder = WebApplication.CreateBuilder(args);

    // Host-wide Serilog: pulls from appsettings.json "Serilog" section if present
    // and falls back to a structured console sink. Replaces the default
    // Microsoft.Extensions.Logging providers entirely.
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}"));

    // CORS — Development-only origin for the Angular dev container. Tightened
    // in Phase 9 when production hosting is settled. See tech-lead section 4.
    const string DocuPilotWebCorsPolicy = "AllowDocuPilotWeb";
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(DocuPilotWebCorsPolicy, policy => policy
            .WithOrigins("http://localhost:4210")
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    // Compose services + infrastructure layers (stubs in Phase 1.5).
    builder.Services.AddServices();
    builder.Services.AddInfrastructure(builder.Configuration);

    // System clock for testable timestamp generation in HealthController.
    builder.Services.AddSingleton(TimeProvider.System);

    builder.Services.AddControllers();

    // Swashbuckle / Swagger registration. UI is only enabled in Development below.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "DocuPilot API",
            Version = "v1",
            Description = "DocuPilot AI — private document-grounded RAG + workflow API."
        });
    });

    var app = builder.Build();

    // Structured request logging — one line per request, includes status + elapsed.
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "DocuPilot API v1");
            options.DocumentTitle = "DocuPilot API — Swagger UI";
        });
    }

    app.UseCors(DocuPilotWebCorsPolicy);

    app.UseAuthorization();

    app.MapControllers();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "DocuPilot.Api terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Expose the Program class to integration test projects (WebApplicationFactory<Program>).
public partial class Program;
