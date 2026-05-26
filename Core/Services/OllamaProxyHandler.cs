using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Kaeo.LlmProxy.Core.Models;

namespace Kaeo.LlmProxy.Core.Services;

/// <summary>
/// Handles translation between Ollama API requests and llama.cpp OpenAI-compatible API requests.
/// Supports streaming, non-streaming, tool calls, JSON format mode, and batch embeddings.
/// </summary>
internal sealed class OllamaProxyHandler(AppSettings settings, StatisticsService stats) : IDisposable
{
    private const string RedactedBodyText = "[REDACTED BY MODEL LOG REDACTION SETTINGS]";
    private const string RedactedValueText = "[REDACTED]";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private AppSettings _settings = settings;

    // Shared pooled HttpClient — avoids socket exhaustion under load.
    private HttpClient _httpClient = BuildHttpClient(settings);

    private readonly StatisticsService _stats = stats;

    public void Dispose() => _httpClient.Dispose();

    /// <summary>Called from the Settings UI after the user saves new settings.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        HttpClient old = _httpClient;
        _httpClient = BuildHttpClient(settings);
        old.Dispose();
    }

    /// <summary>
    /// Returns the (baseUrl, timeoutSeconds) to use for a given Ollama model name.
    /// Requires each mapping to have its own upstream URL configured.
    /// If ollamaModel is null or empty and there's at least one mapping configured,
    /// returns the first mapping's upstream URL as a fallback.
    /// </summary>
    private (string BaseUrl, int TimeoutSeconds) ResolveUpstream(string ollamaModel)
    {
        // Try exact match first
        foreach (ModelMapping m in _settings.ModelMappings)
        {
            if (string.Equals(m.ProxyName, ollamaModel, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(m.UpstreamUrl))
                    throw new InvalidOperationException(
                        $"Model mapping '{m.ProxyName}' has no upstream URL configured. " +
                        "Each mapping must specify its own UpstreamUrl.");

                int timeout = m.UpstreamTimeoutSeconds > 0 ? m.UpstreamTimeoutSeconds : 300;
                return (m.UpstreamUrl.TrimEnd('/'), timeout);
            }
        }

        // Fallback: if model name is empty/null and we have at least one mapping,
        // use the first configured mapping's upstream URL (common for single-model setups)
        if (string.IsNullOrWhiteSpace(ollamaModel) && _settings.ModelMappings.Count > 0)
        {
            ModelMapping fallback = _settings.ModelMappings[0];
            if (string.IsNullOrWhiteSpace(fallback.UpstreamUrl))
                throw new InvalidOperationException(
                    $"Model mapping '{fallback.ProxyName}' has no upstream URL configured. " +
                    "Each mapping must specify its own UpstreamUrl.");

            int timeout = fallback.UpstreamTimeoutSeconds > 0 ? fallback.UpstreamTimeoutSeconds : 300;
            return (fallback.UpstreamUrl.TrimEnd('/'), timeout);
        }

        throw new InvalidOperationException(
            $"No mapping found for model '{ollamaModel}'. " +
            "Add a mapping in settings with ProxyName, ModelName, and UpstreamUrl.");
    }

    private bool ShouldApplyThinkingCompatibility(string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableThinkingCompatibility ?? true;
    }

    /// <summary>
    /// Returns whether heartbeats should be emitted for the given model, combining the
    /// global toggle with the per-mapping <see cref="ModelMapping.EnableHeartbeats"/> flag.
    /// </summary>
    private bool ShouldEmitHeartbeats(string modelName)
    {
        if (!_settings.EnableStreamingHeartbeats) return false;
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableHeartbeats ?? true;
    }

    /// <summary>
    /// Checks if the upstream error response indicates a context size overflow.
    /// Returns true if the error message contains "context" and "exceeded" or similar patterns.
    /// </summary>
    private static async Task<bool> IsContextOverflowErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return false;

        if ((int)response.StatusCode != 500)
            return false;

        try
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return false;

            // Try to parse as structured error
            LlamaCppErrorResponse? errorResp = JsonSerializer.Deserialize<LlamaCppErrorResponse>(body, _jsonOptions);
            string? errorMessage = errorResp?.Error?.Message;

            if (string.IsNullOrWhiteSpace(errorMessage))
                errorMessage = body;

            // Check for common context overflow patterns
            return errorMessage.Contains("context", StringComparison.OrdinalIgnoreCase)
                && (errorMessage.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("too large", StringComparison.OrdinalIgnoreCase)
                 || errorMessage.Contains("too long", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends <paramref name="req"/> to the resolved upstream URL, enforcing the per-mapping timeout
    /// via a linked <see cref="CancellationTokenSource"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendUpstreamAsync(
        HttpRequestMessage req,
        string baseUrl,
        int timeoutSeconds,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        // Build absolute URI from base + relative path already set on req
        req.RequestUri = new Uri(new Uri(baseUrl + "/"), req.RequestUri!.ToString().TrimStart('/'));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        return await _httpClient.SendAsync(req, completionOption, cts.Token);
    }

    private static HttpClient BuildHttpClient(AppSettings _) =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64,
        })
        {
            // Timeout is managed per-request via a linked CancellationTokenSource
            // so that individual model mappings can have different timeouts.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest req = context.Request;
        HttpListenerResponse resp = context.Response;

        string path = req.Url?.AbsolutePath ?? "/";
        string method = req.HttpMethod;

        var log = new RequestLog
        {
            Method = method,
            OllamaPath = path,
        };

        var sw = Stopwatch.StartNew();

        // Handle CORS preflight and static version probe without logging — they are infrastructure
        // noise and would inflate the request log on every client connection.
        resp.AddHeader("Access-Control-Allow-Origin", "*");
        resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

        if (method == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        if (method == "GET" && path == "/api/version")
        {
            await WriteJsonAsync(resp, new { version = "0.1.0" }, ct);
            return;
        }

        bool exceptionLogged = false;
        try
        {
            if (method == "GET" && path == "/api/tags")
            {
                log.UpstreamPath = "/v1/models";
                await HandleTagsAsync(resp, log, ct);
            }
            else if (method == "GET" && path == "/api/ps")
            {
                await HandlePsAsync(resp, log, ct);
            }
            else if (method == "POST" && path == "/api/show")
            {
                log.UpstreamPath = "/v1/models/{model}";
                await HandleShowAsync(req, resp, log, ct);
            }
            else if (method == "POST" && path == "/api/generate")
            {
                log.UpstreamPath = "/v1/completions";
                await HandleGenerateAsync(req, resp, log, ct);
            }
            else if (method == "POST" && path == "/api/chat")
            {
                log.UpstreamPath = "/v1/chat/completions";
                await HandleChatAsync(req, resp, log, ct);
            }
            else if (method == "POST" && (path == "/api/embeddings" || path == "/api/embed"))
            {
                log.UpstreamPath = "/v1/embeddings";
                await HandleEmbeddingsAsync(req, resp, log, ct);
            }
            else if (path is "/api/pull" or "/api/push" or "/api/create" or "/api/copy" or "/api/delete")
            {
                log.Status = RequestStatus.Error;
                resp.StatusCode = 501;
                await WriteJsonAsync(resp,
                    new { error = $"'{path}' is not supported. llama.cpp has no model-management API." }, ct);
            }
            else if (path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
                  || path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
            {
                // Transparent passthrough — forward OpenAI-native requests (e.g. from VS Copilot,
                // OpenAI SDKs) directly to the upstream llama.cpp /v1/* surface unchanged.
                log.UpstreamPath = path;
                await PassthroughAsync(req, resp, log, ct);
            }
            else
            {
                resp.StatusCode = 404;
                await WriteJsonAsync(resp, new { error = $"Unknown endpoint: {path}" }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            log.Status = RequestStatus.Cancelled;
            try { resp.StatusCode = 499; resp.Close(); } catch { }
        }
        catch (Exception ex)
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = ex.Message;

            // Persist the full exception detail (stack trace, inner exceptions) separately.
            _stats.AddLog(log, ex);
            exceptionLogged = true;

            try
            {
                resp.StatusCode = 500;
                await WriteJsonAsync(resp, new { error = ex.Message }, ct);
            }
            catch { }

            // Skip the finally AddLog — we already logged above with the exception.
            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            return;
        }
        finally
        {
            sw.Stop();
            log.DurationMs = sw.Elapsed.TotalMilliseconds;
            if (!exceptionLogged)
                _stats.AddLog(log);
        }
    }

    // ── /v1/* → transparent passthrough ────────────────────────────────────

    /// <summary>
    /// Forwards any OpenAI-native /v1/* request verbatim to the upstream llama.cpp server
    /// and streams the response back. Handles both streaming (SSE) and non-streaming responses.
    /// For POST requests the "model" field in the JSON body is rewritten through the mapping
    /// table so that clients sending e.g. "gpt-4o" are transparently mapped to the loaded model.
    /// </summary>
    private async Task PassthroughAsync(
        HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        using var upstreamReq = new HttpRequestMessage
        {
            Method = new HttpMethod(req.HttpMethod),
            // RequestUri is set to relative path; SendUpstreamAsync will make it absolute.
            RequestUri = new Uri(req.Url!.PathAndQuery, UriKind.Relative),
        };

        // Copy request headers, skipping hop-by-hop headers the HttpClient manages itself.
        foreach (string? name in req.Headers.AllKeys)
        {
            if (name is null) continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
             || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;

            string value = req.Headers[name] ?? string.Empty;
            if (!upstreamReq.Headers.TryAddWithoutValidation(name, value))
                upstreamReq.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        // Track which original model was requested so we can resolve the upstream URL.
        string originalModel = string.Empty;

        if (req.HasEntityBody)
        {
            bool isJsonPost = req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                           && (req.ContentType?.StartsWith("application/json",
                               StringComparison.OrdinalIgnoreCase) ?? false);

            if (isJsonPost)
            {
                string bodyText = await new System.IO.StreamReader(req.InputStream).ReadToEndAsync(ct);
                log.RequestBytes = Encoding.UTF8.GetByteCount(bodyText);
                string rewritten = NormalizeRequestBody(bodyText, log, ShouldApplyThinkingCompatibility);
                originalModel = log.Model; // set by NormalizeRequestBody
                if (_settings.CollectRequestDetails)
                    log.RequestBody = RedactRequestBodyForLog(bodyText, originalModel);
                byte[] bodyBytes = System.Text.Encoding.UTF8.GetBytes(rewritten);
                upstreamReq.Content = new ByteArrayContent(bodyBytes);
                upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            }
            else
            {
                upstreamReq.Content = new StreamContent(req.InputStream);
                if (!string.IsNullOrEmpty(req.ContentType))
                    upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
            }
        }

        var (baseUrl, timeout) = ResolveUpstream(originalModel);
        HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            upstreamReq, baseUrl, timeout, HttpCompletionOption.ResponseHeadersRead, ct);

        log.StatusCode = (int)upstreamResp.StatusCode;
        resp.StatusCode = (int)upstreamResp.StatusCode;

        // Copy response headers
        foreach (var header in upstreamResp.Headers)
        {
            if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
            resp.Headers[header.Key] = string.Join(",", header.Value);
        }
        foreach (var header in upstreamResp.Content.Headers)
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            resp.Headers[header.Key] = string.Join(",", header.Value);
        }

        resp.SendChunked = true;
        resp.KeepAlive = true;

        if (!upstreamResp.IsSuccessStatusCode)
        {
            // Read error body so it can be logged before forwarding.
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";
            byte[] errorBytes = System.Text.Encoding.UTF8.GetBytes(errorBody);
            await resp.OutputStream.WriteAsync(errorBytes, ct);
            resp.OutputStream.Close();
            return;
        }

        await using Stream upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);

        bool isServerSentEvents = IsServerSentEventsResponse(upstreamResp);

        using CountingStream countingStream = new(resp.OutputStream);
        if (_settings.CollectResponseDetails)
        {
            using ResponseCaptureStream captureStream = new(countingStream);
            if (isServerSentEvents)
            {
                await CopyStreamWithSseHeartbeatsAsync(
                    upstreamStream,
                    captureStream,
                    ShouldEmitHeartbeats(originalModel),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(originalModel));
            }
            else
            {
                await upstreamStream.CopyToAsync(captureStream, ct);
            }

            log.ResponseBody = RedactResponseBodyForLog(captureStream.GetCapturedText(), originalModel);
        }
        else if (isServerSentEvents)
        {
            await CopyStreamWithSseHeartbeatsAsync(
                upstreamStream,
                countingStream,
                ShouldEmitHeartbeats(originalModel),
                _settings.StreamingHeartbeatIntervalSeconds,
                ct,
                () => _stats.IncrementHeartbeat(originalModel));
        }
        else
        {
            await upstreamStream.CopyToAsync(countingStream, ct);
        }

        log.ResponseBytes = countingStream.BytesWritten;

        resp.OutputStream.Close();

        log.Status = RequestStatus.Success;
    }

    private static bool IsServerSentEventsResponse(HttpResponseMessage response)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return response.Headers.TryGetValues("X-Accel-Buffering", out IEnumerable<string>? values)
            && values.Any(value => value.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CopyStreamWithSseHeartbeatsAsync(
        Stream source,
        Stream destination,
        bool enableHeartbeats,
        int heartbeatIntervalSeconds,
        CancellationToken ct,
        Action? onHeartbeatSent = null)
    {
        byte[] buffer = new byte[81920];
        byte[] heartbeatBytes = Encoding.UTF8.GetBytes(": kaeo-heartbeat\n\n");
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));

        while (!ct.IsCancellationRequested)
        {
            ValueTask<int> readValueTask = source.ReadAsync(buffer, ct);
            Task<int> readTask = readValueTask.AsTask();

            while (enableHeartbeats && !readTask.IsCompleted)
            {
                Task delayTask = Task.Delay(heartbeatInterval, ct);
                Task completed = await Task.WhenAny(readTask, delayTask);
                if (completed == readTask)
                    break;

                await destination.WriteAsync(heartbeatBytes, ct);
                await destination.FlushAsync(ct);
                onHeartbeatSent?.Invoke();
            }

            int bytesRead = await readTask;
            if (bytesRead == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await destination.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Normalises a JSON request body before forwarding to the upstream:
    /// <list type="bullet">
    ///   <item>Rewrites the "model" field through the mapping table.</item>
    ///   <item>Merges multiple consecutive leading system messages into a single one,
    ///         separated by a blank line, so that strict Jinja templates (e.g. Qwen3)
    ///         that only allow one system message do not raise an exception.</item>
    ///   <item>Removes a trailing assistant response-prefill message, because some upstreams reject it when thinking mode is enabled.</item>
    /// </list>
    /// Returns the original text unchanged if the body isn't valid JSON.
    /// </summary>
    private string NormalizeRequestBody(string json, RequestLog log, Func<string, bool>? shouldApplyThinkingCompatibility = null)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // Read model name for logging and rewriting.
            string original = root.TryGetProperty("model", out JsonElement modelEl)
                ? modelEl.GetString() ?? string.Empty
                : string.Empty;

            string resolved = _settings.ResolveModelName(original);
            log.Model = original;
            bool applyThinkingCompatibility = shouldApplyThinkingCompatibility?.Invoke(original) ?? true;
            string? injectedInstructions = GetInstructionTextForModel(original);

            // Check whether the messages array has consecutive leading system messages.
            bool hasConsecutiveSystemMessages = false;
            bool hasTrailingAssistantPrefill = false;
            bool shouldInjectInstructions = false;
            if (root.TryGetProperty("messages", out JsonElement messagesEl)
                && messagesEl.ValueKind == JsonValueKind.Array)
            {
                List<JsonElement> messages = [.. messagesEl.EnumerateArray()];
                shouldInjectInstructions = !string.IsNullOrWhiteSpace(injectedInstructions);

                int leadingSystem = 0;
                foreach (JsonElement msg in messages)
                {
                    if (msg.TryGetProperty("role", out JsonElement role)
                        && role.GetString()?.Equals("system", StringComparison.OrdinalIgnoreCase) == true)
                        leadingSystem++;
                    else
                        break;
                }

                hasConsecutiveSystemMessages = leadingSystem > 1;
                hasTrailingAssistantPrefill = applyThinkingCompatibility
                    && messages.Count > 0
                    && IsAssistantResponsePrefill(messages[^1]);
            }

            // Nothing to rewrite — return original text unchanged.
            if (string.Equals(original, resolved, StringComparison.Ordinal)
                && !hasConsecutiveSystemMessages
                && !hasTrailingAssistantPrefill
                && !shouldInjectInstructions)
                return json;

            using var ms = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

            writer.WriteStartObject();

            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.Name.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("model", resolved);
                }
                else if (prop.Name.Equals("messages", StringComparison.OrdinalIgnoreCase)
                      && prop.Value.ValueKind == JsonValueKind.Array
                      && (hasConsecutiveSystemMessages || hasTrailingAssistantPrefill || shouldInjectInstructions))
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();

                    List<JsonElement> messages = [.. prop.Value.EnumerateArray()];

                    // Collect and merge consecutive leading system message contents.
                    var systemParts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(injectedInstructions))
                        systemParts.Add(injectedInstructions);

                    bool merging = true;

                    for (int i = 0; i < messages.Count; i++)
                    {
                        JsonElement msg = messages[i];

                        if (hasTrailingAssistantPrefill && i == messages.Count - 1 && IsAssistantResponsePrefill(msg))
                            continue;

                        bool isSystem = merging
                            && msg.TryGetProperty("role", out JsonElement r)
                            && r.GetString()?.Equals("system", StringComparison.OrdinalIgnoreCase) == true;

                        if (isSystem)
                        {
                            string content = msg.TryGetProperty("content", out JsonElement c)
                                ? c.GetString() ?? string.Empty
                                : string.Empty;
                            systemParts.Add(content);
                        }
                        else
                        {
                            // Emit the merged system message once when we leave the system block.
                            if (merging && systemParts.Count > 0)
                            {
                                writer.WriteStartObject();
                                writer.WriteString("role", "system");
                                writer.WriteString("content", string.Join("\n\n", systemParts));
                                writer.WriteEndObject();
                                merging = false;
                            }

                            msg.WriteTo(writer);
                        }
                    }

                    // Edge case: all messages were system messages.
                    if (merging && systemParts.Count > 0)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", string.Join("\n\n", systemParts));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            // Non-JSON or malformed body — forward as-is.
            return json;
        }
    }

    private static bool IsAssistantResponsePrefill(JsonElement message)
    {
        if (!message.TryGetProperty("role", out JsonElement role)
            || !string.Equals(role.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array
            && toolCalls.GetArrayLength() > 0)
        {
            return false;
        }

        if (message.TryGetProperty("tool_call_id", out JsonElement toolCallId)
            && toolCallId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(toolCallId.GetString()))
        {
            return false;
        }

        return true;
    }

    private string? GetInstructionTextForModel(string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        InstructionSet? instructionSet = _settings.FindInstructionSet(mapping?.InstructionSetName);
        return string.IsNullOrWhiteSpace(instructionSet?.Instructions)
            ? null
            : instructionSet.Instructions;
    }

    private string RedactRequestBodyForLog(string body, string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        if (mapping?.RedactRequestBodies ?? true)
            return RedactedBodyText;

        return mapping?.RedactSensitiveJsonFields ?? true
            ? RedactSensitiveJsonFields(body)
            : body;
    }

    private string RedactResponseBodyForLog(string body, string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        if (mapping?.RedactResponseBodies ?? true)
            return RedactedBodyText;

        return mapping?.RedactSensitiveJsonFields ?? true
            ? RedactSensitiveJsonFields(body)
            : body;
    }

    private static string RedactSensitiveJsonFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return body;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            using MemoryStream ms = new();
            using Utf8JsonWriter writer = new(ms, new JsonWriterOptions { Indented = true });
            WriteRedactedJsonElement(writer, doc.RootElement);
            writer.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static void WriteRedactedJsonElement(Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (propertyName is not null && IsSensitiveJsonProperty(propertyName))
        {
            writer.WriteStringValue(RedactedValueText);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJsonElement(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                    WriteRedactedJsonElement(writer, item, propertyName);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveJsonProperty(string propertyName)
    {
        return propertyName.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apikey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("password", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("system", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("messages", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("input", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("content", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAssistantResponsePrefill(LlamaCppMessage message) =>
        string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
        && (message.ToolCalls is null || message.ToolCalls.Count == 0)
        && string.IsNullOrWhiteSpace(message.ToolCallId);

    // ── /api/tags → GET /v1/models ──────────────────────────────────────────

    private async Task HandleTagsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var (baseUrl, timeout) = ResolveUpstream(string.Empty);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        HttpResponseMessage upstreamResp = await SendUpstreamAsync(req, baseUrl, timeout, HttpCompletionOption.ResponseContentRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;

        string body = await upstreamResp.Content.ReadAsStringAsync(ct);
        LlamaCppModelsResponse? models = JsonSerializer.Deserialize<LlamaCppModelsResponse>(body, _jsonOptions);

        var tags = new OllamaTagsResponse
        {
            Models = [.. (models?.Data ?? []).Select(m => new OllamaModelEntry
            {
                Name = m.Id,
                Model = m.Id,
                ModifiedAt = DateTimeOffset.FromUnixTimeSeconds(m.Created).UtcDateTime.ToString("o"),
            })],
        };

        log.Status = upstreamResp.IsSuccessStatusCode ? RequestStatus.Success : RequestStatus.Error;
        await WriteJsonAsync(resp, tags, ct);
    }

    // ── /api/ps → running model stub ────────────────────────────────────────

    private async Task HandlePsAsync(HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        var (baseUrl, timeout) = ResolveUpstream(string.Empty);
        using var psReq = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        HttpResponseMessage upstreamResp = await SendUpstreamAsync(psReq, baseUrl, timeout, HttpCompletionOption.ResponseContentRead, ct);
        string body = await upstreamResp.Content.ReadAsStringAsync(ct);
        LlamaCppModelsResponse? models = JsonSerializer.Deserialize<LlamaCppModelsResponse>(body, _jsonOptions);

        var running = (models?.Data ?? []).Select(m => new
        {
            name = m.Id,
            model = m.Id,
            size = 0L,
            digest = string.Empty,
            details = new { family = m.Id },
            expires_at = DateTime.UtcNow.AddHours(1).ToString("o"),
            size_vram = 0L,
        }).ToList();

        log.Status = RequestStatus.Success;
        await WriteJsonAsync(resp, new { models = running }, ct);
    }

    // ── /api/show → GET /v1/models/{id} ────────────────────────────────────

    private async Task HandleShowAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        OllamaShowRequest? showReq = JsonSerializer.Deserialize<OllamaShowRequest>(body, _jsonOptions);
        string modelName = _settings.ResolveModelName(showReq?.Model ?? showReq?.Name ?? string.Empty);
        log.Model = modelName;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, showReq?.Model ?? showReq?.Name ?? string.Empty);

        var (showBase, showTimeout) = ResolveUpstream(showReq?.Model ?? showReq?.Name ?? string.Empty);
        using var showReqMsg = new HttpRequestMessage(HttpMethod.Get, $"/v1/models/{Uri.EscapeDataString(modelName)}");
        HttpResponseMessage upstreamResp = await SendUpstreamAsync(showReqMsg, showBase, showTimeout, HttpCompletionOption.ResponseContentRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;

        string upstreamBody = await upstreamResp.Content.ReadAsStringAsync(ct);
        LlamaCppModel? model = JsonSerializer.Deserialize<LlamaCppModel>(upstreamBody, _jsonOptions);

        var showResp = new OllamaShowResponse
        {
            Model = model?.Id ?? modelName,
            Details = new OllamaModelDetails { Family = modelName },
        };

        log.Status = upstreamResp.IsSuccessStatusCode ? RequestStatus.Success : RequestStatus.Error;
        await WriteJsonAsync(resp, showResp, ct);
    }

    // ── /api/generate → POST /v1/completions ───────────────────────────────

    private async Task HandleGenerateAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaGenerateRequest? ollamaReq = JsonSerializer.Deserialize<OllamaGenerateRequest>(body, _jsonOptions);
        ArgumentNullException.ThrowIfNull(ollamaReq);

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
        var (genBase, genTimeout) = ResolveUpstream(ollamaReq.Model);

        // Build the prompt, optionally injecting custom instructions
        string prompt = ollamaReq.Prompt;
        string? systemPrefix = ollamaReq.System;

        // Inject custom instructions if configured for this model mapping
        ModelMapping? mapping = _settings.FindModelMapping(ollamaReq.Model);
        if (mapping?.InstructionSetName is not null)
        {
            InstructionSet? instructionSet = _settings.FindInstructionSet(mapping.InstructionSetName);
            if (instructionSet is not null && !string.IsNullOrWhiteSpace(instructionSet.Instructions))
            {
                // Prepend custom instructions to the system prompt
                systemPrefix = string.IsNullOrEmpty(systemPrefix)
                    ? instructionSet.Instructions
                    : $"{instructionSet.Instructions}\n\n{systemPrefix}";
            }
        }

        // Combine system and user prompt
        if (!string.IsNullOrEmpty(systemPrefix))
            prompt = $"{systemPrefix}\n\n{prompt}";

        var llamaReq = new LlamaCppCompletionRequest
        {
            Model = resolvedModel,
            Prompt = prompt,
            Stream = ollamaReq.Stream,
            ResponseFormat = ResolveResponseFormat(ollamaReq.Format),
            Temperature = ollamaReq.Options?.Temperature,
            TopP = ollamaReq.Options?.TopP,
            MaxTokens = ollamaReq.Options?.NumPredict,
            Stop = ollamaReq.Options?.Stop,
            Seed = ollamaReq.Options?.Seed,
        };

        using StringContent genContent = new(JsonSerializer.Serialize(llamaReq, _jsonOptions), Encoding.UTF8, "application/json");
        using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/completions") { Content = genContent },
            genBase, genTimeout, HttpCompletionOption.ResponseHeadersRead, ct);

        log.StatusCode = (int)upstreamResp.StatusCode;

        if (!upstreamResp.IsSuccessStatusCode)
        {
            string errorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: {errorBody}";
            resp.StatusCode = (int)upstreamResp.StatusCode;
            resp.Close();
            return;
        }

        if (ollamaReq.Stream)
        {
            resp.ContentType = "application/x-ndjson";
            resp.SendChunked = true;
            resp.KeepAlive = true; // Keep connection alive during long thinking periods
            await StreamCompletionToOllamaAsync(
                upstreamResp,
                resp,
                ollamaReq.Model,
                log,
                _settings.CollectResponseDetails,
                responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                ct);
        }
        else
        {
            string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);
            string text = chunk?.Choices?.FirstOrDefault()?.Text ?? string.Empty;
            LlamaCppUsage? usage = chunk?.Usage;

            FillTokenStats(log, usage);
            log.ResponseBytes = Encoding.UTF8.GetByteCount(respBody);

            if (_settings.CollectResponseDetails)
                log.ResponseBody = RedactResponseBodyForLog(text, ollamaReq.Model);

            var ollamaResp = new OllamaGenerateResponse
            {
                Model = ollamaReq.Model,
                Response = text,
                Done = true,
                DoneReason = "stop",
                PromptEvalCount = usage?.PromptTokens,
                EvalCount = usage?.CompletionTokens,
            };

            await WriteJsonAsync(resp, ollamaResp, ct);
            log.Status = RequestStatus.Success;
        }
    }

    // ── /api/chat → POST /v1/chat/completions ──────────────────────────────

    private async Task HandleChatAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        log.RequestBytes = Encoding.UTF8.GetByteCount(body);
        OllamaChatRequest? ollamaReq = JsonSerializer.Deserialize<OllamaChatRequest>(body, _jsonOptions);
        ArgumentNullException.ThrowIfNull(ollamaReq);

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, ollamaReq.Model);
        log.Streaming = ollamaReq.Stream;
        var (chatBase, chatTimeout) = ResolveUpstream(ollamaReq.Model);

        // Get model mapping for context management settings
        ModelMapping? mapping = _settings.FindModelMapping(ollamaReq.Model);
        bool enableAutoSummarization = mapping?.EnableAutoSummarization ?? true;
        int preserveRecentCount = mapping?.PreserveRecentMessageCount ?? 4;
        int maxRetries = mapping?.MaxSummarizationRetries ?? 2;

        // Map messages, then strip a trailing assistant response-prefill message.
        // Some clients append a final assistant turn to bias the next response, which
        // Anthropic-compatible upstreams reject when enable_thinking is active.
        List<LlamaCppMessage> messages = [.. ollamaReq.Messages.Select(MapMessage)];
        if (messages.Count > 0
            && ShouldApplyThinkingCompatibility(ollamaReq.Model)
            && IsAssistantResponsePrefill(messages[^1]))
        {
            messages.RemoveAt(messages.Count - 1);
        }

        // Inject custom instructions if configured for this model mapping
        if (mapping?.InstructionSetName is not null)
        {
            InstructionSet? instructionSet = _settings.FindInstructionSet(mapping.InstructionSetName);
            if (instructionSet is not null && !string.IsNullOrWhiteSpace(instructionSet.Instructions))
            {
                // Prepend system message with custom instructions
                messages.Insert(0, new LlamaCppMessage("system", instructionSet.Instructions));
            }
        }

        // Retry loop for context overflow handling
        int retryCount = 0;
        int originalMessageCount = messages.Count;

        while (retryCount <= maxRetries)
        {
            var llamaReq = new LlamaCppChatRequest
            {
                Model = resolvedModel,
                Messages = messages,
                Stream = ollamaReq.Stream,
                Tools = MapTools(ollamaReq.Tools),
                ResponseFormat = ResolveResponseFormat(ollamaReq.Format),
                Temperature = ollamaReq.Options?.Temperature,
                TopP = ollamaReq.Options?.TopP,
                MaxTokens = ollamaReq.Options?.NumPredict,
                Stop = ollamaReq.Options?.Stop,
                Seed = ollamaReq.Options?.Seed,
                PresencePenalty = ollamaReq.Options?.PresencePenalty,
                FrequencyPenalty = ollamaReq.Options?.FrequencyPenalty,
            };

            using StringContent chatContent = new(JsonSerializer.Serialize(llamaReq, _jsonOptions), Encoding.UTF8, "application/json");
            using HttpResponseMessage upstreamResp = await SendUpstreamAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = chatContent },
                chatBase, chatTimeout, HttpCompletionOption.ResponseHeadersRead, ct);

            log.StatusCode = (int)upstreamResp.StatusCode;

            // Check for context overflow error
            bool isContextOverflow = await IsContextOverflowErrorAsync(upstreamResp, ct);

            if (!upstreamResp.IsSuccessStatusCode)
            {
                // If context overflow and auto-summarization is enabled, try to summarize and retry
                if (isContextOverflow && enableAutoSummarization && retryCount < maxRetries)
                {
                    retryCount++;
                    log.ErrorMessage = $"Context overflow detected (attempt {retryCount}/{maxRetries}), summarizing...";

                    // Summarize the conversation
                    List<LlamaCppMessage> summarizedMessages = await SummarizeConversationAsync(
                        messages,
                        preserveRecentCount,
                        chatBase,
                        chatTimeout,
                        resolvedModel,
                        ct);

                    // If summarization didn't reduce message count, stop retrying
                    if (summarizedMessages.Count >= messages.Count)
                    {
                        log.Status = RequestStatus.Error;
                        log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}: Context overflow, summarization did not help";
                        log.SummarizationRetries = retryCount;
                        log.OriginalMessageCount = originalMessageCount;
                        resp.StatusCode = (int)upstreamResp.StatusCode;
                        resp.Close();
                        return;
                    }

                    // Track summarization in log
                    log.SummarizationRetries = retryCount;
                    log.OriginalMessageCount = originalMessageCount;
                    log.SummarizedMessageCount = summarizedMessages.Count;

                    // Update messages for next retry
                    messages = summarizedMessages;
                    continue; // Retry with summarized context
                }

                // Non-retriable error or retries exhausted
                string nonRetriableErrorBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                log.Status = RequestStatus.Error;
                log.ErrorMessage = isContextOverflow
                    ? $"Upstream {(int)upstreamResp.StatusCode}: Context overflow after {retryCount} summarization attempts"
                    : $"Upstream {(int)upstreamResp.StatusCode}: {nonRetriableErrorBody}";

                if (retryCount > 0)
                {
                    log.SummarizationRetries = retryCount;
                    log.OriginalMessageCount = originalMessageCount;
                    log.SummarizedMessageCount = messages.Count;
                }

                resp.StatusCode = (int)upstreamResp.StatusCode;
                resp.Close();
                return;
            }

            // Success! Track summarization stats if any occurred
            if (retryCount > 0)
            {
                log.SummarizationRetries = retryCount;
                log.OriginalMessageCount = originalMessageCount;
                log.SummarizedMessageCount = messages.Count;
            }
            if (ollamaReq.Stream)
            {
                resp.ContentType = "application/x-ndjson";
                resp.SendChunked = true;
                resp.KeepAlive = true; // Keep connection alive during long thinking periods

                // Notify the calling AI in-band that the conversation was summarized
                // before forwarding the real response. This is the first chunk so the
                // AI sees it at the start of its context window update.
                if (retryCount > 0)
                    await WriteContextSummarizedNoticeAsync(resp, ollamaReq.Model, originalMessageCount, messages.Count, ct);

                await StreamChatToOllamaAsync(
                    upstreamResp,
                    resp,
                    ollamaReq.Model,
                    log,
                    _settings.CollectResponseDetails,
                    responseText => RedactResponseBodyForLog(responseText, ollamaReq.Model),
                    ShouldEmitHeartbeats(ollamaReq.Model),
                    _settings.StreamingHeartbeatIntervalSeconds,
                    ct,
                    () => _stats.IncrementHeartbeat(ollamaReq.Model));
            }
            else
            {
                string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);

                // Non-streaming: prefer .message over .delta (OpenAI non-streaming uses message)
                LlamaCppDelta? delta = chunk?.Choices?.FirstOrDefault()?.Message
                                    ?? chunk?.Choices?.FirstOrDefault()?.Delta;
                LlamaCppUsage? usage = chunk?.Usage;
                FillTokenStats(log, usage);
                log.ResponseBytes = Encoding.UTF8.GetByteCount(respBody);

                List<OllamaToolCall>? toolCalls = MapToolCallsToOllama(delta?.ToolCalls);

                // Prepend a brief notice to the content so the calling AI sees it
                // in its own context window.
                string? content = delta?.Content;
                if (retryCount > 0)
                {
                    string notice = BuildContextSummarizedNotice(originalMessageCount, messages.Count);
                    content = string.IsNullOrEmpty(content) ? notice : notice + content;
                }

                if (_settings.CollectResponseDetails)
                    log.ResponseBody = RedactResponseBodyForLog(delta?.Content ?? string.Empty, ollamaReq.Model);

                var ollamaResp = new OllamaChatResponse
                {
                    Model = ollamaReq.Model,
                    Message = new OllamaMessage("assistant", content) { ToolCalls = toolCalls },
                    Done = true,
                    DoneReason = toolCalls?.Count > 0 ? "tool_calls" : "stop",
                    PromptEvalCount = usage?.PromptTokens,
                    EvalCount = usage?.CompletionTokens,
                };

                await WriteJsonAsync(resp, ollamaResp, ct);
                log.Status = RequestStatus.Success;
            }

            // Request succeeded, break out of retry loop
            return;
        }

        // Should never reach here, but handle it gracefully
        log.Status = RequestStatus.Error;
        log.ErrorMessage = "Max retries exceeded";
        resp.StatusCode = 500;
        resp.Close();
    }

    // ── /api/embeddings → POST /v1/embeddings ──────────────────────────────

    private async Task HandleEmbeddingsAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        OllamaEmbeddingsRequest? ollamaReq = JsonSerializer.Deserialize<OllamaEmbeddingsRequest>(body, _jsonOptions);
        ArgumentNullException.ThrowIfNull(ollamaReq);

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        if (_settings.CollectRequestDetails)
            log.RequestBody = RedactRequestBodyForLog(body, ollamaReq.Model);
        var (embedBase, embedTimeout) = ResolveUpstream(ollamaReq.Model);

        // Resolve input: prefer new `input` (string or string[]), fall back to legacy `prompt`.
        object resolvedInput = ResolveEmbeddingInput(ollamaReq);
        bool isBatch = resolvedInput is string[] batch && batch.Length > 1;

        var llamaReq = new LlamaCppEmbeddingsRequest { Model = resolvedModel, Input = resolvedInput };

        using StringContent embedContent = new(JsonSerializer.Serialize(llamaReq, _jsonOptions), Encoding.UTF8, "application/json");
        using var embedReqMsg = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings") { Content = embedContent };
        HttpResponseMessage upstreamResp = await SendUpstreamAsync(embedReqMsg, embedBase, embedTimeout, HttpCompletionOption.ResponseContentRead, ct);
        log.StatusCode = (int)upstreamResp.StatusCode;

        string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
        LlamaCppEmbeddingsResponse? llamaResp = JsonSerializer.Deserialize<LlamaCppEmbeddingsResponse>(respBody, _jsonOptions);

        OllamaEmbeddingsResponse ollamaResp = isBatch
            ? new OllamaEmbeddingsResponse { Embeddings = [.. (llamaResp?.Data ?? []).Select(d => d.Embedding)] }
            : new OllamaEmbeddingsResponse { Embedding = llamaResp?.Data?.FirstOrDefault()?.Embedding ?? [] };

        log.Status = upstreamResp.IsSuccessStatusCode ? RequestStatus.Success : RequestStatus.Error;
        await WriteJsonAsync(resp, ollamaResp, ct);
    }

    // ── Streaming helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the one-line notice injected into assistant content when the conversation
    /// was auto-summarized due to context overflow.
    /// </summary>
    private static string BuildContextSummarizedNotice(int originalCount, int summarizedCount) =>
        $"[Note: conversation history was automatically summarized " +
        $"({originalCount} → {summarizedCount} messages) because the context window was exhausted.]\n\n";

    /// <summary>
    /// Writes a single non-done streaming chunk containing the summarization notice
    /// so the calling AI sees it as the first token of the reply.
    /// </summary>
    private static async Task WriteContextSummarizedNoticeAsync(
        HttpListenerResponse resp, string modelName,
        int originalCount, int summarizedCount,
        CancellationToken ct)
    {
        string notice = BuildContextSummarizedNotice(originalCount, summarizedCount);
        var noticeChunk = new OllamaChatResponse
        {
            Model = modelName,
            Message = new OllamaMessage("assistant", notice),
            Done = false,
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(noticeChunk, _jsonOptions) + "\n");
        await resp.OutputStream.WriteAsync(bytes, ct);
    }

    private static async Task StreamCompletionToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        Func<string, string> redactResponse,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        var responseAccumulator = collectResponse ? new StringBuilder() : null;
        bool reachedDone = false;
        long responseBytes = 0;

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            // SSE format: "data: {...}" or "data: [DONE]"
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                line = line[6..];

            if (line == "[DONE]")
            {
                reachedDone = true;
                var doneChunk = new OllamaGenerateResponse
                {
                    Model = modelName,
                    Response = string.Empty,
                    Done = true,
                    DoneReason = "stop",
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                };
                string doneJson = JsonSerializer.Serialize(doneChunk, _jsonOptions);
                responseBytes += Encoding.UTF8.GetByteCount(doneJson);
                await writer.WriteLineAsync(doneJson);
                await writer.FlushAsync(ct);
                break;
            }

            LlamaCppStreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(line, _jsonOptions); }
            catch { continue; }

            if (chunk is null) continue;

            FillTokenStats(log, chunk.Usage);

            string token = chunk.Choices?.FirstOrDefault()?.Text ?? string.Empty;
            bool done = chunk.Choices?.FirstOrDefault()?.FinishReason != null;

            responseAccumulator?.Append(token);

            var ollamaChunk = new OllamaGenerateResponse
            {
                Model = modelName,
                Response = token,
                Done = done,
            };

            string chunkJson = JsonSerializer.Serialize(ollamaChunk, _jsonOptions);
            responseBytes += Encoding.UTF8.GetByteCount(chunkJson);
            await writer.WriteLineAsync(chunkJson);
            await writer.FlushAsync(ct);
        }

        if (responseAccumulator is not null)
            log.ResponseBody = redactResponse(responseAccumulator.ToString());

        log.ResponseBytes = responseBytes;
        resp.Close();
        log.Status = ct.IsCancellationRequested && !reachedDone
            ? RequestStatus.Cancelled
            : RequestStatus.Success;
    }

    private static async Task StreamChatToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        Func<string, string> redactResponse,
        bool enableHeartbeats,
        int heartbeatIntervalSeconds,
        CancellationToken ct,
        Action? onHeartbeatSent = null)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        var responseAccumulator = collectResponse ? new StringBuilder() : null;
        bool reachedDone = false;
        long responseBytes = 0;
        TimeSpan heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(heartbeatIntervalSeconds, 5, 300));

        while (!ct.IsCancellationRequested)
        {
            string? line = await ReadLineWithOllamaChatHeartbeatsAsync(
                reader,
                writer,
                modelName,
                enableHeartbeats,
                heartbeatInterval,
                ct,
                onHeartbeatSent);
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
                line = line[6..];

            if (line == "[DONE]")
            {
                reachedDone = true;
                var doneChunk = new OllamaChatResponse
                {
                    Model = modelName,
                    Message = new OllamaMessage("assistant", string.Empty),
                    Done = true,
                    DoneReason = "stop",
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                };
                string doneJson = JsonSerializer.Serialize(doneChunk, _jsonOptions);
                responseBytes += Encoding.UTF8.GetByteCount(doneJson);
                await writer.WriteLineAsync(doneJson);
                await writer.FlushAsync(ct);
                break;
            }

            LlamaCppStreamChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(line, _jsonOptions); }
            catch { continue; }

            if (chunk is null) continue;

            FillTokenStats(log, chunk.Usage);

            LlamaCppDelta? delta = chunk.Choices?.FirstOrDefault()?.Delta;
            string token = delta?.Content ?? string.Empty;
            List<OllamaToolCall>? toolCalls = MapToolCallsToOllama(delta?.ToolCalls);
            bool done = chunk.Choices?.FirstOrDefault()?.FinishReason != null;

            responseAccumulator?.Append(token);

            var ollamaChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", token) { ToolCalls = toolCalls },
                Done = done,
                DoneReason = done ? (toolCalls?.Count > 0 ? "tool_calls" : "stop") : null,
            };

            string chunkJson = JsonSerializer.Serialize(ollamaChunk, _jsonOptions);
            responseBytes += Encoding.UTF8.GetByteCount(chunkJson);
            await writer.WriteLineAsync(chunkJson);
            await writer.FlushAsync(ct);
        }

        if (responseAccumulator is not null)
            log.ResponseBody = redactResponse(responseAccumulator.ToString());

        log.ResponseBytes = responseBytes;
        resp.Close();
        log.Status = ct.IsCancellationRequested && !reachedDone
            ? RequestStatus.Cancelled
            : RequestStatus.Success;
    }

    private static async Task<string?> ReadLineWithOllamaChatHeartbeatsAsync(
        StreamReader reader,
        StreamWriter writer,
        string modelName,
        bool enableHeartbeats,
        TimeSpan heartbeatInterval,
        CancellationToken ct,
        Action? onHeartbeatSent = null)
    {
        Task<string?> readTask = reader.ReadLineAsync(ct).AsTask();

        while (enableHeartbeats && !readTask.IsCompleted)
        {
            Task delayTask = Task.Delay(heartbeatInterval, ct);
            Task completed = await Task.WhenAny(readTask, delayTask);
            if (completed == readTask)
                break;

            var heartbeatChunk = new OllamaChatResponse
            {
                Model = modelName,
                Message = new OllamaMessage("assistant", string.Empty),
                Done = false,
            };

            await writer.WriteLineAsync(JsonSerializer.Serialize(heartbeatChunk, _jsonOptions));
            await writer.FlushAsync(ct);
            onHeartbeatSent?.Invoke();
        }

        return await readTask;
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    /// <summary>Converts Ollama format ("json" string or {"type":...} object) to OpenAI response_format.</summary>
    private static LlamaCppResponseFormat? ResolveResponseFormat(object? format)
    {
        if (format is null) return null;

        if (format is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                string? s = je.GetString();
                return string.Equals(s, "json", StringComparison.OrdinalIgnoreCase)
                    ? new LlamaCppResponseFormat { Type = "json_object" }
                    : null;
            }
            if (je.ValueKind == JsonValueKind.Object)
            {
                string type = je.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "text" : "text";
                return new LlamaCppResponseFormat { Type = type };
            }
        }

        return null;
    }

    private static LlamaCppMessage MapMessage(OllamaMessage m) =>
        new(m.Role, m.Content)
        {
            ToolCallId = m.ToolCallId,
            ToolCalls = m.ToolCalls is not null
                ? [.. m.ToolCalls.Select(tc => new LlamaCppToolCall
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Function = tc.Function is null ? null : new LlamaCppToolCallFunction
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments?.ToString(),
                        },
                    })]
                : null,
        };

    private static List<LlamaCppTool>? MapTools(List<OllamaTool>? tools) =>
        tools is null ? null
        : [.. tools.Select(t => new LlamaCppTool
            {
                Type = t.Type,
                Function = t.Function is null ? null : new LlamaCppToolFunction
                {
                    Name = t.Function.Name,
                    Description = t.Function.Description,
                    Parameters = t.Function.Parameters,
                },
            })];

    private static List<OllamaToolCall>? MapToolCallsToOllama(List<LlamaCppToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0) return null;
        return [.. toolCalls.Select(tc =>
        {
            object? args = null;
            if (tc.Function?.Arguments is not null)
            {
                try { args = JsonSerializer.Deserialize<object>(tc.Function.Arguments, _jsonOptions); }
                catch { args = tc.Function.Arguments; }
            }
            return new OllamaToolCall
            {
                Function = new OllamaToolCallFunction { Name = tc.Function?.Name ?? string.Empty, Arguments = args },
            };
        })];
    }

    private static object ResolveEmbeddingInput(OllamaEmbeddingsRequest req)
    {
        if (req.Input is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
            if (je.ValueKind == JsonValueKind.String)
                return je.GetString() ?? string.Empty;
        }
        if (req.Input is string s && !string.IsNullOrEmpty(s))
            return s;
        return req.Prompt ?? string.Empty;
    }

    // ── Context Management ───────────────────────────────────────────────────

    /// <summary>
    /// Summarizes older messages in a conversation to reduce context size while preserving recent exchanges.
    /// Keeps system messages and the most recent N message exchanges, summarizes everything in between.
    /// </summary>
    private async Task<List<LlamaCppMessage>> SummarizeConversationAsync(
        List<LlamaCppMessage> messages,
        int preserveRecentCount,
        string baseUrl,
        int timeoutSeconds,
        string modelName,
        CancellationToken ct)
    {
        if (messages.Count <= preserveRecentCount + 1)
            return messages; // Nothing to summarize

        // Separate system messages, messages to summarize, and recent messages
        List<LlamaCppMessage> systemMessages = [];

        // First pass: collect system messages
        foreach (LlamaCppMessage msg in messages)
        {
            if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                systemMessages.Add(msg);
        }

        // Second pass: collect non-system messages
        List<LlamaCppMessage> conversationMessages = [];
        foreach (LlamaCppMessage msg in messages)
        {
            if (!msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                conversationMessages.Add(msg);
        }

        // Split conversation into old (to summarize) and recent (to preserve)
        int messagesToPreserve = Math.Min(preserveRecentCount, conversationMessages.Count);
        int splitPoint = conversationMessages.Count - messagesToPreserve;

        if (splitPoint <= 0)
            return messages; // Nothing to summarize

        List<LlamaCppMessage> oldMessages = [.. conversationMessages.Take(splitPoint)];
        List<LlamaCppMessage> recentMessages = [.. conversationMessages.Skip(splitPoint)];

        if (oldMessages.Count == 0)
            return messages; // Nothing to summarize

        // Build summarization prompt
        string conversationText = BuildConversationTextForSummary(oldMessages);

        List<LlamaCppMessage> summaryRequest =
        [
            new LlamaCppMessage("system", 
                "You are a helpful assistant that summarizes conversations concisely. " +
                "Preserve key facts, decisions, context, and important details. " +
                "Focus on what's essential for continuing the conversation. " +
                "Respond with ONLY the summary, no preamble or meta-commentary."),
            new LlamaCppMessage("user", 
                $"Please provide a concise summary of the following conversation:\n\n{conversationText}")
        ];

        var summaryRequestObj = new LlamaCppChatRequest
        {
            Model = modelName,
            Messages = summaryRequest,
            Stream = false,
            Temperature = 0.3f, // Lower temperature for consistent summarization
            MaxTokens = 500, // Limit summary length
        };

        try
        {
            using StringContent summaryContent = new(
                JsonSerializer.Serialize(summaryRequestObj, _jsonOptions), 
                Encoding.UTF8, 
                "application/json");

            using HttpResponseMessage summaryResp = await SendUpstreamAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = summaryContent },
                baseUrl, 
                timeoutSeconds, 
                HttpCompletionOption.ResponseHeadersRead, 
                ct);

            if (!summaryResp.IsSuccessStatusCode)
                return messages; // Summarization failed, return original

            string respBody = await summaryResp.Content.ReadAsStringAsync(ct);
            LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);

            LlamaCppDelta? delta = chunk?.Choices?.FirstOrDefault()?.Message 
                                ?? chunk?.Choices?.FirstOrDefault()?.Delta;

            if (delta?.Content is null || string.IsNullOrWhiteSpace(delta.Content))
                return messages; // No summary generated, return original

            // Build new message list: system messages + summary + recent messages
            List<LlamaCppMessage> result = [.. systemMessages];

            // Add summary as an assistant message
            result.Add(new LlamaCppMessage("assistant", 
                $"[Previous conversation summary: {delta.Content}]"));

            result.AddRange(recentMessages);

            return result;
        }
        catch
        {
            // If summarization fails for any reason, return original messages
            return messages;
        }
    }

    /// <summary>
    /// Builds a formatted text representation of messages for summarization.
    /// </summary>
    private static string BuildConversationTextForSummary(List<LlamaCppMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (LlamaCppMessage msg in messages)
        {
            string role = msg.Role switch
            {
                "user" => "User",
                "assistant" => "Assistant",
                "system" => "System",
                _ => msg.Role
            };

            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                sb.AppendLine($"{role}: {msg.Content}");
                sb.AppendLine();
            }
            else if (msg.ToolCalls?.Count > 0)
            {
                sb.AppendLine($"{role}: [Called tools: {string.Join(", ", msg.ToolCalls.Select(tc => tc.Function?.Name ?? "unknown"))}]");
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        using StreamReader reader = new(req.InputStream, req.ContentEncoding);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, object value, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(value, _jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, ct);
        resp.Close();
    }

    private static void FillTokenStats(RequestLog log, LlamaCppUsage? usage)
    {
        if (usage is null) return;
        log.PromptTokens = usage.PromptTokens;
        log.CompletionTokens = usage.CompletionTokens;
    }

    /// <summary>Wraps a write-only stream and counts the bytes written through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            BytesWritten += buffer.Length;
        }
    }

    /// <summary>Captures bytes written through it while forwarding them immediately to the inner stream.</summary>
    private sealed class ResponseCaptureStream(Stream inner) : Stream
    {
        private readonly MemoryStream _capture = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public string GetCapturedText() => Encoding.UTF8.GetString(_capture.ToArray());

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _capture.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), ct);
            await _capture.WriteAsync(buffer.AsMemory(offset, count), ct);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            await _capture.WriteAsync(buffer, ct);
        }
    }
}
