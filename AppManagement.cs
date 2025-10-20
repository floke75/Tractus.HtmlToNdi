using Serilog;
using Serilog.Core;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Tractus.HtmlToNdi;

/// <summary>
/// Provides application-level management functions, such as logging and data directory access.
/// </summary>
public static class AppManagement
{
    /// <summary>
    /// Gets or sets a value indicating whether this is the first run of the application.
    /// </summary>
    public static bool IsFirstRun { get; set; }

    /// <summary>
    /// Gets or sets the logging level switch.
    /// </summary>
    public static LoggingLevelSwitch LoggingLevel { get; set; } = new LoggingLevelSwitch();

    /// <summary>
    /// Gets the application's data directory.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }

    /// <summary>
    /// Deletes a file from the data directory.
    /// </summary>
    /// <param name="fileName">The name of the file to delete.</param>
    public static void DeleteFileFromDataDirectory(string fileName)
    {
        var path = Path.Combine(DataDirectory, fileName);

        File.Delete(path);
    }

    /// <summary>
    /// Checks if a file exists in the data directory.
    /// </summary>
    /// <param name="fileName">The name of the file to check.</param>
    /// <returns>True if the file exists; otherwise, false.</returns>
    public static bool FileExistsInDataDirectory(string fileName)
    {
        return File.Exists(Path.Combine(DataDirectory, fileName));
    }

    /// <summary>
    /// Reads all lines from a file in the data directory.
    /// </summary>
    /// <param name="fileName">The name of the file to read.</param>
    /// <returns>An array of strings containing all lines of the file.</returns>
    public static string[] ReadFileFromDataDirectory(string fileName)
    {
        var path = Path.Combine(DataDirectory, fileName);

        return File.ReadAllLines(path);
    }

    /// <summary>
    /// Reads all text from a file in the data directory.
    /// </summary>
    /// <param name="fileName">The name of the file to read.</param>
    /// <returns>A string containing all text of the file.</returns>
    public static string ReadTextFromDataDirectory(string fileName)
    {
        var path = Path.Combine(DataDirectory, fileName);

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Writes all lines to a file in the data directory.
    /// </summary>
    /// <param name="fileName">The name of the file to write to.</param>
    /// <param name="lines">The lines to write to the file.</param>
    public static void WriteFileToDataDirectory(string fileName, string[] lines)
    {
        var path = Path.Combine(DataDirectory, fileName);

        File.WriteAllLines(path, lines);
    }

    /// <summary>
    /// Writes all text to a file in the data directory.
    /// </summary>
    /// <param name="fileName">The name of the file to write to.</param>
    /// <param name="content">The content to write to the file.</param>
    public static void WriteFileToDataDirectory(string fileName, string content)
    {
        var path = Path.Combine(DataDirectory, fileName);

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Gets the name of the application.
    /// </summary>
    public static string AppName => Assembly.GetEntryAssembly()?.GetName()?.Name ?? "App Name Not Set";

    /// <summary>
    /// Gets the version of the application.
    /// </summary>
    public static string Version => Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";

    /// <summary>
    /// Gets the instance name of the application.
    /// </summary>
    public static string InstanceName
    {
        get
        {
            var machineName = Environment.MachineName;

            var osPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "windows"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "macos"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "linux"
                : "other";

            var bitness = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? "x86_64"
                : RuntimeInformation.ProcessArchitecture == Architecture.X86
                ? "x86_32"
                : RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "arm64"
                : RuntimeInformation.ProcessArchitecture == Architecture.Arm
                ? "arm"
                : RuntimeInformation.ProcessArchitecture.ToString();

            return $"{osPlatform}_{bitness}_{machineName}";
        }
    }


    /// <summary>
    /// Initializes the application management services.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    public static void Initialize(string[] args)
    {
        if (!Directory.Exists(AppManagement.DataDirectory))
        {
            IsFirstRun = true;
            Directory.CreateDirectory(DataDirectory);
        }

        var currentDomain = AppDomain.CurrentDomain;
        currentDomain.UnhandledException += OnAppDomainUnhandledException;

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LoggingLevel);

        var isDebugMode = args.Any(x => x.Equals("-debug"));

        if (isDebugMode)
        {
            LoggingLevel.MinimumLevel = Serilog.Events.LogEventLevel.Debug;
        }

        var quietMode = args.Any(x => x.Equals("-quiet"));

        if (!quietMode)
        {
            loggerConfig = loggerConfig.WriteTo.Console();
        }

        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        loggerConfig = loggerConfig.WriteTo.File(System.IO.Path.Combine(
                documentsPath,
                $"{AppName}_log.txt"), rollingInterval: RollingInterval.Day);

        Log.Logger = loggerConfig.CreateLogger();

        Log.Information($"{AppName} starting up.");
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = (Exception)e.ExceptionObject;

        Log.Error("Unhandled exception in appdomain: {@ex}", exception);
        if (e.IsTerminating)
        {
            Log.Error("Runtime is terminating. Fatal exception.");
        }
        else
        {
            Log.Error("Runtime is not terminating.");
        }
    }
}
