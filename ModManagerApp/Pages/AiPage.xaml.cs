using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SMM
{
    /// <summary>
    /// AI 设置：把接口地址 / 密钥 / 模型一次性写进 InGameCodex 和 StardewAI 的 config.json，
    /// 省得进游戏在两个设置面板里各填一遍。
    ///
    /// 注意两个 Mod 的配置层级不一样：
    ///   InGameCodex → .Ai.BaseUrl / .Ai.ApiKey / .Ai.Model / .Ai.Preset
    ///   StardewAI   → .BaseUrl    / .ApiKey    / .Model    / .Preset
    /// </summary>
    public partial class AiPage : UserControl, IPage
    {
        private const string CodexFolder = "InGameCodex";
        private const string AiFolder = "StardewAI";

        private readonly IShell _shell;
        private bool _loading;
        private bool _busy;
        private List<string> _fetched;      // 从接口拉到的模型清单

        private AppEnv Env => _shell.Env;

        public AiPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
        }

        public void OnActivated() => Reload();

        // ==================== 读取 ====================

        private void Reload()
        {
            _loading = true;
            try
            {
                var models = AiModels.Merge(_fetched, null).ToList();
                CmbModel.ItemsSource = models;

                var presets = AiPreset.All.Select(p => p.Label).ToList();
                CmbPreset.ItemsSource = presets;

                string codex = JsonEdit.ConfigPathOf(Env, CodexFolder);
                string ai = JsonEdit.ConfigPathOf(Env, AiFolder);

                string baseUrl = JsonEdit.Read(codex, ".Ai.BaseUrl") ?? JsonEdit.Read(ai, ".BaseUrl") ?? "";
                string key = JsonEdit.Read(codex, ".Ai.ApiKey") ?? JsonEdit.Read(ai, ".ApiKey") ?? "";
                string model = JsonEdit.Read(codex, ".Ai.Model") ?? JsonEdit.Read(ai, ".Model") ?? "";

                TxtBaseUrl.Text = baseUrl;
                TxtApiKey.Text = key;
                TxtModel.Text = model;

                var preset = AiPreset.Get(AiPreset.GuessId(baseUrl));
                CmbPreset.SelectedItem = preset?.Label;

                RefreshModelChoices(model);
                UpdateCurrentTexts();
                TxtHint.Text = "";
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>模型下拉：接口拉到的清单 + 常备款（当前值永远保留）。</summary>
        private void RefreshModelChoices(string current)
        {
            string cur = string.IsNullOrWhiteSpace(current) ? (TxtModel.Text ?? "").Trim() : current;
            CmbModel.ItemsSource = AiModels.Merge(_fetched, cur);
        }

        private void UpdateCurrentTexts()
        {
            TxtCodexNow.Text = Describe(CodexFolder, ".Ai.");
            TxtAiNow.Text = Describe(AiFolder, ".");
        }

        private string Describe(string folder, string prefix)
        {
            string cfg = JsonEdit.ConfigPathOf(Env, folder);
            if (cfg == null || !File.Exists(cfg))
                return "（还没有 config.json —— 先进一次游戏，让它生成配置）";

            string url = JsonEdit.Read(cfg, prefix + "BaseUrl") ?? "";
            string model = JsonEdit.Read(cfg, prefix + "Model") ?? "";
            string key = JsonEdit.Read(cfg, prefix + "ApiKey") ?? "";
            return $"现在：{Short(url)}　·　模型 {model}　·　密钥 {KeyInfo(key)}";
        }

        private static string Short(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "(未填地址)";
            return url.Length <= 46 ? url : url.Substring(0, 44) + "…";
        }

        private static string KeyInfo(string key)
            => string.IsNullOrWhiteSpace(key) ? "未填" : key.Trim().Length + " 位";

        // ==================== 交互 ====================

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            Reload();
            SetStatus("已重新读取两个 Mod 的当前设置");
        }

        private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            var p = AiPreset.ByLabel(CmbPreset.SelectedItem as string);
            if (p == null)
                return;

            TxtBaseUrl.Text = p.BaseUrl;
            if (!string.IsNullOrWhiteSpace(p.Model))
                TxtModel.Text = p.Model;
            RefreshModelChoices(p.Model);

            SetStatus(p.Id == "custom"
                ? "选「自定义」：接口地址和模型自己填"
                : $"已填入「{p.Label}」的地址与模型" + (p.NeedsKey ? "（还需要填密钥）" : "（不需要密钥）"));
        }

        private void CmbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            if (CmbModel.SelectedItem is string m && m.Length > 0)
                TxtModel.Text = m;
        }

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            string url = (TxtBaseUrl.Text ?? "").Trim();
            if (url.Length == 0)
            {
                SetStatus("先填接口地址（BaseUrl），例如 http://127.0.0.1:52120/v1");
                return;
            }

            string key = (TxtApiKey.Text ?? "").Trim();
            _busy = true;
            SetStatus("正在向 " + url + "/models 拉取模型清单…");
            try
            {
                var list = await Task.Run(() => FetchModels(url, key));
                _fetched = list;
                RefreshModelChoices(TxtModel.Text);

                SetStatus(list.Count == 0
                    ? "接口没返回任何模型（可能路径不对或需要密钥）"
                    : $"拉到 {list.Count} 个模型，已填进右边的下拉：{string.Join("、", list.Take(6))}"
                      + (list.Count > 6 ? " …" : ""));
            }
            catch (Exception ex)
            {
                SetStatus("拉取失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            string baseUrl = (TxtBaseUrl.Text ?? "").Trim();
            string key = (TxtApiKey.Text ?? "").Trim();
            string model = (TxtModel.Text ?? "").Trim();

            if (model.Length == 0)
            {
                SetStatus("模型不能为空");
                return;
            }

            var targets = new List<(string folder, string prefix, string name)>();
            if (ChkCodex.IsChecked == true)
                targets.Add((CodexFolder, ".Ai.", "游戏内图鉴（InGameCodex）"));
            if (ChkAi.IsChecked == true)
                targets.Add((AiFolder, ".", "游戏内 AI 助手（StardewAI）"));

            if (targets.Count == 0)
            {
                SetStatus("请至少勾选一个要应用的目标");
                return;
            }

            // 预设 id：用户选的方案地址若和当前填的一致就用它，否则算自定义
            var picked = AiPreset.ByLabel(CmbPreset.SelectedItem as string);
            string presetId = AiPreset.Matches(picked, baseUrl) ? picked.Id : "custom";

            _busy = true;
            SetStatus("正在写入…");
            try
            {
                var msgs = new List<string>();
                await Task.Run(() =>
                {
                    foreach (var (folder, prefix, name) in targets)
                    {
                        string cfg = JsonEdit.ConfigPathOf(Env, folder);
                        if (cfg == null || !File.Exists(cfg))
                        {
                            msgs.Add($"{name}：还没有 config.json（先进一次游戏）");
                            continue;
                        }

                        JsonEdit.Write(Env, cfg, prefix + "BaseUrl", baseUrl);
                        JsonEdit.Write(Env, cfg, prefix + "ApiKey", key);
                        JsonEdit.Write(Env, cfg, prefix + "Model", model);
                        JsonEdit.Write(Env, cfg, prefix + "Preset", presetId);

                        // 顺手把"按方案保存的密钥"也更新掉（游戏内切方案时会用到）
                        if (presetId != "custom")
                        {
                            try { JsonEdit.Write(Env, cfg, prefix + "PresetKeys." + presetId, key); }
                            catch { /* 老配置里没有 PresetKeys 就算了 */ }
                        }

                        msgs.Add($"{name}：已写入（模型 {model}）");
                    }
                });

                UpdateCurrentTexts();
                SetStatus(string.Join("；", msgs) + "　—— 重启游戏后生效");
                _shell.Toast("AI 设置已写入：" + string.Join("；", msgs));
            }
            catch (Exception ex)
            {
                SetStatus("写入失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(Env.DataDir, "config-backup");
            if (!Directory.Exists(dir))
            {
                SetStatus("还没有备份（改过一次配置后就会自动生成）");
                return;
            }
            App.OpenFolder(dir);
        }

        // ==================== 拉模型 ====================

        /// <summary>GET {baseUrl}/models，取 OpenAI 兼容格式里的 data[].id。</summary>
        private static List<string> FetchModels(string baseUrl, string apiKey)
        {
            string url = baseUrl.TrimEnd('/') + "/models";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());

            using var resp = http.SendAsync(req).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"接口返回 {(int)resp.StatusCode} {resp.ReasonPhrase}："
                                    + (body ?? "").Trim().Substring(0, Math.Min(200, (body ?? "").Trim().Length)));

            var list = new List<string>();
            using var doc = JsonUtil.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in data.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.String)
                        list.Add(it.GetString());
                    else if (it.ValueKind == JsonValueKind.Object
                             && it.TryGetProperty("id", out var id)
                             && id.ValueKind == JsonValueKind.String)
                        list.Add(id.GetString());
                }
            }

            return list.Where(x => !string.IsNullOrWhiteSpace(x))
                       .Select(x => x.Trim())
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        private void SetStatus(string text)
        {
            TxtStatus.Text = text ?? "";
            TxtHint.Text = text ?? "";
            _shell.SetStatus(text ?? "");
        }
    }
}
