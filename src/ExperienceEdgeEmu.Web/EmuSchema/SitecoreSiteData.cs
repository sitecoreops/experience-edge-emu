namespace ExperienceEdgeEmu.Web.EmuSchema;

public record SitecoreSiteData(SiteInfoResult AllSiteInfo)
{
    public SiteInfo? SiteInfo(string site) => AllSiteInfo.Results.FirstOrDefault(x => x.Name.Equals(site, StringComparison.OrdinalIgnoreCase));

    public SiteInfo[] SiteInfoCollection() => AllSiteInfo.Results;
}

public record SiteInfoResult(int Total, SiteInfo[] Results);

public class SiteInfo
{
    public required string Name { get; set; }
    public required string RootPath { get; set; }
    public required RedirectInfo[] Redirects { get; set; }
    public string? Hostname { get; set; }
    public string? Language { get; set; }
    public string? Robots { get; set; }
    public string[]? Sitemap { get; set; }
    public KeyValuePair[]? Attributes { get; set; }

    internal Dictionary<string, SiteInfoLanguageDataRead?> InternalLanguageData { get; set; } = [];

    public SiteDictionary? Dictionary(string? language, int? first, string? after)
    {
        if (InternalLanguageData != null && InternalLanguageData.TryGetValue(language?.ToLowerInvariant() ?? "en", out var data))
        {
            if (data == null || data.Dictionary == null)
            {
                return new SiteDictionary();
            }

            if (first != null)
            {
                return new SiteDictionary
                {
                    PageInfo = data.Dictionary.PageInfo,
                    Total = data.Dictionary.Total,
                    Results = [.. data.Dictionary.Results.Take(first.Value)]
                };
            }

            return data?.Dictionary;
        }

        return null;
    }

    public SiteErrorhandling? ErrorHandling(string? language)
    {
        if (InternalLanguageData != null && InternalLanguageData.TryGetValue(language?.ToLowerInvariant() ?? "en", out var data))
        {
            return data?.ErrorHandling;
        }

        return new SiteErrorhandling();
    }

    public SiteRoutesResult? Routes(string? language, int? first, string? after)
    {
        if (InternalLanguageData != null && InternalLanguageData.TryGetValue(language?.ToLowerInvariant() ?? "en", out var data))
        {
            if (data == null || data.Routes == null)
            {
                return new SiteRoutesResult();
            }

            if (first != null)
            {
                return new SiteRoutesResult
                {
                    PageInfo = data.Routes.PageInfo,
                    Total = data.Routes.Total,
                    Results = [.. data.Routes.Results.Take(first.Value)]
                };
            }

            return data.Routes;
        }

        return new SiteRoutesResult();
    }
}

public record RedirectInfo(bool IsQueryStringPreserved, string Locale, string Pattern, string RedirectType, string Target);

public class SiteDictionary
{
    public PageInfo PageInfo { get; set; } = new PageInfo(false, string.Empty);
    public int Total { get; set; }
    public KeyValuePair[] Results { get; set; } = [];
}

public record KeyValuePair(string Key, object Value);

public class SiteErrorhandling
{
    public SitecoreItem? NotFoundPage { get; set; }
    public string NotFoundPagePath { get; set; } = string.Empty;
    public SitecoreItem? ServerErrorPage { get; set; }
    public string ServerErrorPagePath { get; set; } = string.Empty;
}

public class SiteRoutesResult
{
    public PageInfo PageInfo { get; set; } = new PageInfo(false, string.Empty);
    public int Total { get; set; }
    public RouteResult[] Results { get; set; } = [];
}

public record SiteDataRoutesResult(PageInfo PageInfo, int Total, RouteDataResult[] Results);

public class RouteResult
{
    public required SitecoreItem Route { get; set; }
    public required string RoutePath { get; set; }
}

public class RouteDataResult
{
    public required RouteInfo Route { get; set; }
    public required string RoutePath { get; set; }
}

public record RouteInfo(string Id);

public record SitecoreSiteDataLanguageData(SiteInfoResultLanguageData AllSiteInfo);

public record SiteInfoResultLanguageData(int Total, SiteInfoLanguageDataWrite[] Results);

public record SiteInfoLanguageDataWrite(string Name, SiteDictionary? Dictionary, SiteErrorhandling? ErrorHandling, SiteDataRoutesResult? Routes);

public record SiteInfoLanguageDataRead(string Name, SiteDictionary? Dictionary, SiteErrorhandling? ErrorHandling, SiteRoutesResult? Routes);
