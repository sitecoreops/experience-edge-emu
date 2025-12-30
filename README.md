# 🪶 Experience Edge Emu (EEE) 🪶

Lightweight Sitecore Experience Edge emulator for local (offline) cross-platform development and test automation.

[![cicd](https://github.com/sitecoreops/experience-edge-emu/actions/workflows/cicd.yml/badge.svg)](https://github.com/sitecoreops/experience-edge-emu/actions/workflows/cicd.yml)
[![Latest Release](https://img.shields.io/github/v/release/sitecoreops/experience-edge-emu)](https://github.com/sitecoreops/experience-edge-emu/releases/latest)
[![Docker Image](https://img.shields.io/badge/docker-ghcr.io/sitecoreops/eee-blue)](https://ghcr.io/sitecoreops/eee)

## Features

- GraphQL endpoint
  - Experience Edge compatibility:
    - `site` queries.
    - `layout` queries.
    - `item` queries.
    - `search` queries **NOT SUPPORTED**⚠️.
  - Extras:
    - `crawl` mutation, crawls existing Experience Edge endpoint to seed emulator with data and media 🚀.
- Hosting media items.
- [GraphiQL UI](https://github.com/graphql-dotnet/server) accessible on `/`.
- Hot reloading data when files in data root is modified.
- Health endpoint `/healthz`.
- Docker multi platform images `docker image pull ghcr.io/sitecoreops/eee` (runs on both Windows x64 and Linux x64).
- Native binaries for Windows x64 and Linux x64.
- Predefined "skatepark" dataset, use argument `--dataset skatepark`. 

## Data layout

Under your data root (default `./data`, configured with the `EMU__DATAROOTPATH` environment variable) the following rules apply:

```text
./data
   ├── /items/**/*.json (any structure supported, files must contain at least the required fields of type Item in the schema)
   ├── /site
         ├── /**/<language>.json (language specific siteInfo data such as dictionary and routes)
         ├── sitedata.json (SiteData.allSiteInfo is stored here )
   ├── /media/** (stored as the media path)
   ├── /imported-schema.graphqls (will be create first time the crawl mutation is executed)
```

> 💡TIP: Run a `crawl` mutation to get some data to learn from.

## Crawling preview endpoints (preview context id's or local SitecoreAI CMS instances)

If you want to crawl Experience Edge with *preview** context id's or a local SitecoreAI CMS instances, then you will hit a CM server. This requires the following patch to increase the Sitecore GraphQL complexity configuration:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <sitecore>
    <api>
      <GraphQL>
        <defaults>
          <security>
            <publicService type="Sitecore.Services.GraphQL.Hosting.Security.GraphQLSecurity, Sitecore.Services.GraphQL">
              <complexityConfiguration type="GraphQL.Validation.Complexity.ComplexityConfiguration, GraphQL">
                <maxDepth>50</maxDepth>
                <maxComplexity>500000</maxComplexity>
              </complexityConfiguration>
            </publicService>
          </security>
        </defaults>
      </GraphQL>
    </api>
  </sitecore>
</configuration>
```

## Limitations & known issues

Currently there a few limitations/gotchas, some may be fixed in the future:

1. When running `eee` in Docker, you cannot crawl a local SitecoreAI CMS instance *unless* they share the same Docker network.
1. Using the `maxWidth` and `maxHeight` on `src` property fields does nothing.
1. `SiteInfo.RoutesResult` only supports the `language` and `first` parameters, `excludedPaths`, `includePaths` and `after` does nothing.
1. `SiteInfo.DictionaryResult` only supports the `language` and `first` parameters, `after` does nothing.

## Quick start

You can run in Docker or download native binaries for Linux and Windows. Running with SSL is important if your head application also runs SSL to avoid the browser blocks loading media on non SSL urls.

### Docker

run without SSL:

```powershell
docker run -e "EMU__MEDIAHOST=http://localhost:5710" -p 5710:8080 ghcr.io/sitecoreops/eee
```

or with persistence:

```powershell
docker run -v "./data/eee:/app/data" -e "EMU__MEDIAHOST=http://localhost:5710" -p 5710:8080 ghcr.io/sitecoreops/eee
```

or with SSL:

1. Use [./compose.yml](./compose.yml) as reference, modify as needed, for example change image data volumes.
1. Then `docker compose up -d`.
1. Make your machine trust the certificate, run `certutil -addstore -f "ROOT" ".\\docker\\caddy\\data\\caddy\\pki\\authorities\\local\\root.crt"`.

### Native binary

1. Download one of the binaries from <https://github.com/sitecoreops/eee/releases>.
1. Without SSL, run `.\eee.exe` (Windows) or `eee` (Linux).
1. For SSL, add the arguments:
   1. `--Kestrel:Endpoints:HttpsDefaultCert:Url=https://localhost:5711` to use the developer certificate from `dotnet dev-certs`.
   1. or `--Kestrel:Endpoints:Https:Url=https://localhost:5711 --Kestrel:Endpoints:Https:Certificate:Subject=localhost` to use your own.

### Usage

Run `query`:

```powershell
curl -k "https://localhost:5711/graphql" -H "Content-Type: application/json" --data-raw '{"query":"{item(path:\"/sitecore/content/tests/minimal\",language:\"en\"){id,path,name,displayName}}"}'
```

Run `crawl` mutation:

```powershell
curl -k "https://localhost:5711/graphql" -H "Content-Type: application/json" --data-raw '{"query":"mutation{crawl(edgeContextId:\"<EDGE-CONTEXT-ID>\",languages:[\"en\"]){success,itemsProcessed,sitesProcessed,durationMs,message}}"}'
```

Or open <https://localhost:5711> to use the GraphiQL UI.

When you have seeded some data, change your local head application to use <https://localhost:5711/graphql> instead of your usual Experience Edge url.
