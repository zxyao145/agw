using Agw.Host.Hosting;
using Agw.Host.Logging;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agw.Host.Tests.Logging;

public sealed class AgwLoggerFactoryTests : IDisposable
{
    private const string InfoSink = "Serilog:WriteTo:Files:Args:configure:Info";
    private const string ErrorSink = "Serilog:WriteTo:Files:Args:configure:Error";

    private readonly string _directory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".scratch", $"logger-{Guid.CreateVersion7():N}")
    );

    [Fact]
    public void CreateLogger_DefaultLogDirectory_WritesBuiltInFiles()
    {
        using var configuration = CreateConfiguration();
        var paths = CreatePaths(configuration);

        var (information, error) = WriteMarkers(configuration, paths, AgwHostProfile.Standalone);

        Assert.Equal(Path.Combine(_directory, "data", "logs"), paths.LogsDirectory);
        var info = ReadLogs(paths.LogsDirectory, "agw-info-*.log");
        var errors = ReadLogs(paths.LogsDirectory, "agw-error-*.log");
        var host = ReadLogs(paths.LogsDirectory, "application-standalone-*.log");
        Assert.Contains(information, info);
        Assert.Contains(error, info);
        Assert.Contains(information, host);
        Assert.Contains(error, host);
        Assert.Contains(error, errors);
        Assert.DoesNotContain(information, errors);
    }

    [Fact]
    public void CreateLogger_ExplicitLogDirectory_WritesBuiltInFiles()
    {
        var logs = Path.Combine(_directory, "日志 目录 & logs");
        using var configuration = CreateConfiguration(new() { ["AgwLogDir"] = logs });
        var paths = CreatePaths(configuration);

        var (information, error) = WriteMarkers(configuration, paths, AgwHostProfile.Standalone);

        Assert.Equal(logs, paths.LogsDirectory);
        Assert.False(Directory.Exists(Path.Combine(paths.Root, "logs")));
        Assert.Contains(information, ReadLogs(logs, "agw-info-*.log"));
        Assert.Contains(error, ReadLogs(logs, "agw-error-*.log"));
        Assert.Contains(information, ReadLogs(logs, "application-standalone-*.log"));
    }

    [Theory]
    [InlineData(AgwHostProfile.Standalone, "standalone")]
    [InlineData(AgwHostProfile.ControlPlane, "controlplane")]
    [InlineData(AgwHostProfile.DataPlane, "dataplane")]
    public void CreateLogger_HostProfiles_UsesExpectedFileNames(AgwHostProfile profile, string fileName)
    {
        using var configuration = CreateConfiguration(new() { ["AgwDataDir"] = Path.Combine(_directory, fileName) });
        var paths = CreatePaths(configuration);

        var (information, _) = WriteMarkers(configuration, paths, profile);

        Assert.Equal(Path.Combine(_directory, fileName, "logs"), paths.LogsDirectory);
        Assert.Contains(information, ReadLogs(paths.LogsDirectory, $"application-{fileName}-*.log"));
        Assert.Contains(information, ReadLogs(paths.LogsDirectory, "agw-info-*.log"));
        Assert.Single(Directory.GetFiles(paths.LogsDirectory, "application-*.log"));
    }

    [Fact]
    public void CreateLogger_ConfiguredLevelsAndFormats_PreservesSettings()
    {
        using var configuration = CreateConfiguration(
            new()
            {
                ["Serilog:MinimumLevel:Default"] = "Warning",
                [$"{InfoSink}:Args:outputTemplate"] = "{Level:u3}|{Message:lj}{NewLine}",
            }
        );
        var paths = CreatePaths(configuration);
        var marker = Guid.NewGuid().ToString("N");

        using (var logger = AgwLoggerFactory.CreateLogger(configuration, paths, AgwHostProfile.Standalone))
        {
            logger.Debug("debug-{Marker}", marker);
            logger.Information("information-{Marker}", marker);
            logger.Warning("warning-{Marker}", marker);
            logger.Error("error-{Marker}", marker);
        }

        Assert.Equal(
            new[] { $"WRN|warning-{marker}", $"ERR|error-{marker}" },
            ReadLines(paths.LogsDirectory, "agw-info-*.log")
        );
        var errors = ReadLogs(paths.LogsDirectory, "agw-error-*.log");
        Assert.Contains("[ERR] |", errors);
        Assert.Contains($"error-{marker}", errors);
        Assert.DoesNotContain($"warning-{marker}", errors);
        var host = ReadLogs(paths.LogsDirectory, "application-standalone-*.log");
        Assert.Contains($"warning-{marker}", host);
        Assert.DoesNotContain($"information-{marker}", host);
    }

    [Fact]
    public void CreateLogger_NamedSinkOverrides_UsesEffectiveSettings()
    {
        // 环境专用 JSON 中 Error 写在 Info 之前，并覆盖两个内置 sink 的参数；命令行再覆盖 Info 的格式。
        // The environment JSON lists Error before Info and overrides both built-in sinks; the command line then overrides Info.
        Directory.CreateDirectory(_directory);
        var environmentJson = Path.Combine(_directory, "appsettings.Test.json");
        File.WriteAllText(
            environmentJson,
            """
            {
              "Serilog": {
                "WriteTo": {
                  "Files": {
                    "Args": {
                      "configure": {
                        "Error": { "Args": { "outputTemplate": "ENV-ERROR {Message:lj}{NewLine}" } },
                        "Info": { "Args": { "path": "env-info-.log", "outputTemplate": "ENV-INFO {Message:lj}{NewLine}" } }
                      }
                    }
                  }
                }
              }
            }
            """
        );
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        configuration.AddJsonFile(environmentJson);
        configuration.AddCommandLine([
            $"--AgwDataDir={Path.Combine(_directory, "data")}",
            $"--{InfoSink}:Args:outputTemplate=CLI-INFO {{Message:lj}}{{NewLine}}",
        ]);
        var paths = CreatePaths(configuration);

        var (information, error) = WriteMarkers(configuration, paths, AgwHostProfile.Standalone);

        Assert.Empty(Directory.GetFiles(paths.LogsDirectory, "agw-info-*.log"));
        Assert.Equal(
            new[] { $"CLI-INFO {information}", $"CLI-INFO {error}" },
            ReadLines(paths.LogsDirectory, "env-info-*.log")
        );
        Assert.Equal(new[] { $"ENV-ERROR {error}" }, ReadLines(paths.LogsDirectory, "agw-error-*.log"));
        Assert.Equal(Path.Combine(paths.LogsDirectory, "env-info-.log"), configuration[$"{InfoSink}:Args:path"]);
    }

    [Fact]
    public void CreateLogger_AdditionalFileSink_PreservesConfiguredPath()
    {
        var topLevel = Path.Combine(_directory, "additional", "top-level.log");
        var nested = Path.Combine(_directory, "additional", "nested.log");
        using var configuration = CreateConfiguration(
            new()
            {
                ["Serilog:WriteTo:Additional:Name"] = "File",
                ["Serilog:WriteTo:Additional:Args:path"] = topLevel,
                ["Serilog:WriteTo:Files:Args:configure:Audit:Name"] = "File",
                ["Serilog:WriteTo:Files:Args:configure:Audit:Args:path"] = nested,
            }
        );
        var paths = CreatePaths(configuration);

        var (information, _) = WriteMarkers(configuration, paths, AgwHostProfile.Standalone);

        Assert.Contains(information, File.ReadAllText(topLevel));
        Assert.Contains(information, File.ReadAllText(nested));
        Assert.Equal(topLevel, configuration["Serilog:WriteTo:Additional:Args:path"]);
        Assert.Equal(nested, configuration["Serilog:WriteTo:Files:Args:configure:Audit:Args:path"]);
        Assert.Contains(information, ReadLogs(paths.LogsDirectory, "agw-info-*.log"));
    }

    public static TheoryData<string, string> InvalidFileNames()
    {
        var absolute = Path.Combine(AppContext.BaseDirectory, "agw-info-.log");
        return new()
        {
            { InfoSink, absolute },
            { InfoSink, "logs/agw-info-.log" },
            { InfoSink, "logs\\agw-info-.log" },
            { InfoSink, "." },
            { InfoSink, ".." },
            { InfoSink, "" },
            { InfoSink, " " },
            { ErrorSink, "../agw-error-.log" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidFileNames))]
    public void CreateLogger_InvalidBuiltInFileName_ThrowsInvalidParam(string sink, string path)
    {
        using var configuration = CreateConfiguration(new() { [$"{sink}:Args:path"] = path });
        var paths = CreatePaths(configuration);

        var exception = Assert.Throws<AgwException>(() =>
            AgwLoggerFactory.CreateLogger(configuration, paths, AgwHostProfile.Standalone)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains($"'{sink}:Args:path'", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.LogsDirectory));
    }

    [Theory]
    [InlineData("Serilog:WriteTo:0:Name", "Console", "Serilog:WriteTo:0")]
    [InlineData("Serilog:WriteTo:Files:Args:configure:1:Name", "File", "Serilog:WriteTo:Files:Args:configure:1")]
    public void CreateLogger_NumericSinkKeys_ThrowsInvalidParam(string key, string value, string expected)
    {
        using var configuration = CreateConfiguration(new() { [key] = value });
        var paths = CreatePaths(configuration);

        var exception = Assert.Throws<AgwException>(() =>
            AgwLoggerFactory.CreateLogger(configuration, paths, AgwHostProfile.Standalone)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains($"'{expected}'", exception.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.LogsDirectory));
    }

    [Theory]
    [InlineData("Serilog:WriteTo:Files:Name", "File")]
    [InlineData($"{InfoSink}:Name", "Console")]
    [InlineData($"{ErrorSink}:Name", "")]
    public void CreateLogger_BuiltInSinkNameChanged_ThrowsInvalidParam(string key, string value)
    {
        using var configuration = CreateConfiguration(new() { [key] = value });
        var paths = CreatePaths(configuration);

        var exception = Assert.Throws<AgwException>(() =>
            AgwLoggerFactory.CreateLogger(configuration, paths, AgwHostProfile.Standalone)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Contains($"'{key}'", exception.Message);
    }

    [Fact]
    public void CreateLogger_RollsAndRetainsFiles()
    {
        // Info 保留全部轮转文件以检查事件完整；Error 只保留两个文件以检查保留窗口。
        // Info keeps every rolled file to check completeness; Error keeps two files to check the retention window.
        using var configuration = CreateConfiguration(
            new()
            {
                [$"{InfoSink}:Args:fileSizeLimitBytes"] = "1024",
                [$"{InfoSink}:Args:retainedFileCountLimit"] = "1000",
                [$"{InfoSink}:Args:outputTemplate"] = "{Message:lj}{NewLine}",
                [$"{ErrorSink}:Args:fileSizeLimitBytes"] = "1024",
                [$"{ErrorSink}:Args:retainedFileCountLimit"] = "2",
                [$"{ErrorSink}:Args:outputTemplate"] = "{Message:lj}{NewLine}",
            }
        );
        var paths = CreatePaths(configuration);
        const int count = 40;
        var padding = new string('x', 200);

        using (var logger = AgwLoggerFactory.CreateLogger(configuration, paths, AgwHostProfile.Standalone))
        {
            for (var sequence = 0; sequence < count; sequence++)
                logger.Error("event {Sequence} {Padding}", sequence, padding);
        }

        var infoFiles = Directory.GetFiles(paths.LogsDirectory, "agw-info-*.log");
        Assert.True(infoFiles.Length > 2, $"Expected rolled files, found {infoFiles.Length}.");
        Assert.Equal(Enumerable.Range(0, count), ReadSequences(infoFiles, padding).Order());
        var errorFiles = Directory.GetFiles(paths.LogsDirectory, "agw-error-*.log");
        Assert.Equal(2, errorFiles.Length);
        var retained = ReadSequences(errorFiles, padding).Order().ToArray();
        Assert.NotEmpty(retained);
        Assert.Equal(Enumerable.Range(count - retained.Length, retained.Length), retained);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private ConfigurationManager CreateConfiguration(Dictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["AgwDataDir"] = Path.Combine(_directory, "data") }
        );
        if (values is not null)
            configuration.AddInMemoryCollection(values);
        return configuration;
    }

    private static AgwDataPaths CreatePaths(IConfiguration configuration)
    {
        var paths = AgwDataPaths.ResolveFromConfiguration(configuration);
        paths.EnsureCreated();
        return paths;
    }

    private static (string Information, string Error) WriteMarkers(
        ConfigurationManager configuration,
        AgwDataPaths paths,
        AgwHostProfile profile
    )
    {
        var information = $"information-{Guid.NewGuid():N}";
        var error = $"error-{Guid.NewGuid():N}";
        using var logger = AgwLoggerFactory.CreateLogger(configuration, paths, profile);
        logger.Information("{Marker}", information);
        logger.Error("{Marker}", error);
        return (information, error);
    }

    private static string ReadLogs(string directory, string pattern)
    {
        var files = Directory.GetFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        return string.Concat(files.Select(File.ReadAllText));
    }

    private static string[] ReadLines(string directory, string pattern)
    {
        return ReadLogs(directory, pattern).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<int> ReadSequences(IEnumerable<string> files, string padding)
    {
        foreach (var line in files.SelectMany(File.ReadAllLines))
        {
            var parts = line.Split(' ');
            Assert.Equal(3, parts.Length);
            Assert.Equal("event", parts[0]);
            Assert.Equal(padding, parts[2]);
            yield return int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
