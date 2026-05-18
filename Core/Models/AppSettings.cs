using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kaeo.LlmProxy.Core.Models;

/// <summary>The upstream server type this mapping targets.</summary>
internal enum UpstreamType
{
    LlamaCpp,
}

/// <summary>Maps an Ollama model name to a specific upstream server and llama.cpp model name.</summary>
internal sealed class ModelMapping
{
    public string OllamaName { get; set; } = string.Empty;
    public string LlamaCppName { get; set; } = string.Empty;
    public bool EnableThinkingCompatibility { get; set; } = true;

    /// <summary>Upstream backend for this mapping. Only LlamaCpp is supported currently.</summary>
    public UpstreamType UpstreamType { get; set; } = UpstreamType.LlamaCpp;

    /// <summary>
    /// Upstream base URL for this mapping (e.g. "http://192.168.1.10:8080"). Required.
    /// Each mapping must specify its own upstream server.
    /// </summary>
    public string UpstreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Request timeout in seconds for this mapping. Default: 300 seconds if not specified or zero.
    /// </summary>
    public int UpstreamTimeoutSeconds { get; set; } = 300;
}

/// <summary>Logging configuration persisted inside settings.jsonc.</summary>
internal sealed class LoggingSettings
{
    /// <summary>Minimum Serilog level: Verbose, Debug, Information, Warning, Error, Fatal. Min: Verbose, Max: Fatal.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Maximum size in MB of a single app-log file before rolling. Min: 1, Max: 1000.</summary>
    public int AppLogFileSizeLimitMb { get; set; } = 10;

    /// <summary>Number of rolled app-log files to retain (oldest deleted first). Min: 1, Max: 999.</summary>
    public int AppLogRetainedFileCount { get; set; } = 7;

    /// <summary>Maximum size in MB of a single LiteDB request-log file before archiving. Min: 1, Max: 5000.</summary>
    public int RequestLogFileSizeLimitMb { get; set; } = 50;

    /// <summary>
    /// How long to retain request log entries before they are automatically deleted.
    /// Set to 0 to keep entries forever. Default: 72 hours (3 days).
    /// </summary>
    public int LogRetentionHours { get; set; } = 72;

    /// <summary>Root directory for all log output. Relative to executable directory for portable deployment.</summary>
    public string LogDirectory { get; set; } = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Data", "logs");
}

