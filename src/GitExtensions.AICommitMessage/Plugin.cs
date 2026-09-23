using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands.Settings;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Plugins;
using GitExtensions.Extensibility.Settings;
using GitUIPluginInterfaces;

namespace GitExtensions.AICommitMessage
{
    /// <summary>
    /// Adds an "AI message" button to the Commit dialog toolbar (next to "Commit templates").
    /// Clicking it sends the staged diff to a user-configured OpenAI-compatible endpoint and puts the
    /// suggested message in the commit box. The feature is OFF by default and the diff is sent ONLY
    /// on an explicit click — never when the dialog opens or a menu is browsed.
    /// </summary>
    [Export(typeof(IGitPlugin))]
    [Export(typeof(IGitPluginForCommit))]
    [Export(typeof(IGitPluginForRepository))]
    public sealed class Plugin : GitPluginBase, IGitPluginForRepository, IGitPluginForCommit
    {
        private const string Title = "AI commit message";
        private const string ButtonName = "aiCommitMessageButton";
        private const string ButtonText = "✨ AI message";

        private const string DefaultSystemPrompt =
            "你是一名资深软件工程师，正在为暂存的 diff 编写 git 提交信息。\n" +
            "按照以下确切格式编写提交信息：\n" +
            "\n" +
            "格式为:  类型: 修改描述(最多40个字符), 结尾不加句号。\n" +
            "主题行：使用祈使语气，，\n" +
            "类型： 依据修改内容使用如下关键词：\n" +
            "feat、fix、refactor、docs、test 等特性描述\n" +
            "\n" +
            "修改描述：说明改了什么（WHAT），尤其是为什么改（WHY）\n" +
            "指南：\n" +
            "从 diff 中推断意图；绝不要编造 diff 中不存在的内容。\n" +
            "简洁且具体；避免“更新了一些代码”这类空话。\n" +
            "只输出原始提交信息文本：不要 markdown，不要代码围栏，不要\n" +
            "外层引号，前后不要任何评论。";

        private const string ChatApiTypeLabel = "chat";
        private const string ResponseApiTypeLabel = "response";
        private static readonly string[] ApiTypeValues = { ChatApiTypeLabel, ResponseApiTypeLabel };

        private readonly BoolSetting _enabled = new("Enabled", "Enable AI commit message generation", false);
        private readonly StringSetting _baseUrl = new("API base URL", "API base URL (OpenAI-compatible)", "https://api.openai.com/v1");

        // OpenAI-compatible endpoints expose either the classic Chat Completions API or the newer
        // Responses API. The choice only changes which endpoint is called and how the reply is parsed.
        private readonly ChoiceSetting _apiType = new(
            "API type",
            "接口类型",
            new List<string>(ApiTypeValues),
            ChatApiTypeLabel);
        private readonly PasswordSetting _apiKey = new("API key", "API key", "");
        private readonly List<string> _modelValues = new();
        private readonly ChoiceSetting _model;
        private readonly StringSetting _systemPrompt = new("System prompt", "System prompt", DefaultSystemPrompt);
        private const string UnlimitedDiffSizeLabel = "不限制";
        private static readonly string[] MaxDiffSizeValues = { "10000", "50000", UnlimitedDiffSizeLabel };

        // Byte budget for the staged diff. A dropdown keeps the accepted values explicit; "不限制" means
        // the whole diff is sent. Default is 10000 bytes (as in 10000 = 10 kB).
        private readonly ChoiceSetting _maxDiffSize = new(
            "Max diff size (bytes)",
            "Max diff size sent to the model, in bytes",
            new List<string>(MaxDiffSizeValues),
            "10000");

        // URL and API key use custom text boxes so changes can trigger model discovery. ChoiceSetting
        // supplies the model's native, non-editable ComboBox through Git Extensions' settings UI.
        private readonly TextBox _baseUrlControl = new() { Width = 320 };
        private readonly TextBox _apiKeyControl = new() { Width = 320, UseSystemPasswordChar = true };
        private readonly Label _modelStatusControl = new()
        {
            AutoSize = true,
            Text = "模型状态：尚未获取模型列表。"
        };
        private readonly PseudoSetting _modelStatus;
        private readonly ToolTip _modelToolTip = new();
        private readonly System.Windows.Forms.Timer _modelRefreshTimer = new() { Interval = 700 };
        private CancellationTokenSource? _modelRefreshCancellation;

