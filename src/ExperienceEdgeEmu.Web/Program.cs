using ExperienceEdgeEmu.Web;
using Serilog;
using Serilog.Events;

// handle arguments
if (!EmuStartupExtensions.TryHandleArguments(args, out var datasetName, out var exitCode))
{
    Environment.ExitCode = exitCode;

    return;
}

// configure app
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog(new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Extensions.Http.DefaultHttpClientFactory", LogEventLevel.Warning)
    .MinimumLevel.Override("Polly", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger());

builder.Services.AddHealthChecks();
builder.Services.AddEmu(builder.Configuration);
builder.Configuration.AddCommandLine(args);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseEmu();
app.MapHealthChecks("/healthz");

// run
app.PrepareDataset(datasetName);

await app.TriggerDataStoreRebuild(app.Lifetime.ApplicationStopping);
await app.RunAsync();
