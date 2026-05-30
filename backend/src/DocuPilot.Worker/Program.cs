using DocuPilot.Services;
using DocuPilot.Infrastructure;
using DocuPilot.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Compose the SHARED Services + Infrastructure layers — the SAME extensions the API calls, so
// the two hosts can never drift on a registration (lessons.md DA-021: the Worker exited 139 when
// a dependency lived only in the API's Program.cs). Everything the poller resolves
// (IDocumentProcessingService, repositories, IFileStorage, the text extractors, TimeProvider,
// the scoped DocuPilotDbContext) comes from here — no inline service registration below.
builder.Services.AddServices();
builder.Services.AddInfrastructure(builder.Configuration);

// Phase 3 (DA-025): bind the poller config (Worker:PollIntervalSeconds / Worker:StuckResetMinutes,
// env Worker__* — DA-028) and register the document-processing BackgroundService.
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.AddHostedService<DocumentProcessingWorker>();

var host = builder.Build();
host.Run();
