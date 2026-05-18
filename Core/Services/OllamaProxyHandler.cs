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
            if (string.Equals(m.OllamaName, ollamaModel, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(m.UpstreamUrl))
                    throw new InvalidOperationException(
                        $"Model mapping '{m.OllamaName}' has no upstream URL configured. " +
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
                    $"Model mapping '{fallback.OllamaName}' has no upstream URL configured. " +
                    "Each mapping must specify its own UpstreamUrl.");

            int timeout = fallback.UpstreamTimeoutSeconds > 0 ? fallback.UpstreamTimeoutSeconds : 300;
            return (fallback.UpstreamUrl.TrimEnd('/'), timeout);
        }

        throw new InvalidOperationException(
            $"No mapping found for model '{ollamaModel}'. " +
            "Add a mapping in settings with OllamaName, LlamaCppName, and UpstreamUrl.");
    }

    private bool ShouldApplyThinkingCompatibility(string modelName)
    {
        ModelMapping? mapping = _settings.FindModelMapping(modelName);
        return mapping?.EnableThinkingCompatibility ?? true;
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
                if (_settings.CollectRequestDetails)
                    log.RequestBody = bodyText;
                string rewritten = NormalizeRequestBody(bodyText, log, ShouldApplyThinkingCompatibility);
                originalModel = log.Model; // set by NormalizeRequestBody
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

        if (_settings.CollectResponseDetails)
        {
            using var buffer = new MemoryStream();
            await upstreamStream.CopyToAsync(buffer, ct);
            byte[] responseBytes = buffer.ToArray();
            log.ResponseBody = Encoding.UTF8.GetString(responseBytes);
            await resp.OutputStream.WriteAsync(responseBytes, ct);
        }
        else
        {
            await upstreamStream.CopyToAsync(resp.OutputStream, ct);
        }

        resp.OutputStream.Close();

        log.Status = RequestStatus.Success;
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

            // Check whether the messages array has consecutive leading system messages.
            bool hasConsecutiveSystemMessages = false;
            bool hasTrailingAssistantPrefill = false;
            if (root.TryGetProperty("messages", out JsonElement messagesEl)
                && messagesEl.ValueKind == JsonValueKind.Array)
            {
                List<JsonElement> messages = [.. messagesEl.EnumerateArray()];

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
                && !hasTrailingAssistantPrefill)
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
                      && (hasConsecutiveSystemMessages || hasTrailingAssistantPrefill))
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();

                    List<JsonElement> messages = [.. prop.Value.EnumerateArray()];

                    // Collect and merge consecutive leading system message contents.
                    var systemParts = new List<string>();
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
        if (_settings.CollectRequestDetails)
            log.RequestBody = body;
        OllamaShowRequest? showReq = JsonSerializer.Deserialize<OllamaShowRequest>(body, _jsonOptions);
        string modelName = _settings.ResolveModelName(showReq?.Model ?? showReq?.Name ?? string.Empty);
        log.Model = modelName;

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
        if (_settings.CollectRequestDetails)
            log.RequestBody = body;
        OllamaGenerateRequest? ollamaReq = JsonSerializer.Deserialize<OllamaGenerateRequest>(body, _jsonOptions);
        ArgumentNullException.ThrowIfNull(ollamaReq);

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        log.Streaming = ollamaReq.Stream;
        var (genBase, genTimeout) = ResolveUpstream(ollamaReq.Model);

        string prompt = string.IsNullOrEmpty(ollamaReq.System)
            ? ollamaReq.Prompt
            : $"{ollamaReq.System}\n\n{ollamaReq.Prompt}";

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
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}";
            resp.StatusCode = (int)upstreamResp.StatusCode;
            resp.Close();
            return;
        }

        if (ollamaReq.Stream)
        {
            resp.ContentType = "application/x-ndjson";
            resp.SendChunked = true;
            await StreamCompletionToOllamaAsync(upstreamResp, resp, ollamaReq.Model, log, _settings.CollectResponseDetails, ct);
        }
        else
        {
            string respBody = await upstreamResp.Content.ReadAsStringAsync(ct);
            LlamaCppStreamChunk? chunk = JsonSerializer.Deserialize<LlamaCppStreamChunk>(respBody, _jsonOptions);
            string text = chunk?.Choices?.FirstOrDefault()?.Text ?? string.Empty;
            LlamaCppUsage? usage = chunk?.Usage;

            FillTokenStats(log, usage);

            if (_settings.CollectResponseDetails)
                log.ResponseBody = text;

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
        if (_settings.CollectRequestDetails)
            log.RequestBody = body;
        OllamaChatRequest? ollamaReq = JsonSerializer.Deserialize<OllamaChatRequest>(body, _jsonOptions);
        ArgumentNullException.ThrowIfNull(ollamaReq);

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
        log.Streaming = ollamaReq.Stream;
        var (chatBase, chatTimeout) = ResolveUpstream(ollamaReq.Model);

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

        if (!upstreamResp.IsSuccessStatusCode)
        {
            log.Status = RequestStatus.Error;
            log.ErrorMessage = $"Upstream {(int)upstreamResp.StatusCode}";
            resp.StatusCode = (int)upstreamResp.StatusCode;
            resp.Close();
            return;
        }

        if (ollamaReq.Stream)
        {
            resp.ContentType = "application/x-ndjson";
            resp.SendChunked = true;
            await StreamChatToOllamaAsync(upstreamResp, resp, ollamaReq.Model, log, _settings.CollectResponseDetails, ct);
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

            List<OllamaToolCall>? toolCalls = MapToolCallsToOllama(delta?.ToolCalls);

            if (_settings.CollectResponseDetails)
                log.ResponseBody = delta?.Content;

            var ollamaResp = new OllamaChatResponse
            {
                Model = ollamaReq.Model,
                Message = new OllamaMessage("assistant", delta?.Content) { ToolCalls = toolCalls },
                Done = true,
                DoneReason = toolCalls?.Count > 0 ? "tool_calls" : "stop",
                PromptEvalCount = usage?.PromptTokens,
                EvalCount = usage?.CompletionTokens,
            };

            await WriteJsonAsync(resp, ollamaResp, ct);
            log.Status = RequestStatus.Success;
        }
    }

    // ── /api/embeddings → POST /v1/embeddings ──────────────────────────────

    private async Task HandleEmbeddingsAsync(HttpListenerRequest req, HttpListenerResponse resp, RequestLog log, CancellationToken ct)
    {
        string body = await ReadBodyAsync(req, ct);
        if (_settings.CollectRequestDetails)
            log.RequestBody = body;
        OllamaEmbeddingsRequest? ollamaReq = JsonSerializer.Deserialize<OllamaEmbeddingsRequest>(body, _jsonOptions);
        ArgumentNullException.ThrowIfNull(ollamaReq);

        string resolvedModel = _settings.ResolveModelName(ollamaReq.Model);
        log.Model = resolvedModel;
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

    private static async Task StreamCompletionToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        var responseAccumulator = collectResponse ? new StringBuilder() : null;

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
                var doneChunk = new OllamaGenerateResponse
                {
                    Model = modelName,
                    Response = string.Empty,
                    Done = true,
                    DoneReason = "stop",
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(doneChunk, _jsonOptions));
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

            await writer.WriteLineAsync(JsonSerializer.Serialize(ollamaChunk, _jsonOptions));
            await writer.FlushAsync(ct);
        }

        if (responseAccumulator is not null)
            log.ResponseBody = responseAccumulator.ToString();

        resp.Close();
        log.Status = RequestStatus.Success;
    }

    private static async Task StreamChatToOllamaAsync(
        HttpResponseMessage upstreamResp,
        HttpListenerResponse resp,
        string modelName,
        RequestLog log,
        bool collectResponse,
        CancellationToken ct)
    {
        await using Stream stream = await upstreamResp.Content.ReadAsStreamAsync(ct);
        using StreamReader reader = new(stream, Encoding.UTF8);
        await using StreamWriter writer = new(resp.OutputStream, Encoding.UTF8, leaveOpen: true);

        var responseAccumulator = collectResponse ? new StringBuilder() : null;

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line is null) break;          // end of stream
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
                line = line[6..];

            if (line == "[DONE]")
            {
                var doneChunk = new OllamaChatResponse
                {
                    Model = modelName,
                    Message = new OllamaMessage("assistant", string.Empty),
                    Done = true,
                    DoneReason = "stop",
                    PromptEvalCount = log.PromptTokens > 0 ? log.PromptTokens : null,
                    EvalCount = log.CompletionTokens > 0 ? log.CompletionTokens : null,
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(doneChunk, _jsonOptions));
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

            await writer.WriteLineAsync(JsonSerializer.Serialize(ollamaChunk, _jsonOptions));
            await writer.FlushAsync(ct);
        }

        if (responseAccumulator is not null)
            log.ResponseBody = responseAccumulator.ToString();

        resp.Close();
        log.Status = RequestStatus.Success;
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
}