        // True while the API URL or key has been edited without a successful model list load since.
        // Generation is refused in that window so a model from the previous endpoint is never sent.
        private bool _modelListStale;

        private IGitModule? _module;
        private bool _idleHooked;

        public Plugin()
            : base(hasSettings: true)
        {
            Name = "AI Commit Message";
            Description = "Generate a commit message from the staged diff via an OpenAI-compatible API";
            Icon = LoadIcon();
            _model = new ChoiceSetting("Model", "Model", _modelValues);
            _modelStatus = new PseudoSetting(_modelStatusControl, "Model status");
            _modelRefreshTimer.Tick += OnModelRefreshTimerTick;
        }

        public override IEnumerable<ISetting> GetSettings()
        {
            ConfigureSettingsControls();

            yield return _enabled;
            yield return _baseUrl;
            yield return _apiType;
            // Keep the API key before the model: entering the endpoint and key can now populate the model list.
            yield return _apiKey;
            yield return _model;
            yield return _modelStatus;
            yield return _maxDiffSize;

            // Show the system prompt in a tall, multi-line box so it's readable and editable.
            _systemPrompt.CustomControl = new TextBox
            {
                Multiline = true,
                Height = 160,
                WordWrap = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,

                // Pre-fill from the effective value as well, so the box never shows up blank even if the
                // host does not feed the control through its binding.
                Text = _systemPrompt.ValueOrDefault(Settings)
            };
            yield return _systemPrompt;
        }

        private void ConfigureSettingsControls()
        {
            EnsureSettingDefaults();

            _baseUrl.CustomControl = _baseUrlControl;
            _apiKey.CustomControl = _apiKeyControl;

            _baseUrlControl.Text = _baseUrl.ValueOrDefault(Settings) ?? string.Empty;
            _apiKeyControl.Text = _apiKey.ValueOrDefault(Settings) ?? string.Empty;

            string? configuredModel = _model.ValueOrDefault(Settings)?.Trim();
            if (!string.IsNullOrWhiteSpace(configuredModel)
                && !_modelValues.Contains(configuredModel, StringComparer.OrdinalIgnoreCase))
            {
                _modelValues.Add(configuredModel);
            }

            SetModelStatus("模型状态：尚未获取模型列表。");
            _baseUrlControl.TextChanged -= OnModelSourceChanged;
            _apiKeyControl.TextChanged -= OnModelSourceChanged;
            _baseUrlControl.TextChanged += OnModelSourceChanged;
            _apiKeyControl.TextChanged += OnModelSourceChanged;
        }

