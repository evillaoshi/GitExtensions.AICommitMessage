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
        private const string AiMenuName = "aiCommitDropDownButton";
        private const string AiMenuText = "🤖 AI commit";
        private const string StageMenuItemText = "生成 stage";
        private const string MessageMenuItemText = "生成 commit";

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

        private static readonly string[] FeatureSummaryBytesValues = { "8000", "20000", UnlimitedDiffSizeLabel };

        // Byte budget for the change summary that "AI commit" sends when it asks the model to pick one
        // related group of changes.
        private readonly ChoiceSetting _featureSummaryBytes = new(
            "AI feature summary (bytes)",
            "AI 分组摘要上限（字节）",
            new List<string>(FeatureSummaryBytesValues),
            "8000");

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
            yield return _featureSummaryBytes;

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

            // A failed earlier refresh leaves the list stale, which blocks both buttons. Re-arm the fetch
            // so simply reopening the settings page can clear that state.
            if (_modelListStale && !string.IsNullOrWhiteSpace(_baseUrlControl.Text))
            {
                _modelRefreshTimer.Start();
            }
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

            string? featureSummaryBytes = _featureSummaryBytes[Settings];
            if (string.IsNullOrWhiteSpace(featureSummaryBytes)
                || !FeatureSummaryBytesValues.Any(value => string.Equals(value, featureSummaryBytes.Trim(), StringComparison.Ordinal)))
            {
                _featureSummaryBytes[Settings] = "8000";
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

        // Reads the staged-diff byte budget: a number for the explicit limits, 0 for "不限制".
        private int GetMaxDiffBytes() => ReadByteLimit(_maxDiffSize);

        // Same semantics for the change summary sent to the grouping call.
        private int GetFeatureSummaryBytes() => ReadByteLimit(_featureSummaryBytes);

        private int ReadByteLimit(ChoiceSetting setting)
        {
            string? value = setting.ValueOrDefault(Settings);
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value.Trim(), UnlimitedDiffSizeLabel, StringComparison.Ordinal))
            {
                return 0;
            }

            return int.TryParse(value.Trim(), out int bytes) && bytes > 0 ? bytes : 0;
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

            AddAiMenu(host, insertIndex, form);
        }

        // Mirrors Git Extensions' own "Commit templates" toolbar entry: one drop-down button with the two
        // AI actions, so the toolbar stays tidy and the choice is explicit.
        private void AddAiMenu(ToolStrip host, int insertIndex, Form form)
        {
            // Added at most once - the idle hook can fire again for the same form.
            if (host.Items.Cast<ToolStripItem>().Any(item => item.Name == AiMenuName))
            {
                return;
            }

            ToolStripDropDownButton menu = new()
            {
                Name = AiMenuName,
                Text = AiMenuText,
                Image = Icon,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ToolTipText = "AI：先挑选并暂存一组相关改动，或只根据已暂存的改动生成提交信息"
            };

            menu.DropDownItems.Add(CreateMenuItem(
                StageMenuItemText,
                "挑选一组相关的未暂存改动，暂存它们之后再生成提交信息",
                () => OnAiCommitClickedAsync(form, menu)));
            menu.DropDownItems.Add(CreateMenuItem(
                MessageMenuItemText,
                "根据已经暂存的改动生成提交信息",
                () => OnGenerateClickedAsync(form, menu)));

            if (insertIndex >= 0 && insertIndex <= host.Items.Count)
            {
                host.Items.Insert(insertIndex, menu);
            }
            else
            {
                host.Items.Add(menu);
            }
        }

        private static ToolStripMenuItem CreateMenuItem(string text, string toolTip, Func<Task> onClick)
        {
            ToolStripMenuItem item = new()
            {
                Text = text,
                ToolTipText = toolTip
            };
            item.Click += async (_, _) => await onClick().ConfigureAwait(true);
            return item;
        }

        private async Task OnGenerateClickedAsync(Form form, ToolStripItem trigger)
        {
            string? workingDir = GetDialogModule(form)?.WorkingDir;
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
                ShowMessage(form,
                    "请先填写 API URL 和 API Key，等待模型列表加载后选择一个模型。",
                    MessageBoxIcon.Information);
                return;
            }

            if (_modelListStale)
            {
                ShowMessage(form,
                    "模型列表尚未成功获取：API URL 或 API Key 已更改。\n\n"
                    + "请打开 设置 → 插件 → AI Commit Message，确认模型列表已加载后再生成。",
                    MessageBoxIcon.Information);
                return;
            }

            int maxDiffBytes = GetMaxDiffBytes();

            string? originalText = trigger.Text;
            trigger.Enabled = false;
            trigger.Text = "Generating…";
            try
            {
                // Off the UI thread; the continuation resumes on the UI thread to update the form.
                string message = await Task.Run(() => GenerateAsync(workingDir!, baseUrl, apiKey, model, systemPrompt, maxDiffBytes, apiType));
                if (form.IsDisposed || trigger.IsDisposed)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(message) && !SetCommitMessage(form, message))
                {
                    ShowGeneratedMessage(form, message);
                }
            }
            catch (NoStagedChangesException)
            {
                ShowMessage(form,
                    "No staged changes were found. Stage the files you want to commit, then click again.",
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage(form,
                    "Failed to generate a commit message:\n\n" + ex.Message,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (!trigger.IsDisposed)
                {
                    trigger.Text = originalText;
                    trigger.Enabled = true;
                }
            }
        }

        /// <summary>
        /// "AI commit": let the model pick one related group out of the unstaged changes, confirm the file
        /// list with the user, stage exactly those files, refresh the dialog and fill in the commit message.
        /// Nothing is ever committed automatically.
        /// </summary>
        private async Task OnAiCommitClickedAsync(Form form, ToolStripItem trigger)
        {
            // The dialog knows which repository it belongs to; _module is only the last one registered,
            // so with several repository windows open it can point somewhere else entirely.
            IGitModule? module = GetDialogModule(form);
            string? workingDir = module?.WorkingDir;
            if (module is null || string.IsNullOrEmpty(workingDir))
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
                ShowMessage(form,
                    "请先填写 API URL 和 API Key，等待模型列表加载后选择一个模型。",
                    MessageBoxIcon.Information);
                return;
            }

            if (_modelListStale)
            {
                ShowMessage(form,
                    "模型列表尚未成功获取：API URL 或 API Key 已更改。\n\n"
                    + "请打开 设置 → 插件 → AI Commit Message，确认模型列表已加载后再使用 AI commit。",
                    MessageBoxIcon.Information);
                return;
            }

            string? originalText = trigger.Text;
            trigger.Enabled = false;
            trigger.Text = "Analyzing…";
            try
            {
                int summaryBytes = GetFeatureSummaryBytes();
                ChangeSet changeSet = await Task.Run(() => GitHelper.GetChangeSet(workingDir!, summaryBytes)).ConfigureAwait(true);
                if (form.IsDisposed || trigger.IsDisposed)
                {
                    return;
                }

                if (changeSet.UnstagedPaths.Count == 0)
                {
                    ShowMessage(form, "没有未暂存的改动可供分组。请先修改文件，再点 🤖 AI commit。", MessageBoxIcon.Information);
                    return;
                }

                // Canonical spelling of every candidate: a differently-cased answer from the model must
                // never stage a path that the confirmation dialog did not list.
                Dictionary<string, string> candidates = new(StringComparer.OrdinalIgnoreCase);
                foreach (string path in changeSet.UnstagedPaths)
                {
                    candidates[path] = path;
                }

                List<string> chosen;
                string feature;
                string reason;
                try
                {
                    OpenAiClient client = new(baseUrl, apiKey, model, apiType);
                    FeatureSelection selection = await client
                        .SelectFeatureAsync(changeSet.Summary, changeSet.UnstagedPaths)
                        .ConfigureAwait(true);
                    if (form.IsDisposed || trigger.IsDisposed)
                    {
                        return;
                    }

                    chosen = selection.Files
                        .Select(file => candidates.TryGetValue(file, out string? canonical) ? canonical : null)
                        .Where(path => path is not null)
                        .Select(path => path!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    feature = selection.Feature;
                    reason = selection.Reason;
                }
                catch (Exception ex)
                {
                    if (form.IsDisposed || trigger.IsDisposed)
                    {
                        return;
                    }

                    // Never stage everything silently: the fallback needs an explicit yes.
                    if (!AskYesNo(form,
                        "AI 未能挑出改动分组：\n\n" + ex.Message + "\n\n是否改为暂存全部未暂存文件？",
                        MessageBoxIcon.Warning))
                    {
                        return;
                    }

                    chosen = changeSet.UnstagedPaths.ToList();
                    feature = "全部未暂存改动";
                    reason = "AI 分组失败，由用户选择暂存全部未暂存文件。";
                }

                if (chosen.Count == 0)
                {
                    ShowMessage(form,
                        "AI 没有挑出可暂存的改动（它返回的路径都不在未暂存清单里）。\n\n"
                        + "功能：" + feature + "\n原因：" + reason,
                        MessageBoxIcon.Information);
                    return;
                }

                if (!ConfirmStaging(form, feature, reason, chosen))
                {
                    return;
                }

                // The work tree may have changed while the model was answering (or while the dialog sat
                // open), so re-read it and only stage paths that are still unstaged right now.
                IReadOnlyList<string> stillUnstaged = await Task.Run(
                    () => (IReadOnlyList<string>)GitHelper.GetChangedFiles(workingDir!)
                        .Where(entry => entry.HasUnstagedChanges)
                        .Select(entry => entry.Path)
                        .ToList()).ConfigureAwait(true);
                if (form.IsDisposed || trigger.IsDisposed)
                {
                    return;
                }

                HashSet<string> currentPaths = new(stillUnstaged, StringComparer.OrdinalIgnoreCase);
                List<string> stageable = chosen.Where(currentPaths.Contains).ToList();
                if (stageable.Count != chosen.Count)
                {
                    ShowMessage(form,
                        "以下文件在确认之后已不再是未暂存状态，将被跳过：\n\n"
                        + string.Join(Environment.NewLine, chosen.Where(path => !currentPaths.Contains(path))),
                        MessageBoxIcon.Information);
                }

                if (stageable.Count == 0)
                {
                    ShowMessage(form, "需要暂存的文件都已不再处于未暂存状态，请刷新后重试。", MessageBoxIcon.Information);
                    return;
                }

                trigger.Text = "Staging…";
                IReadOnlyList<GitItemStatus>? knownItems = GetUnstagedItems(form);

                // Staged on the UI thread on purpose: this is exactly what the dialog's own stage buttons
                // do, so the index write cannot race a concurrent rescan.
                string stageOutput = string.Empty;
                bool staged = StageSelection(module, changeSet, knownItems, stageable, out stageOutput);
                bool refreshed = RescanCommitForm(form);
                if (!staged)
                {
                    // git may have updated the index before reporting an error, so refresh either way.
                    ShowMessage(form, "暂存失败：\n\n" + stageOutput, MessageBoxIcon.Error);
                    return;
                }

                if (!refreshed)
                {
                    ShowMessage(form,
                        "文件已暂存，但未能刷新提交对话框；请按 F5 刷新列表后继续。",
                        MessageBoxIcon.Information);
                }

                trigger.Text = "Generating…";
                int maxDiffBytes = GetMaxDiffBytes();
                string message = await Task.Run(() => GenerateAsync(
                    workingDir!, baseUrl, apiKey, model, systemPrompt, maxDiffBytes, apiType)).ConfigureAwait(true);
                if (form.IsDisposed || trigger.IsDisposed)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(message) && !SetCommitMessage(form, message))
                {
                    ShowGeneratedMessage(form, message);
                }
            }
            catch (NoStagedChangesException)
            {
                ShowMessage(form, "暂存后没有可读取的改动，无法生成提交信息。", MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowMessage(form, "AI commit 失败：\n\n" + ex.Message, MessageBoxIcon.Error);
            }
            finally
            {
                if (!trigger.IsDisposed)
                {
                    trigger.Text = originalText;
                    trigger.Enabled = true;
                }
            }
        }

        // Reads the commit dialog's unstaged list through reflection (the plugin has no reference to
        // GitUI). Returns null when the layout is unknown; the caller then stages plain paths instead.
        private static IReadOnlyList<GitItemStatus>? GetUnstagedItems(Form form)
        {
            object? list = GetMember(form, "Unstaged");
            if (list is null)
            {
                return null;
            }

            PropertyInfo? property = list.GetType().GetProperty(
                "GitItemStatuses", BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(list) as IReadOnlyList<GitItemStatus>;
        }

        // Stages exactly the confirmed paths through Git Extensions' own staging code, so its index
        // bookkeeping and submodule handling stay consistent. Paths never reach a shell.
        private static bool StageSelection(
            IGitModule module,
            ChangeSet changeSet,
            IReadOnlyList<GitItemStatus>? knownItems,
            IReadOnlyList<string> chosen,
            out string output)
        {
            output = string.Empty;
            List<GitItemStatus> items = new();
            foreach (string path in chosen)
            {
                // Exact match only: the dialog's item carries IsDeleted/IsNew, which decide whether the
                // index update has to remove the entry instead of adding it.
                GitItemStatus? match = knownItems?.FirstOrDefault(
                    item => string.Equals(item.Name, path, StringComparison.Ordinal));
                if (match is not null)
                {
                    items.Add(match);
                    continue;
                }

                GitItemStatus item = new(path);
                StatusEntry? entry = changeSet.UnstagedEntries.FirstOrDefault(
                    candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
                if (entry?.IsDeletedInWorkTree == true)
                {
                    item.IsDeleted = true;
                }

                items.Add(item);
            }

            if (items.Count == 0)
            {
                output = "没有要暂存的文件。";
                return false;
            }

            return module.StageFiles(items, out output);
        }

        // FormCommit refreshes its staged/unstaged lists through this private method.
        private static bool RescanCommitForm(Form form)
        {
            MethodInfo? rescan = form.GetType().GetMethod(
                "RescanChanges",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (rescan is null)
            {
                return false;
            }

            rescan.Invoke(form, null);
            return true;
        }

        // Shows exactly which files are about to be staged, before the index is touched.
        private static bool ConfirmStaging(Form owner, string feature, string reason, IReadOnlyList<string> files)
        {
            using Form dialog = new()
            {
                Text = "AI commit - 确认暂存内容",
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(680, 460)
            };

            Label header = new()
            {
                Dock = DockStyle.Top,
                Height = 72,
                Padding = new Padding(10),
                Text = "功能：" + feature + Environment.NewLine + "原因：" + reason
            };

            ListBox list = new()
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                SelectionMode = SelectionMode.None
            };
            foreach (string file in files)
            {
                list.Items.Add(file);
            }

            FlowLayoutPanel buttons = new()
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8)
            };

            Button confirm = new()
            {
                Text = $"暂存并生成信息（{files.Count} 个文件）",
                DialogResult = DialogResult.OK,
                AutoSize = true
            };
            Button cancel = new()
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                AutoSize = true
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(confirm);

            dialog.Controls.Add(list);
            dialog.Controls.Add(header);
            dialog.Controls.Add(buttons);
            dialog.AcceptButton = confirm;
            dialog.CancelButton = cancel;

            return dialog.ShowDialog(owner) == DialogResult.OK;
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
                // Keep the note inside the configured budget instead of adding it on top.
                const string truncationNote = "\n\n[diff truncated to fit the configured byte limit]";
                int noteBytes = Encoding.UTF8.GetByteCount(truncationNote);
                diff = noteBytes < maxDiffBytes
                    ? GitHelper.TruncateToUtf8Bytes(diff, maxDiffBytes - noteBytes) + truncationNote
                    : GitHelper.TruncateToUtf8Bytes(diff, maxDiffBytes);
            }

            OpenAiClient client = new(baseUrl, apiKey, model, apiType);
            return await client.CompleteAsync(systemPrompt, diff).ConfigureAwait(false);
        }

        // Sets the commit message using FormCommit's own ReplaceMessage(string), falling back to Message.Text.
        // Returns false when neither exists, so the caller can show the text instead of losing it.
        private static bool SetCommitMessage(Form form, string message)
        {
            MethodInfo? replace = form.GetType().GetMethod(
                "ReplaceMessage",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: new[] { typeof(string) }, modifiers: null);
            if (replace is not null)
            {
                replace.Invoke(form, new object[] { message });
                return true;
            }

            if (GetMember(form, "Message") is Control messageControl)
            {
                messageControl.Text = message;
                messageControl.Focus();
                return true;
            }

            return false;
        }

        // The message cost a real API call: show it rather than dropping it silently.
        private static void ShowGeneratedMessage(Form owner, string message)
            => MessageBox.Show(
                owner.IsDisposed ? null : owner,
                "无法把生成的信息写入提交框（宿主界面可能已变化），请手动复制：\n\n" + message,
                Title,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

        // The dialog belongs to a specific repository, so ask it instead of trusting the last registered
        // plugin instance (_module), which can belong to a different repository window.
        private IGitModule? GetDialogModule(Form form)
        {
            PropertyInfo? module = form.GetType().GetProperty(
                "Module", BindingFlags.Instance | BindingFlags.Public);
            return module?.GetValue(form) as IGitModule ?? _module;
        }

        // Dialog helpers that tolerate the dialog being closed while a model call was in flight.
        private static void ShowMessage(Form owner, string text, MessageBoxIcon icon)
            => MessageBox.Show(owner.IsDisposed ? null : owner, text, Title, MessageBoxButtons.OK, icon);

        private static bool AskYesNo(Form owner, string text, MessageBoxIcon icon)
            => MessageBox.Show(
                owner.IsDisposed ? null : owner, text, Title, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;

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