/// <summary>Persisted application settings.</summary>
internal sealed class AppSettings
{
    private static readonly string _settingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Data",
        "settings.jsonc");

    // Read: allow // and /* */ comments so the annotated template remains valid.
    private static readonly JsonSerializerOptions _readOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    // Write: indented JSON used when serialising back (comments stripped, that is fine).
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Port this proxy listens on (Ollama default: 11434). Min: 1, Max: 65535.</summary>
    public int ListenPort { get; set; } = 11434;

    /// <summary>
    /// IP address to bind the listener to. Use "localhost" (127.0.0.1), "0.0.0.0" (all interfaces), 
    /// or a specific IP address. Note: Binding to "0.0.0.0" or specific IPs may require admin rights 
    /// or netsh urlacl reservation. Default: "localhost".
    /// </summary>
    public string ListenAddress { get; set; } = "localhost";

    /// <summary>Model name mappings: Each Ollama model name maps to a specific upstream server and llama.cpp model name.</summary>
    public List<ModelMapping> ModelMappings { get; set; } = [];

    /// <summary>Maximum number of log entries to keep in memory. Min: 10, Max: 100000.</summary>
    public int MaxLogEntries { get; set; } = 500;

    /// <summary>Automatically start the proxy when the application launches. Default: true.</summary>
    public bool AutoStartProxy { get; set; } = true;

    /// <summary>Open the dashboard window on startup instead of starting minimised to tray. Default: false.</summary>
    public bool StartWithDashboardOpen { get; set; } = false;

    /// <summary>
    /// When true, allows more than one instance of the application to run simultaneously.
    /// By default only a single instance is permitted; attempting to launch a second instance
    /// will display a message and exit. Advanced users may set this to true when running
    /// multiple proxy configurations side-by-side. Default: false.
    /// </summary>
    public bool AllowMultipleInstances { get; set; } = false;

    /// <summary>
    /// When true, show a notification dialog the first time the main window is closed to the tray.
    /// Users can disable it from that dialog. Default: true.
    /// </summary>
    public bool ShowCloseToTrayNotification { get; set; } = true;

    /// <summary>
    /// When true, the raw request body is captured into each <see cref="RequestLog"/> entry.
    /// Useful for debugging but increases memory and storage usage. Default: false.
    /// </summary>
    public bool CollectRequestDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>
    /// When true, the assembled LLM response text is captured into each <see cref="RequestLog"/> entry.
    /// For streaming responses this accumulates all chunks into a single string.
    /// Useful for debugging but increases memory and storage usage. Default: false.
    /// </summary>
    public bool CollectResponseDetails { get; set; } =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>Logging configuration.</summary>
    public LoggingSettings Logging { get; set; } = new();

    public static AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            AppSettings defaults = new();
            defaults.CreateDefaultFile();
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, _readOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        string dir = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(this, _writeOptions));
    }

    /// <summary>
    /// Resolves a requested model name to the llama.cpp model name.
    /// Returns the mapped llama.cpp name if found, otherwise returns the original name unchanged.
    /// </summary>
    public string ResolveModelName(string requestedModel)
    {
        foreach (ModelMapping mapping in ModelMappings)
        {
            if (string.Equals(mapping.OllamaName, requestedModel, StringComparison.OrdinalIgnoreCase))
                return mapping.LlamaCppName;
        }

        return requestedModel;
    }

    /// <summary>
    /// Finds a model mapping by either the exposed Ollama name or the mapped llama.cpp model name.
    /// Returns null when no configured mapping matches.
    /// </summary>
    public ModelMapping? FindModelMapping(string requestedModel)
    {
        foreach (ModelMapping mapping in ModelMappings)
        {
            if (string.Equals(mapping.OllamaName, requestedModel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mapping.LlamaCppName, requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                return mapping;
            }
        }

        return null;
    }

    private static string JsBool(bool value) => value ? "true" : "false";

    /// <summary>Writes the annotated default config template to disk on first run.</summary>
    private void CreateDefaultFile()
    {
        string dir = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(dir);

        string logDir = Logging.LogDirectory.Replace("\\", "\\\\");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  // \u2500\u2500\u2500 Proxy \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        sb.AppendLine();
        sb.AppendLine("  // Port the proxy listens on (Ollama clients connect here).");
        sb.AppendLine("  // Min: 1  Max: 65535  Default: 11434");
        sb.AppendLine($"  \"ListenPort\": {ListenPort},");
        sb.AppendLine();
        sb.AppendLine("  // IP address to bind the listener to.");
        sb.AppendLine("  // Values: \"localhost\" (127.0.0.1 only), \"0.0.0.0\" (all interfaces), or a specific IP.");
        sb.AppendLine("  // Note: Binding to \"0.0.0.0\" or specific IPs may require admin rights or netsh urlacl.");
        sb.AppendLine("  // Default: \"localhost\"");
        sb.AppendLine($"  \"ListenAddress\": \"{ListenAddress}\",");

        sb.AppendLine();
        sb.AppendLine("  // Max recent request log entries kept in memory for the GUI.");
        sb.AppendLine("  // Min: 10  Max: 100000  Default: 500");
        sb.AppendLine($"  \"MaxLogEntries\": {MaxLogEntries},");

        sb.AppendLine();
        sb.AppendLine("  // Automatically start the proxy when the application launches.");
        sb.AppendLine("  // Default: true");
        sb.AppendLine($"  \"AutoStartProxy\": {JsBool(AutoStartProxy)},");
        sb.AppendLine();
        sb.AppendLine("  // Open the dashboard window immediately on startup instead of sitting silently in the tray.");
        sb.AppendLine("  // Default: false");
        sb.AppendLine($"  \"StartWithDashboardOpen\": {JsBool(StartWithDashboardOpen)},");
        sb.AppendLine();
        sb.AppendLine("  // Show a reminder when closing the dashboard window that the app continues running in the notification area.");
        sb.AppendLine("  // Can be disabled from the reminder dialog. Default: true");
        sb.AppendLine($"  \"ShowCloseToTrayNotification\": {JsBool(ShowCloseToTrayNotification)},");
        sb.AppendLine();
        sb.AppendLine("  // Capture the raw request body in each log entry for debugging.");
        sb.AppendLine("  // Increases memory and DB storage usage. Default: false");
        sb.AppendLine($"  \"CollectRequestDetails\": {JsBool(CollectRequestDetails)},");
        sb.AppendLine();
        sb.AppendLine("  // Capture the full LLM response text in each log entry for debugging.");
        sb.AppendLine("  // For streaming responses, accumulates all chunks into a single string.");
        sb.AppendLine("  // Increases memory and DB storage usage. Default: false");
        sb.AppendLine($"  \"CollectResponseDetails\": {JsBool(CollectResponseDetails)},");
        sb.AppendLine();
        sb.AppendLine("  // \u2500\u2500\u2500 Model Name Mappings \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        sb.AppendLine("  // Each entry maps an Ollama model name to a specific upstream server and llama.cpp model.");
        sb.AppendLine("  // Each mapping MUST specify its own UpstreamUrl.");
        sb.AppendLine("  // EnableThinkingCompatibility strips assistant response-prefill turns for models that reject them when thinking is enabled.");
        sb.AppendLine("  // UpstreamTimeoutSeconds: defaults to 300 if not specified or zero.");
        sb.AppendLine("  // Example:");
        sb.AppendLine("  //   { \"OllamaName\": \"llama3\", \"LlamaCppName\": \"llama-3-8b\", \"EnableThinkingCompatibility\": true,");
        sb.AppendLine("  //     \"UpstreamUrl\": \"http://192.168.1.10:8080\", \"UpstreamTimeoutSeconds\": 120 }");
        sb.AppendLine("  \"ModelMappings\": [],");
        sb.AppendLine();
        sb.AppendLine("  // \u2500\u2500\u2500 Logging \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500");
        sb.AppendLine("  \"Logging\": {");
        sb.AppendLine();
        sb.AppendLine("    // Minimum Serilog severity level written to the app log.");
        sb.AppendLine("    // Values: Verbose | Debug | Information | Warning | Error | Fatal");
        sb.AppendLine($"    \"MinimumLevel\": \"{Logging.MinimumLevel}\",");
        sb.AppendLine();
        sb.AppendLine("    // Roll the app log file when it reaches this size (MB).");
        sb.AppendLine("    // Min: 1  Max: 1000  Default: 10");
        sb.AppendLine($"    \"AppLogFileSizeLimitMb\": {Logging.AppLogFileSizeLimitMb},");
        sb.AppendLine();
        sb.AppendLine("    // How many rolled app log files to keep before deleting the oldest.");
        sb.AppendLine("    // Min: 1  Max: 999  Default: 7");
        sb.AppendLine($"    \"AppLogRetainedFileCount\": {Logging.AppLogRetainedFileCount},");
        sb.AppendLine();
        sb.AppendLine("    // Archive the LiteDB request-log database when it reaches this size (MB).");
        sb.AppendLine("    // Min: 1  Max: 5000  Default: 50");
        sb.AppendLine($"    \"RequestLogFileSizeLimitMb\": {Logging.RequestLogFileSizeLimitMb},");
        sb.AppendLine();
        sb.AppendLine("    // How long to keep request log entries before automatic deletion.");
        sb.AppendLine("    // Set to 0 to retain forever. Default: 72 (3 days).");
        sb.AppendLine($"    \"LogRetentionHours\": {Logging.LogRetentionHours},");
        sb.AppendLine();
        sb.AppendLine("    // Root directory for logs. App logs go in /app/, request DB in /requests/.");
        sb.AppendLine($"    \"LogDirectory\": \"{logDir}\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        File.WriteAllText(_settingsPath, sb.ToString());
    }
}