        // Git Extensions hosts plugin settings at SettingLevel.Unknown (the AppSettings container), and
        // the control bindings there load the STORED value only: a setting that was never saved renders as
        // an empty field even though its declared default is what generation actually uses. Persist those
        // defaults once so the settings page shows the real values. The model list deliberately has no
        // default, so it stays empty until the API returns models.
        private void EnsureSettingDefaults()
        {
            if (string.IsNullOrWhiteSpace(_baseUrl[Settings]))
            {
                _baseUrl[Settings] = "https://api.openai.com/v1";
            }

            if (string.IsNullOrWhiteSpace(_systemPrompt[Settings]))
            {
                _systemPrompt[Settings] = DefaultSystemPrompt;
            }

            string? diffSize = _maxDiffSize[Settings];
            if (string.IsNullOrWhiteSpace(diffSize)
                || !MaxDiffSizeValues.Any(value => string.Equals(value, diffSize.Trim(), StringComparison.Ordinal)))
            {
                _maxDiffSize[Settings] = "10000";
            }

            string? apiType = _apiType[Settings];
            if (string.IsNullOrWhiteSpace(apiType)
                || !ApiTypeValues.Any(value => string.Equals(value, apiType.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _apiType[Settings] = ChatApiTypeLabel;
            }

            // An unset BoolSetting renders as a grey three-state box; store the default so the checkbox
            // is a plain unchecked box (same effective value) at every level.
            if (_enabled[Settings] is null)
            {
                _enabled[Settings] = false;
            }

            // The "Global for all repositories" level reads its own cache of the settings file, so flush
            // what we just materialised - otherwise that level would still show empty boxes.
            DistributedSettings.CreateGlobal().Save();
        }

        private void OnModelSourceChanged(object? sender, EventArgs e)
        {
            _modelRefreshTimer.Stop();
            _modelRefreshCancellation?.Cancel();
            _modelListStale = true;

            if (string.IsNullOrWhiteSpace(_baseUrlControl.Text))
            {
                SetModelStatus("模型状态：请先填写 API URL。");
                return;
            }

            SetModelStatus("模型状态：等待获取模型列表…");
            _modelRefreshTimer.Start();
        }

        private void SetModelStatus(string status)
        {
            _modelStatusControl.Text = status;
        }

        private async void OnModelRefreshTimerTick(object? sender, EventArgs e)
        {
            _modelRefreshTimer.Stop();
            await RefreshModelsAsync();
        }

        private async Task RefreshModelsAsync()
        {
            string baseUrl = _baseUrlControl.Text.Trim();
            string apiKey = _apiKeyControl.Text.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                SetModelStatus("模型状态：请先填写 API URL。");
                return;
            }

            SetModelStatus("模型状态：正在获取模型列表…");
            _modelRefreshCancellation?.Cancel();
            CancellationTokenSource cancellation = new();
            _modelRefreshCancellation = cancellation;

            try
            {
                OpenAiClient client = new(baseUrl, apiKey, string.Empty, GetApiType());
                IReadOnlyList<string> models = await client.GetModelsAsync(cancellation.Token);
                if (cancellation.IsCancellationRequested
                    || !string.Equals(baseUrl, _baseUrlControl.Text.Trim(), StringComparison.Ordinal)
                    || !string.Equals(apiKey, _apiKeyControl.Text.Trim(), StringComparison.Ordinal))
                {
                    return;
                }

                string preferredModel = _model.CustomControl?.SelectedItem?.ToString()?.Trim()
                    ?? _model.ValueOrDefault(Settings)?.Trim()
                    ?? string.Empty;
                PopulateModelValues(models, preferredModel);
                _modelListStale = false;
                SetModelStatus($"模型状态：已获取 {models.Count} 个可用模型。");
                if (_model.CustomControl is ComboBox modelControl)
                {
                    _modelToolTip.SetToolTip(modelControl, $"已获取 {models.Count} 个可用模型。");
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // A newer URL or API key change superseded this request.
            }
            catch (OperationCanceledException)
            {
                SetModelStatus("模型状态：获取模型列表超时（6 秒）。");
            }
            catch (TimeoutException)
            {
                SetModelStatus("模型状态：获取模型列表超时（6 秒）。");
            }
            catch (Exception ex)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    SetModelStatus("模型状态：获取失败：" + ex.Message);
                    if (_model.CustomControl is ComboBox modelControl)
                    {
                        _modelToolTip.SetToolTip(modelControl, "获取模型失败：" + ex.Message);
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(_modelRefreshCancellation, cancellation))
                {
                    _modelRefreshCancellation = null;
                }
                cancellation.Dispose();
            }
        }

        private void PopulateModelValues(IReadOnlyList<string> models, string preferredModel)
        {
            List<string> values = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.Count == 0)
            {
                return;
            }

            _modelValues.Clear();
            _modelValues.AddRange(values);

            if (_model.CustomControl is ComboBox modelControl)
            {
                modelControl.BeginUpdate();
                try
                {
                    modelControl.Items.Clear();
                    modelControl.Items.AddRange(_modelValues.ToArray());
                    int selectedIndex = _modelValues.FindIndex(model =>
                        string.Equals(model, preferredModel, StringComparison.OrdinalIgnoreCase));
                    modelControl.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
                }
                finally
                {
                    modelControl.EndUpdate();
                }
            }
        }

        // Reads the API type dropdown: "chat" (Chat Completions) or "response" (Responses API).
        private string GetApiType()
        {
            string? value = _apiType.ValueOrDefault(Settings);
            return string.Equals(value?.Trim(), ResponseApiTypeLabel, StringComparison.OrdinalIgnoreCase)
                ? ResponseApiTypeLabel
                : ChatApiTypeLabel;
        }

