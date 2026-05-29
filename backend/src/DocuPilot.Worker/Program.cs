using DocuPilot.Services;
using DocuPilot.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

// Compose services + infrastructure layers (stubs in Phase 1.5).
builder.Services.AddServices();
builder.Services.AddInfrastructure(builder.Configuration);

// Hosted services (document processing pipeline) are registered in later phases.

var host = builder.Build();
host.Run();
