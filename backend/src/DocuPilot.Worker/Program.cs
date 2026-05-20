using DocuPilot.Application;
using DocuPilot.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

// Compose application + infrastructure layers (stubs in Phase 1).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Hosted services (document processing pipeline) are registered in later phases.

var host = builder.Build();
host.Run();
