using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitExtensions.AICommitMessage
{
    /// <summary>
    /// Minimal client for OpenAI-compatible endpoints. <c>chat</c> uses the classic Chat Completions API
    /// (<c>{baseUrl}/chat/completions</c>), <c>response</c> uses the newer Responses API
    /// (<c>{baseUrl}/responses</c>). Both shapes are accepted by OpenAI, Azure OpenAI, OpenRouter, Groq,
    /// and local servers such as Ollama, so the caller just picks which one its endpoint speaks.
    /// </summary>
    internal sealed class OpenAiClient
    {
        internal const string ChatApiType = "chat";
        internal const string ResponseApiType = "response";

        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly bool _useResponseApi;

        public OpenAiClient(string baseUrl, string apiKey, string model, string apiType)
        {
            _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            _apiKey = (apiKey ?? string.Empty).Trim();
            _model = (model ?? string.Empty).Trim();
            _useResponseApi = string.Equals((apiType ?? string.Empty).Trim(), ResponseApiType, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Hard deadline for loading the model list. Applied both to <see cref="HttpClient.Timeout"/> and
        /// to a linked token, so the wait is bounded even when DNS resolution itself is slow.
        /// </summary>
        internal const int ModelListTimeoutSeconds = 6;

        public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            EnsureBaseUrl();

            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(ModelListTimeoutSeconds));

            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(ModelListTimeoutSeconds) };
            using HttpRequestMessage request = new(HttpMethod.Get, _baseUrl + "/models");
            AddAuthorization(request);

            using HttpResponseMessage response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The API returned {(int)response.StatusCode} ({response.ReasonPhrase}) while loading models.\n\n{Truncate(responseText, 1000)}");
            }

            using JsonDocument doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Unexpected API response shape (no data array in /models response):\n\n" + Truncate(responseText, 1000));
            }

            List<string> models = data.EnumerateArray()
                .Where(item => item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();

            if (models.Count == 0)
            {
                throw new InvalidOperationException("The /models response did not contain any model IDs.");
            }

            return models;
        }

        private const string FeatureSelectionInstructions =
            "你是资深软件工程师。下面是某个 git 仓库当前的改动清单：STAGED 表示已经暂存，UNSTAGED 表示尚未暂存。\n" +
            "请从 UNSTAGED 中挑出一组属于**同一个功能**的改动，供一次提交使用。\n" +
            "规则：\n" +
            "- 只能从 UNSTAGED 里挑文件，绝不能挑 STAGED 里的文件。\n" +
            "- 优先选择调用链或功能上彼此相关的文件；如果确实只有一处改动相关，就只选它。\n" +
            "- 至少选 1 个文件，宁少勿滥；路径必须与清单中给出的字符串完全一致。\n" +
            "- feature：用中文一句话概括这个功能，最多 20 个字，不加句号。\n" +
            "- reason：用中文说明为什么这些改动属于同一个功能，最多 80 个字。\n" +
            "- 只输出一个 JSON 对象，不要 markdown，不要代码围栏，前后不要任何解释文字：\n" +
            "{\"feature\":\"...\",\"files\":[\"路径1\",\"路径2\"],\"reason\":\"...\"}";

        /// <summary>
        /// Asks the model to pick one related group out of the unstaged changes. Uses whichever API the
        /// caller configured, exactly like <see cref="CompleteAsync"/> does.
        /// </summary>
        /// <param name="knownPaths">
        /// The real candidate paths. When given, the JSON object whose "files" match them best wins, so an
        /// echoed example from the instructions cannot be mistaken for the answer.
        /// </param>
        public async Task<FeatureSelection> SelectFeatureAsync(
            string changeSummary,
            IReadOnlyList<string>? knownPaths = null,
            CancellationToken cancellationToken = default)
        {
            EnsureBaseUrl();
            EnsureModel();

            string responseText = _useResponseApi
                ? await PostAsync(
                    "/responses",
                    JsonSerializer.Serialize(new
                    {
                        model = _model,
                        instructions = FeatureSelectionInstructions,
                        input = changeSummary
                    }),
                    cancellationToken).ConfigureAwait(false)
                : await PostAsync(
                    "/chat/completions",
                    JsonSerializer.Serialize(new
                    {
                        model = _model,
                        temperature = 0.2,
                        messages = new object[]
                        {
                            new { role = "system", content = FeatureSelectionInstructions },
                            new { role = "user", content = changeSummary }
                        }
                    }),
                    cancellationToken).ConfigureAwait(false);

            string content = _useResponseApi
                ? ExtractResponseContent(responseText)
                : ExtractChatContent(responseText);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException(
                    "模型返回的内容为空，无法进行分组：\n\n" + Truncate(responseText, 1000));
            }

            return FeatureSelection.Parse(content, knownPaths);
        }

        public async Task<string> CompleteAsync(string systemPrompt, string diff, CancellationToken cancellationToken = default)
        {
            EnsureBaseUrl();
            EnsureModel();

            return _useResponseApi
                ? await CompleteWithResponseApiAsync(systemPrompt, diff, cancellationToken).ConfigureAwait(false)
                : await CompleteWithChatApiAsync(systemPrompt, diff, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> CompleteWithChatApiAsync(string systemPrompt, string diff, CancellationToken cancellationToken)
        {
            var requestBody = new
            {
                model = _model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = UserContent(diff) }
                }
            };

            string responseText = await PostAsync("/chat/completions", JsonSerializer.Serialize(requestBody), cancellationToken).ConfigureAwait(false);
            return ExtractChatContent(responseText);
        }

        private async Task<string> CompleteWithResponseApiAsync(string systemPrompt, string diff, CancellationToken cancellationToken)
        {
            var requestBody = new
            {
                model = _model,
                instructions = systemPrompt,
                input = UserContent(diff)
            };

            string responseText = await PostAsync("/responses", JsonSerializer.Serialize(requestBody), cancellationToken).ConfigureAwait(false);
            return ExtractResponseContent(responseText);
        }

        private static string UserContent(string diff)
            => "Here is the staged git diff. Write the commit message for it:\n\n" + diff;

        private async Task<string> PostAsync(string path, string json, CancellationToken cancellationToken)
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };
            using HttpRequestMessage request = new(HttpMethod.Post, _baseUrl + path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            AddAuthorization(request);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The API returned {(int)response.StatusCode} ({response.ReasonPhrase}).\n\n{Truncate(responseText, 1000)}");
            }

            return responseText;
        }

        private void EnsureBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                throw new InvalidOperationException(
                    "API base URL is not configured. Set it in Settings → Plugins → AI Commit Message.");
            }
        }

        private void EnsureModel()
        {
            if (string.IsNullOrWhiteSpace(_model))
            {
                throw new InvalidOperationException("No model is selected. Load the model list and choose a model first.");
            }
        }

        private void AddAuthorization(HttpRequestMessage request)
        {
            if (!string.IsNullOrEmpty(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        // Responses API: the text lives in output[].content[].text ("output_text" parts). Some servers
        // also expose a convenient top-level output_text, which is preferred when present.
        private static string ExtractResponseContent(string responseText)
        {
            using JsonDocument doc = JsonDocument.Parse(responseText);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("output_text", out JsonElement direct)
                && direct.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(direct.GetString()))
            {
                return direct.GetString()!.Trim();
            }

            if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out JsonElement text)
                            && text.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(text.GetString()))
                        {
                            return text.GetString()!.Trim();
                        }
                    }
                }
            }

            throw new InvalidOperationException(
                "Unexpected API response shape (no output text in the /responses reply):\n\n" + Truncate(responseText, 1000));
        }

        private static string ExtractChatContent(string responseText)
        {
            using JsonDocument doc = JsonDocument.Parse(responseText);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString()?.Trim() ?? string.Empty;
                }
            }

            throw new InvalidOperationException(
                "Unexpected API response shape (no choices[0].message.content):\n\n" + Truncate(responseText, 1000));
        }

        internal static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    /// <summary>The one group of changes the model picked out of the unstaged files.</summary>
    internal sealed class FeatureSelection
    {
        private FeatureSelection(string feature, string reason, IReadOnlyList<string> files)
        {
            Feature = feature;
            Reason = reason;
            Files = files;
        }

        /// <summary>Short human-readable name of the feature, e.g. "模型下拉与接口类型".</summary>
        public string Feature { get; }

        /// <summary>Why the model thinks these files belong together.</summary>
        public string Reason { get; }

        /// <summary>Paths the model selected; still unvalidated against the real change set.</summary>
        public IReadOnlyList<string> Files { get; }

        /// <summary>
        /// Tolerant parser: models like to wrap JSON in ``` fences or add a sentence around it, so the
        /// first balanced JSON object inside the reply is used.
        /// </summary>
        public static FeatureSelection Parse(string responseText, IReadOnlyList<string>? knownPaths = null)
        {
            string text = responseText ?? string.Empty;

            // Models sometimes restate the example from the instructions before sending the real answer,
            // so the object whose "files" match the real candidates best wins; without candidates the first
            // object that carries "files" does.
            string chosenJson = string.Empty;
            string fallbackJson = string.Empty;
            int bestScore = -1;
            foreach (string candidate in ExtractJsonObjects(text))
            {
                try
                {
                    using JsonDocument probe = JsonDocument.Parse(candidate);
                    if (probe.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!probe.RootElement.TryGetProperty("files", out _))
                    {
                        if (fallbackJson.Length == 0)
                        {
                            fallbackJson = candidate;
                        }

                        continue;
                    }

                    int score = ScoreFiles(probe.RootElement, knownPaths);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        chosenJson = candidate;
                    }
                }
                catch (JsonException)
                {
                    // Balanced braces that are not valid JSON; keep looking.
                }
            }

            if (chosenJson.Length == 0)
            {
                chosenJson = fallbackJson;
            }

            if (chosenJson.Length == 0)
            {
                throw new InvalidOperationException(
                    "模型没有返回可用于分组的 JSON 对象：\n\n" + OpenAiClient.Truncate(text, 1000));
            }

            using JsonDocument doc = JsonDocument.Parse(chosenJson);
            JsonElement root = doc.RootElement;

            string feature = root.TryGetProperty("feature", out JsonElement featureElement)
                && featureElement.ValueKind == JsonValueKind.String
                    ? featureElement.GetString()?.Trim() ?? string.Empty
                    : string.Empty;

            string reason = root.TryGetProperty("reason", out JsonElement reasonElement)
                && reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()?.Trim() ?? string.Empty
                    : string.Empty;

            List<string> files = new();
            if (root.TryGetProperty("files", out JsonElement filesElement) && filesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in filesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? path = item.GetString()?.Trim().Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(path) || files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files.Add(path);
                }
            }

            return new FeatureSelection(
                string.IsNullOrWhiteSpace(feature) ? "未命名功能" : feature,
                reason,
                files);
        }

        // How many of the object's "files" are real candidates; 0 when there is nothing to compare with.
        private static int ScoreFiles(JsonElement root, IReadOnlyList<string>? knownPaths)
        {
            if (knownPaths is null || knownPaths.Count == 0
                || !root.TryGetProperty("files", out JsonElement files)
                || files.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            HashSet<string> known = new(knownPaths, StringComparer.OrdinalIgnoreCase);
            int score = 0;
            foreach (JsonElement item in files.EnumerateArray())
            {
                string? path = item.ValueKind == JsonValueKind.String
                    ? item.GetString()?.Replace('\\', '/').Trim()
                    : null;
                if (path is not null && known.Contains(path))
                {
                    score++;
                }
            }

            return score;
        }

        private static IEnumerable<string> ExtractJsonObjects(string text)
        {
            int index = 0;
            while (index < text.Length)
            {
                int start = text.IndexOf('{', index);
                if (start < 0)
                {
                    yield break;
                }

                string? candidate = ReadBalancedObject(text, start);
                if (candidate is null)
                {
                    yield break;
                }

                yield return candidate;
                index = start + candidate.Length;
            }
        }

        private static string? ReadBalancedObject(string text, int start)
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            return text.Substring(start, i - start + 1);
                        }

                        break;
                }
            }

            return null;
        }
    }
}
