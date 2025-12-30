using ExperienceEdgeEmu.Web.DataStore;
using ExperienceEdgeEmu.Web.DataStore.Crawler;
using ExperienceEdgeEmu.Web.EmuSchema;
using ExperienceEdgeEmu.Web.Media;
using GraphQL;
using GraphQL.Server.Ui.GraphiQL;
using GraphQL.Types;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using System.CommandLine;

namespace ExperienceEdgeEmu.Web;

public static partial class EmuStartupExtensions
{
    public static bool TryHandleArguments(string[] args, out string? datasetName, out int exitCode)
    {
        exitCode = 0;
        datasetName = null;

        var validDatasetNames = new[] { "skatepark" };

        Option<string> datasetOption = new("--dataset")
        {
            Description = $"Builtin dataset to use, allowed values: {string.Join(", ", validDatasetNames)}"
        };

        datasetOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();

            if (value is not null && !validDatasetNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.AddError($"Invalid dataset '{value}', allowed values are: {string.Join(", ", validDatasetNames)}.");
            }
        });

        RootCommand rootCommand = new("Experience Edge Emu (EEE)");

        rootCommand.Options.Add(datasetOption);
        rootCommand.TreatUnmatchedTokensAsErrors = false; // allow other args for web host

        var parseResult = rootCommand.Parse(args);

        parseResult.Invoke();

        if (parseResult.Errors.Count > 0)
        {
            Environment.ExitCode = 1;

            return false;
        }

        if (parseResult.Tokens.Any(x => string.Equals(x.ToString(), "--help", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        datasetName = parseResult.GetValue(datasetOption);

        return true;
    }

    public static IServiceCollection AddEmu(this IServiceCollection services, IConfigurationRoot configuration)
    {
        var emuSection = configuration.GetSection(EmuSettings.Key);

        ArgumentNullException.ThrowIfNull(emuSection);

        // configure specific HttpClient for calling Experience Edge with custom resiliency policy
        services.AddHttpClient(StringConstants.EmuHttpClientName).AddResilienceHandler(StringConstants.EmuResiliencePolicyName, builder =>
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                                    .Handle<HttpRequestException>()
                                    .HandleResult(r => (int)r.StatusCode is 429 or >= 500),
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromSeconds(3),
                UseJitter = true
            });

            builder.AddTimeout(new HttpTimeoutStrategyOptions()
            {
                Timeout = TimeSpan.FromSeconds(30)
            });
        });

        services.Configure<EmuSettings>(emuSection);
        services.AddSingleton<EmuFileSystem>();
        services.AddSingleton<InMemoryItemStore>();
        services.AddSingleton<InMemorySiteDataStore>();
        services.AddSingleton<FileDataStore>();
        services.AddSingleton<IntrospectionToSdlConverter>();
        services.AddScoped<ExperienceEdgeCrawlerService>();
        services.AddSingleton<ItemPostProcessingQueue>();
        services.AddSingleton<JsonMediaUrlReplacer>();
        services.AddHostedService<ItemPostProcessingWorker>();
        services.AddSingleton<MediaDownloadQueue>();
        services.AddMemoryCache();
        services.AddHostedService<MediaDownloadWorker>();
        services.AddSingleton<FileExtensionContentTypeProvider>();
        services.AddSingleton<MediaUrlRewriter>();
        services.AddSingleton<JsonFileChangeQueue>();
        services.AddHostedService<JsonFileWatcherWorker>();
        services.AddHostedService<JsonFileChangeWorker>();
        services.AddEmuSchema();
        services.AddGraphQL(b => b
          .AddSchema<DynamicEmuSchema>()
          .AddSystemTextJson()
          .UseMemoryCache(options =>
           {
               options.SizeLimit = 1000000;
               options.SlidingExpiration = null;
           })
          .ConfigureExecutionOptions(options =>
          {
              var logger = options.RequestServices!.GetRequiredService<ILogger<Program>>();

              options.UnhandledExceptionDelegate = ctx =>
              {
                  logger.LogError(ctx.OriginalException, "Unhandled GraphQL server exception.");

                  return Task.CompletedTask;
              };
          })
        );

        return services;
    }

    public static WebApplication UseEmu(this WebApplication app)
    {
        var graphQLEndpoint = "/graphql";

        app.UseWebSockets();

        // rewrite default calls from Content SDK and ASP.NET SDK
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value!.TrimEnd('/');

            if (path.Equals("/sitecore/api/graph/edge", StringComparison.OrdinalIgnoreCase) || path.Equals("/v1/content/api/graphql/v1", StringComparison.OrdinalIgnoreCase))
            {
                context.Request.Path = graphQLEndpoint;
            }

            await next();
        });

        // wire up GraphQL endpoint
        app.UseGraphQL<ISchema>(graphQLEndpoint, options =>
        {
            options.CsrfProtectionEnabled = false;
        });

        // wire up GraphQL UI
        app.UseGraphQLGraphiQL("/", new GraphiQLOptions
        {
            GraphQLEndPoint = graphQLEndpoint,
            SubscriptionsEndPoint = graphQLEndpoint,
        });

        app.UseMiddleware<MediaFileMiddleware>();

        return app;
    }

    public static WebApplication PrepareDataset(this WebApplication app, string? datasetName)
    {
        var fileSystem = app.Services.GetRequiredService<EmuFileSystem>();

        if (datasetName != null)
        {
            if (!fileSystem.IsDatasetDeployed() && fileSystem.HasData())
            {
                throw new InvalidOperationException("Cannot load a dataset when data exists in data root, please delete data.");
            }

            fileSystem.DeployDataset(datasetName);
        }
        else if (fileSystem.IsDatasetDeployed())
        {
            fileSystem.DeleteData();
        }

        return app;
    }

    public static async Task TriggerDataStoreRebuild(this WebApplication app, CancellationToken cancellationToken) =>
        await app.Services.GetRequiredService<FileDataStore>().RebuildAsync(cancellationToken);

}
