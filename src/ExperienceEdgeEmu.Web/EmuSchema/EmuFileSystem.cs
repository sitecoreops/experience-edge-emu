using Microsoft.Extensions.Options;
using System.IO.Compression;

namespace ExperienceEdgeEmu.Web.EmuSchema;

public enum DataCategory
{
    Unknown = 0,
    Item = 1,
    Site = 2
}

public class EmuFileSystem
{
    private readonly string _dataRootPath;
    private readonly string _datasetLockfilePath;
    private readonly ILogger<EmuFileSystem> _logger;

    public EmuFileSystem(IHostEnvironment env, IOptions<EmuSettings> options, ILogger<EmuFileSystem> logger)
    {
        if (Path.IsPathFullyQualified(options.Value.DataRootPath))
        {
            _dataRootPath = options.Value.DataRootPath;
        }
        else
        {
            _dataRootPath = Path.Combine(env.ContentRootPath, options.Value.DataRootPath);
        }

        _dataRootPath = Path.GetFullPath(_dataRootPath);

        // disable old schema files from before we had automatic import
        foreach (var schemaFilePath in GetSchemaFilePaths())
        {
            if (schemaFilePath.Equals(GetImportedSchemaFilePath(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Move(schemaFilePath, schemaFilePath + ".disabled");
        }

        _datasetLockfilePath = MakeAbsoluteDataPath("dataset.lock");
        _logger = logger;
    }

    public FileSystemWatcher CreateJsonFileWatcher()
    {
        EnsureDataRootPathExists();

        return new(_dataRootPath)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            Filter = "*.json",
            IncludeSubdirectories = true
        };
    }

    private void EnsureDataRootPathExists()
    {
        if (!Directory.Exists(_dataRootPath))
        {
            Directory.CreateDirectory(_dataRootPath);
        }
    }

    public string[] GetSchemaFilePaths()
    {
        EnsureDataRootPathExists();

        return Directory.GetFiles(_dataRootPath, "*.graphqls");
    }

    public string[] GetJsonFilePaths()
    {
        EnsureDataRootPathExists();

        return Directory.GetFiles(_dataRootPath, "*.json", SearchOption.AllDirectories);
    }

    public string GetImportedSchemaFilePath()
    {
        EnsureDataRootPathExists();

        return MakeAbsoluteDataPath("imported-schema.graphqls");
    }

    public string MakeAbsoluteDataPath(string relativePath) => Path.Combine(_dataRootPath, relativePath);

    public DataCategory GetDataCategory(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return DataCategory.Unknown;
        }

        // normalize and convert to '/'
        string Normalize(string p)
        {
            return Path.GetFullPath(p)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .TrimEnd('/');
        }

        var root = Normalize(_dataRootPath);
        var path = Normalize(filePath);

        // remove root if exists
        if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[(root.Length + 1)..];
        }

        // select category
        if (path.StartsWith("item/", StringComparison.OrdinalIgnoreCase))
        {
            return DataCategory.Item;
        }

        if (path.StartsWith("site/", StringComparison.OrdinalIgnoreCase))
        {
            return DataCategory.Site;
        }

        return DataCategory.Unknown;
    }

    public string GetMediaFilePath(Uri mediaUri) => GetMediaFilePath(mediaUri.AbsolutePath);

    public string GetMediaFilePath(string urlPath)
    {
        var relativeMediaPath = urlPath
                                    .Replace("/-/media/", "")
                                    .Replace("/-/jssmedia/", "")
                                    .Replace('/', Path.DirectorySeparatorChar);

        return Path.Combine(_dataRootPath, "media", relativeMediaPath);
    }

    public bool HasData() => GetJsonFilePaths().Length > 0 || GetSchemaFilePaths().Length > 0;

    public bool IsDatasetDeployed()
    {
        return File.Exists(_datasetLockfilePath);
    }

    public void DeployDataset(string name)
    {
        // cleanup old data
        DeleteData();

        // unzip from embedded resource
        var type = typeof(EmuSettings);
        var resourceName = $"{type.Namespace}.Datasets.{name}.zip";

        using var zipStream = type.Assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

        ZipFile.ExtractToDirectory(zipStream, _dataRootPath, overwriteFiles: true);

        // write lock file
        File.WriteAllText(_datasetLockfilePath, name);

        _logger.LogInformation("Deployed dataset: {DatasetName}", name);
    }

    public void DeleteData()
    {
        Directory.Delete(_dataRootPath, recursive: true);

        _logger.LogInformation("Deleted all data in: {DataRootPath}", _dataRootPath);
    }
}
