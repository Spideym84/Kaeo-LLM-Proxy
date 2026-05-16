namespace Kaeo.LlmProxy.Core.Models;

internal enum RequestStatus
{
    Success,
    Error,
    Cancelled,
}

/// <summary>A single logged proxy request with timing and token stats.</summary>
internal sealed class RequestLog
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Method { get; set; } = string.Empty;
    public string OllamaPath { get; set; } = string.Empty;
    public string UpstreamPath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Streaming { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Success;
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; }
    public double DurationMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double TokensPerSecond { get; set; }

    /// <summary>
    /// When set, references the <see cref="ExceptionDetail.Id"/> stored in the exceptions
    /// collection for the full stack trace and inner exception chain.
    /// </summary>
    public int? ExceptionId { get; set; }

    /// <summary>
    /// Raw request body captured when <c>CollectRequestDetails</c> is enabled in settings.
    /// Null when capture is disabled.
    /// </summary>
    public string? RequestBody { get; set; }

    /// <summary>
    /// Assembled LLM response text captured when <c>CollectResponseDetails</c> is enabled in settings.
    /// For streaming responses this is the full text accumulated across all chunks.
    /// Null when capture is disabled.
    /// </summary>
    public string? ResponseBody { get; set; }
}
