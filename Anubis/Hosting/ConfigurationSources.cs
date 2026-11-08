using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.FileProviders;

namespace k6labs.Anubis.Hosting;

/// <summary>
/// Optional configuration mounted into the container, placed below the environment variables:
/// <list type="bullet">
/// <item><c>Anubis:ConfigFile</c>: a JSON file in the appsettings format (e.g. a Kubernetes ConfigMap),</item>
/// <item><c>Anubis:SecretsDirectory</c>: a directory with one file per key (e.g. a mounted Secret;
/// the file <c>Email__Smtp__Password</c> sets <c>Email:Smtp:Password</c>).</item>
/// </list>
/// Resulting priority: appsettings.json &lt; appsettings.{Environment}.json &lt; ConfigFile &lt;
/// SecretsDirectory &lt; environment variables &lt; command line (&lt; the admin settings file, added later).
/// Both reload when the mounted files change.
/// </summary>
public static class ConfigurationSources
{
    public const string ConfigFileKey = "Anubis:ConfigFile";
    public const string SecretsDirectoryKey = "Anubis:SecretsDirectory";

    public static void AddMountedSources(IConfigurationBuilder configuration, string contentRoot)
    {
        var settings = configuration.Build();   // only to read the two paths themselves
        var sources = configuration.Sources;
        var environmentIndex = sources.ToList().FindLastIndex(source => source is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        var insertAt = environmentIndex < 0 ? sources.Count : environmentIndex;

        if (settings[ConfigFileKey] is { Length: > 0 } configFile)
        {
            var json = new JsonConfigurationSource
            {
                Path = Path.GetFullPath(configFile, contentRoot),
                Optional = false,
                ReloadOnChange = true,
            };
            json.ResolveFileProvider();
            sources.Insert(insertAt++, json);
        }

        if (settings[SecretsDirectoryKey] is { Length: > 0 } secretsDirectory)
        {
            sources.Insert(insertAt, new KeyPerFileConfigurationSource
            {
                FileProvider = new PhysicalFileProvider(Path.GetFullPath(secretsDirectory, contentRoot)),
                Optional = true,
                ReloadOnChange = true,
            });
        }
    }
}
