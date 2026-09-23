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
    /// Minimal client for the OpenAI-compatible Chat Completions endpoint
    /// (<c>{baseUrl}/chat/completions</c>). The same shape is accepted by OpenAI, Azure OpenAI,
    /// OpenRouter, Groq, and local servers such as Ollama, so one code path covers cloud and local.
    /// </summary>
    internal sealed class OpenAiClient
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;

        public OpenAiClient(string baseUrl, string apiKey, string model)
        {
            _baseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            _apiKey = (apiKey ?? string.Empty).Trim();
            _model = (model ?? string.Empty).Trim();
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

        public async Task<string> CompleteAsync(string systemPrompt, string diff, CancellationToken cancellationToken = default)
        {
            EnsureBaseUrl();
            EnsureModel();

            var requestBody = new
            {
                model = _model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = "Here is the staged git diff. Write the commit message for it:\n\n" + diff }
                }
            };

            string json = JsonSerializer.Serialize(requestBody);

            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };
            using HttpRequestMessage request = new(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            AddAuthorization(request);

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The API returned {(int)response.StatusCode} ({response.ReasonPhrase}).\n\n{Truncate(responseText, 1000)}");
            }

            return ExtractContent(responseText);
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

        private static string ExtractContent(string responseText)
        {
            using JsonDocument doc = JsonDocument.Parse(responseText);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement first = choices[0];
                if (first.TryGetProperty("message", out JsonElement message)
                    && message.TryGetProperty("content", out JsonElement content))
                {
                    return content.GetString()?.Trim() ?? string.Empty;
                }
            }

            throw new InvalidOperationException(
                "Unexpected API response shape (no choices[0].message.content):\n\n" + Truncate(responseText, 1000));
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
