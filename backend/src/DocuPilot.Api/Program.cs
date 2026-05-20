using DocuPilot.Application;
using DocuPilot.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Compose application + infrastructure layers (stubs in Phase 1).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
