
using ExperienceEdgeEmu.Web.EmuSchema;
using ExperienceEdgeEmu.Web.Media;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.SystemTextJson;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ExperienceEdgeEmu.Web.DataStore.Crawler;

public record CrawlResult(bool Success, int ItemsProcessed, int SitesProcessed, double DurationMs, string? Message = null);

public class ExperienceEdgeCrawlerService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExperienceEdgeCrawlerService> _logger;
    private readonly EmuFileSystem _emuFileSystem;
    private readonly ItemPostProcessingQueue _filePostProcessingQueue;
    private readonly MediaDownloadQueue _mediaDownloadQueue;
    private readonly DynamicEmuSchema _emuSchema;
    private readonly IntrospectionToSdlConverter _introspectionConverter;
    private int _itemsProcessed;
    private int _sitesProcessed;
    private readonly string _getItemQuery;
    private readonly string _getSitesLanguageNeutralQuery;
    private readonly string _getSitesLanguageDataQuery;
    private readonly string _introspectionQuery;
    private readonly int _maxDegreeOfParallelism;
    private static readonly JsonSerializerOptions _fileWritingJsonSerializerOptions = new() { WriteIndented = true };

    public ExperienceEdgeCrawlerService(IHttpClientFactory httpClientFactory,
        ILogger<ExperienceEdgeCrawlerService> logger,
        IOptions<EmuSettings> options,
        EmuFileSystem emuFileSystem,
        ItemPostProcessingQueue filePostProcessingQueue,
        MediaDownloadQueue mediaDownloadQueue,
        DynamicEmuSchema emuSchema,
        IntrospectionToSdlConverter introspectionConverter)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _emuFileSystem = emuFileSystem;
        _filePostProcessingQueue = filePostProcessingQueue;
        _mediaDownloadQueue = mediaDownloadQueue;
        _emuSchema = emuSchema;
        _introspectionConverter = introspectionConverter;
        _getItemQuery = ReadEmbeddedResource("Queries.get-item.graphql");
        _getSitesLanguageNeutralQuery = ReadEmbeddedResource("Queries.get-sitedata-language-neutral.graphql");
        _getSitesLanguageDataQuery = ReadEmbeddedResource("Queries.get-sitedata-language-data.graphql");
        _introspectionQuery = ReadEmbeddedResource("Queries.introspection.graphql");
        _maxDegreeOfParallelism = options.Value.Crawler.MaxDegreeOfParallelism;
    }

    public async Task<CrawlResult> Crawl(IResolveFieldContext context)
    {
        if (_emuFileSystem.IsDatasetDeployed())
        {
            throw new InvalidOperationException("Cannot crawl while using a dataset, please start app without dataset.");
        }

        _itemsProcessed = 0;
        _sitesProcessed = 0;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var edgeEndpointUrl = context.GetArgument<string?>("edgeEndpointUrl");
        var edgeContextId = context.GetArgument<string>("edgeContextId");
        var languages = context.GetArgument<string[]>("languages", []);
        var siteNames = context.GetArgument<string[]>("siteNames");
        var client = CreateGraphQLClient(edgeEndpointUrl, edgeContextId);

        _logger.LogInformation("Starting crawl endpoint={EdgeEndpointHost}, languages={Languages}, siteNames={SiteNames}.", client.Options.EndPoint?.Host, string.Join(',', languages), string.Join(',', siteNames ?? []));

        var crawlPaths = new List<string>();

        try
        {
            // crawl site data and collect root paths
            var siteDataResponse = await GetSiteDataLanguageNeutralAsync(client, context.CancellationToken);
            var siteData = siteDataResponse.GetSiteData();

            if (siteDataResponse == null || siteData == null)
            {
                throw new Exception("Crawl failed, could not get site data");
            }

            // if site name filter is empty, add all sites
            if (siteNames == null || siteNames.Length == 0)
            {
                siteNames = [.. siteData.AllSiteInfo.Results.Select(x => x.Name)];
            }

            var sitesFiltered = siteData.AllSiteInfo.Results.Where(x => siteNames.Any(y => x.Name.Equals(y, StringComparison.OrdinalIgnoreCase)));

            foreach (var site in sitesFiltered)
            {
                // fix the root path of the default site as it is "/sitecore/content" and not what we want...
                if (site.Name == "website")
                {
                    site.RootPath += "/Home";
                }

                _logger.LogInformation("Found site '{SiteName}' with root path '{Path}'", site.Name, site.RootPath);

                crawlPaths.Add(site.RootPath);
            }

            // build item crawl matrix
            var matrix = new List<(string Path, string Language)>();

            foreach (var crawlPath in crawlPaths)
            {
                foreach (var language in languages)
                {
                    matrix.Add((Path: crawlPath, Language: language));
                }
            }

            // crawl paths and languages
            await Parallel.ForEachAsync(matrix, new ParallelOptions
            {
                CancellationToken = context.CancellationToken,
                MaxDegreeOfParallelism = _maxDegreeOfParallelism
            }, async (crawlOrder, ct) =>
            {
                await CrawlItemAsync(client, crawlOrder.Language, crawlOrder.Path, ct);
            });

            // crawl sites
            await CrawlSites(client, languages, siteDataResponse, siteNames, context.CancellationToken);
        }
        catch (Exception ex)
        {
            var errorMessage = "Crawl failed due to exception.";

            _logger.LogError(ex, errorMessage);

            context.Errors.Add(new ExecutionError(ex.Message, ex));

            return new CrawlResult(true, _itemsProcessed, _sitesProcessed, watch.Elapsed.TotalMilliseconds, errorMessage);
        }

        var results = new CrawlResult(true, _itemsProcessed, _sitesProcessed, watch.Elapsed.TotalMilliseconds);

        // wait on file post processing
        while (!_filePostProcessingQueue.IsEmpty())
        {
            _logger.LogInformation("File post processing queue is not empty, waiting...");

            await Task.Delay(500);
        }

        // wait on media downloads
        while (!_mediaDownloadQueue.IsEmpty())
        {
            _logger.LogInformation("Media download queue is not empty, waiting...");

            await Task.Delay(500);
        }

        // import SDL and reload schema
        await ImportSdlAsync(client, context.CancellationToken);

        _emuSchema.ReloadSchema();

        // done
        _logger.LogInformation("Crawl finished, found {ItemsProcessed} items.", _itemsProcessed);

        return results;
    }

    private async Task ImportSdlAsync(GraphQLHttpClient client, CancellationToken cancellationToken)
    {
        var request = new GraphQLRequest
        {
            Query = _introspectionQuery,
        };

        var response = await client.SendQueryAsync<IntrospectionSchema>(request, cancellationToken);

        EnsureSuccessGraphQLResponse(response);

        var sdl = _introspectionConverter.ToSdl(response.Data);
        var sdlFilePath = _emuFileSystem.GetImportedSchemaFilePath();

        await File.WriteAllTextAsync(sdlFilePath, sdl, cancellationToken);
    }

    private async Task<GetSiteDataLanguageNeutralResponse> GetSiteDataLanguageNeutralAsync(GraphQLHttpClient client, CancellationToken cancellationToken)
    {
        var request = new GraphQLRequest
        {
            Query = _getSitesLanguageNeutralQuery,
        };

        var response = await client.SendQueryAsync<GetSiteDataLanguageNeutralResponse>(request, cancellationToken);

        EnsureSuccessGraphQLResponse(response);

        return response.Data;
    }

    private async Task<GetSiteDataLanguageDataResponse> GetSitesLanguageDataAsync(GraphQLHttpClient client, string language, CancellationToken cancellationToken)
    {
        var request = new GraphQLRequest
        {
            Query = _getSitesLanguageDataQuery,
            Variables = new { language }
        };

        var response = await client.SendQueryAsync<GetSiteDataLanguageDataResponse>(request, cancellationToken);

        EnsureSuccessGraphQLResponse(response);

        return response.Data;
    }

    private async Task<GraphQLResponse<ItemResponse>> GetItemResponseAsync(GraphQLHttpClient client, string language, string path, CancellationToken cancellationToken)
    {
        var request = new GraphQLRequest
        {
            Query = _getItemQuery,
            Variables = new
            {
                path,
                language
            }
        };

        var response = await client.SendQueryAsync<ItemResponse>(request, cancellationToken);

        EnsureSuccessGraphQLResponse(response);

        return response;
    }

    private void EnsureSuccessGraphQLResponse<T>(GraphQLResponse<T> response)
    {
        if (response.Errors != null && response.Errors.Length > 0)
        {
            foreach (var error in response.Errors)
            {
                _logger.LogError("GraphQL client error: {ErrorMessage}", error.Message);
            }

            throw new GraphQLResponseErrorException();
        }

        if (response.Data == null)
        {
            throw new GraphQLResponseDataNullException();
        }
    }

    private async Task CrawlItemAsync(GraphQLHttpClient client, string language, string path, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Crawling path '{Path}' and language {Language}", path, language);

        var response = await GetItemResponseAsync(client, language, path, cancellationToken);
        var data = response.Data ?? throw new Exception("Crawl failed, response data was null.");
        var item = data.GetItem();

        if (item == null)
        {
            return;
        }

        await SaveItem(language, item, response.Data.Raw, cancellationToken);

        Interlocked.Increment(ref _itemsProcessed);

        if (!item.HasChildren || item.Children.Total == 0)
        {
            return;
        }

        if (item.Children.PageInfo.HasNext)
        {
            _logger.LogError("Paging of children not supported, item.children.pageInfo.hasNext was true and item.children.total was {ChildrenTotal} on {Path}.", path, item.Children.Total);

            return;
        }

        var maxDegreeOfParallelism = _maxDegreeOfParallelism >= 5 ? 5 : _maxDegreeOfParallelism;

        await Parallel.ForEachAsync(item.Children.Results, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism
        }, async (child, ct) =>
        {
            await CrawlItemAsync(client, language, child.Path, ct);
        });
    }

    private async Task SaveItem(string language, SitecoreItem item, JsonObject raw, CancellationToken cancellationToken)
    {
        if (item?.Path == null)
        {
            return;
        }

        // build short item path
        var itemPath = item.Path.Replace("/sitecore/", "/", StringComparison.OrdinalIgnoreCase);

        if (item.Path.EndsWith("/" + item.Name, StringComparison.OrdinalIgnoreCase))
        {
            itemPath = itemPath[..itemPath.LastIndexOf("/" + item.Name, StringComparison.OrdinalIgnoreCase)];
        }
        else
        {
            _logger.LogError("Save item aborted, path '{ItemPath}' does not end with item name '{ItemName}', cannot shorten the item path.", item.Path, item.Name);

            return;
        }

        string ShortenSegment(string input)
        {
            input = input.Replace(" ", string.Empty);
            input = input.Replace("-", string.Empty);

            if (input.Length <= 8)
            {
                return input;
            }

            return input[..8];
        }

        var shortPath = string.Join('/', itemPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Take(6)
            .Select(x => ShortenSegment(x).ToLowerInvariant()));

        // save item
        var directory = _emuFileSystem.MakeAbsoluteDataPath(Path.Combine("item", shortPath));

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var filePath = Path.GetFullPath(Path.Combine(directory, $"{ShortenSegment(item.Name)}_{item.Id}_{language}.json".ToLowerInvariant()));

        await using var stream = File.Create(filePath);

        await JsonSerializer.SerializeAsync(stream, new { data = new { item = raw } }, _fileWritingJsonSerializerOptions, cancellationToken);

        _logger.LogInformation("Saved item data to {FilePath}", filePath);

        // enqueue message for post processing
        await _filePostProcessingQueue.QueueMessageAsync(new ItemPostProcessingMessage(filePath, raw));
    }

    private async Task CrawlSites(GraphQLHttpClient client, string[] languages, GetSiteDataLanguageNeutralResponse sitesLanguageNeutral, IEnumerable<string> acceptedSiteNames, CancellationToken cancellationToken)
    {
        // save the language neutral sites data
        await SaveSiteDataLanguageNeutral(sitesLanguageNeutral, cancellationToken);

        await Parallel.ForEachAsync(languages, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _maxDegreeOfParallelism
        }, async (language, ct) =>
        {
            // get and save language specfic site data
            var sitesLanguageData = await GetSitesLanguageDataAsync(client, language, ct);

            foreach (var site in (sitesLanguageData.Site?.AllSiteInfo.Results ?? []).Where(x => acceptedSiteNames.Any(y => x.Name.Equals(y, StringComparison.OrdinalIgnoreCase))))
            {
                await SaveSiteInfo(site, language, ct);
            }
        });
    }

    private async Task SaveSiteDataLanguageNeutral(GetSiteDataLanguageNeutralResponse data, CancellationToken cancellationToken)
    {
        var directory = _emuFileSystem.MakeAbsoluteDataPath("site");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var filePath = Path.GetFullPath(Path.Combine(directory, "sitedata.json"));

        await using var stream = File.Create(filePath);

        await JsonSerializer.SerializeAsync(stream, new { data = new { site = data.Raw } }, _fileWritingJsonSerializerOptions, cancellationToken);

        Interlocked.Add(ref _sitesProcessed, data.GetSiteData()?.AllSiteInfo.Total ?? 0);

        _logger.LogInformation("Saved sites data to {FilePath}", filePath);
    }

    private async Task SaveSiteInfo(SiteInfoLanguageDataWrite data, string language, CancellationToken cancellationToken)
    {
        var directory = _emuFileSystem.MakeAbsoluteDataPath(Path.Combine("site", "siteinfo", data.Name));

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var filePath = Path.GetFullPath(Path.Combine(directory, $"{language}.json"));

        await using var stream = File.Create(filePath);

        await JsonSerializer.SerializeAsync(stream, new { siteInfoData = data }, _fileWritingJsonSerializerOptions, cancellationToken);

        _logger.LogInformation("Saved sites data to {FilePath}", filePath);
    }

    private GraphQLHttpClient CreateGraphQLClient(string? edgeEndpointUrl, string edgeContextId)
    {
        Uri? edgeEndpointUri;
        var isRealEdgeUri = false;

        if (string.IsNullOrEmpty(edgeEndpointUrl))
        {
            edgeEndpointUri = new Uri($"https://edge-platform.sitecorecloud.io/v1/content/api/graphql/v1?sitecoreContextId={edgeContextId}");

            isRealEdgeUri = true;
        }
        else
        {
            edgeEndpointUri = new Uri(edgeEndpointUrl);
        }

        var httpClient = _httpClientFactory.CreateClient(StringConstants.EmuHttpClientName);

        if (!isRealEdgeUri)
        {
            httpClient.DefaultRequestHeaders.Add(StringConstants.ScApiKey, edgeContextId);
        }

        return new GraphQLHttpClient(
            new GraphQLHttpClientOptions { EndPoint = edgeEndpointUri },
            new SystemTextJsonSerializer(new JsonSerializerOptions { PropertyNameCaseInsensitive = false }),
            httpClient);
    }

    private string ReadEmbeddedResource(string name)
    {
        var type = GetType();
        var resourceName = $"{type.Namespace}.{name}";

        using var stream = type.Assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private class ItemResponse
    {
        public SitecoreItem? GetItem() => Raw.Deserialize<SitecoreItem>(JsonSerializerOptions.Web);

        [JsonPropertyName("item")]
        public required JsonObject Raw { get; set; }
    }

    private class GetSiteDataLanguageNeutralResponse
    {
        public SitecoreSiteData? GetSiteData() => Raw.Deserialize<SitecoreSiteData>(JsonSerializerOptions.Web);

        [JsonPropertyName("site")]
        public required JsonObject Raw { get; set; }
    }

    private class GetSiteDataLanguageDataResponse
    {
        public SitecoreSiteDataLanguageData? Site { get; set; }
    }
}