        // Reads the byte budget from the dropdown: a number for the explicit limits, 0 for "不限制".
        private int GetMaxDiffBytes()
        {
            string? value = _maxDiffSize.ValueOrDefault(Settings);
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value.Trim(), UnlimitedDiffSizeLabel, StringComparison.Ordinal))
            {
                return 0;
            }

            return int.TryParse(value.Trim(), out int bytes) && bytes > 0 ? bytes : 0;
        }

        // Cuts the text so the UTF-8 byte count fits the budget without splitting a surrogate pair.
        private static string TruncateToUtf8Bytes(string text, int maxBytes)
        {
            if (maxBytes <= 0 || Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }

            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = low + ((high - low + 1) / 2);
                if (Encoding.UTF8.GetByteCount(text, 0, mid) <= maxBytes)
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (low > 0 && char.IsHighSurrogate(text[low - 1]))
            {
                low--;
            }

            return text.Substring(0, low);
        }

        public override void Register(IGitUICommands gitUiCommands)
        {
            base.Register(gitUiCommands);
            _module = gitUiCommands.Module;
            gitUiCommands.PreCommit += OnPreCommit;
            gitUiCommands.PostCommit += OnPostCommit;
        }

        public override void Unregister(IGitUICommands gitUiCommands)
        {
            gitUiCommands.PreCommit -= OnPreCommit;
            gitUiCommands.PostCommit -= OnPostCommit;
            UnhookIdle();
            _modelRefreshTimer.Stop();
            _modelRefreshCancellation?.Cancel();
            base.Unregister(gitUiCommands);
        }

        /// <summary>Plugins menu entry — just open the settings page.</summary>
        public override bool Execute(GitUIEventArgs args)
        {
            args.GitUICommands.StartSettingsDialog(this);
            return false;
        }

        // PreCommit fires right before the Commit dialog is created. We can't touch the form yet, so we
        // wait for the app to go idle (the form is shown by then) and inject the button into its toolbar.
        private void OnPreCommit(object? sender, GitUIEventArgs e)
        {
            if (!_enabled.ValueOrDefault(Settings))
            {
                return;
            }

            HookIdle();
        }

        private void OnPostCommit(object? sender, GitUIPostActionEventArgs e) => UnhookIdle();

        private void HookIdle()
        {
            if (_idleHooked)
            {
                return;
            }

            _idleHooked = true;
            Application.Idle += OnApplicationIdle;
        }

        private void UnhookIdle()
        {
            if (!_idleHooked)
            {
                return;
            }

            _idleHooked = false;
            Application.Idle -= OnApplicationIdle;
        }

        private void OnApplicationIdle(object? sender, EventArgs e)
        {
            Form? form = FindOpenForm("FormCommit");
            if (form is null)
            {
                return; // dialog not visible yet — try again on the next idle
            }

            UnhookIdle();
            try
            {
                InjectButton(form);
            }
            catch
            {
                // Best effort: if the toolbar layout changed in this GE build, just skip the button.
            }
        }

        private void InjectButton(Form form)
        {
            ToolStripItem? templatesItem = GetMember(form, "commitTemplatesToolStripMenuItem") as ToolStripItem;
            ToolStrip? host = GetMember(form, "toolbarCommit") as ToolStrip;

            int insertIndex = -1;
            if (host is not null && templatesItem is not null)
            {
                int idx = host.Items.IndexOf(templatesItem);
                if (idx >= 0)
                {
                    insertIndex = idx + 1;
                }
            }

            host ??= AllControls(form).OfType<ToolStrip>()
                        .FirstOrDefault(ts => templatesItem is not null && ts.Items.Contains(templatesItem))
                     ?? AllControls(form).OfType<ToolStrip>().FirstOrDefault();

            if (host is null)
            {
                return;
            }

            // Avoid adding a second button if this form was already processed.
            if (host.Items.Cast<ToolStripItem>().Any(i => i.Name == ButtonName))
            {
                return;
            }

            ToolStripButton button = new()
            {
                Name = ButtonName,
                Text = ButtonText,
                Image = Icon,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "Generate a commit message from the staged diff"
            };
            button.Click += async (_, _) => await OnGenerateClickedAsync(form, button).ConfigureAwait(true);

            if (insertIndex >= 0 && insertIndex <= host.Items.Count)
            {
                host.Items.Insert(insertIndex, button);
            }
            else
            {
                host.Items.Add(button);
            }
        }

        private async Task OnGenerateClickedAsync(Form form, ToolStripButton button)
        {
            string? workingDir = _module?.WorkingDir;
            if (string.IsNullOrEmpty(workingDir))
            {
                return;
            }

            // Read settings on the UI thread.
            string baseUrl = _baseUrl.ValueOrDefault(Settings) ?? string.Empty;
            string model = _model.ValueOrDefault(Settings) ?? string.Empty;
            string apiKey = _apiKey.ValueOrDefault(Settings) ?? string.Empty;
            string apiType = GetApiType();
            string systemPrompt = _systemPrompt.ValueOrDefault(Settings) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                systemPrompt = DefaultSystemPrompt;
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                MessageBox.Show(form,
                    "请先填写 API URL 和 API Key，等待模型列表加载后选择一个模型。",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_modelListStale)
            {
                MessageBox.Show(form,
                    "模型列表尚未成功获取：API URL 或 API Key 已更改。\n\n"
                    + "请打开 设置 → 插件 → AI Commit Message，确认模型列表已加载后再生成。",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int maxDiffBytes = GetMaxDiffBytes();

            string? originalText = button.Text;
            button.Enabled = false;
            button.Text = "Generating…";
            try
            {
                // Off the UI thread; the continuation resumes on the UI thread to update the form.
                string message = await Task.Run(() => GenerateAsync(workingDir!, baseUrl, apiKey, model, systemPrompt, maxDiffBytes, apiType));
                if (!string.IsNullOrEmpty(message))
                {
                    SetCommitMessage(form, message);
                }
            }
            catch (NoStagedChangesException)
            {
                MessageBox.Show(form,
                    "No staged changes were found. Stage the files you want to commit, then click again.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(form,
                    "Failed to generate a commit message:\n\n" + ex.Message,
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                button.Text = originalText;
                button.Enabled = true;
            }
        }

        private static async Task<string> GenerateAsync(string workingDir, string baseUrl, string apiKey, string model, string systemPrompt, int maxDiffBytes, string apiType)
        {
            string diff = GitHelper.GetStagedDiff(workingDir);
            if (string.IsNullOrWhiteSpace(diff))
            {
                throw new NoStagedChangesException();
            }

            if (maxDiffBytes > 0 && Encoding.UTF8.GetByteCount(diff) > maxDiffBytes)
            {
                diff = TruncateToUtf8Bytes(diff, maxDiffBytes)
                    + "\n\n[diff truncated to fit the configured byte limit]";
            }

            OpenAiClient client = new(baseUrl, apiKey, model, apiType);
            return await client.CompleteAsync(systemPrompt, diff).ConfigureAwait(false);
        }

        // Sets the commit message using FormCommit's own ReplaceMessage(string), falling back to Message.Text.
        private static void SetCommitMessage(Form form, string message)
        {
            MethodInfo? replace = form.GetType().GetMethod(
                "ReplaceMessage",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: new[] { typeof(string) }, modifiers: null);
            if (replace is not null)
            {
                replace.Invoke(form, new object[] { message });
                return;
            }

            if (GetMember(form, "Message") is Control messageControl)
            {
                messageControl.Text = message;
                messageControl.Focus();
            }
        }

        private static object? GetMember(Form form, string name)
        {
            FieldInfo? field = form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(form);
        }

        private static Form? FindOpenForm(string typeName)
        {
            foreach (Form f in Application.OpenForms)
            {
                if (f.GetType().Name == typeName && f.Visible && !f.IsDisposed)
                {
                    return f;
                }
            }

            return null;
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control descendant in AllControls(child))
                {
                    yield return descendant;
                }
            }
        }

        private static Image? LoadIcon()
        {
            try
            {
                using System.IO.Stream? stream = typeof(Plugin).Assembly
                    .GetManifestResourceStream("GitExtensions.AICommitMessage.Resources.icon.png");
                return stream is null ? null : Image.FromStream(stream);
            }
            catch
            {
                return null;
            }
        }

        private sealed class NoStagedChangesException : Exception
        {
        }
    }
}
