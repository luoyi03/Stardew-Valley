using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SMM
{
    public abstract class ObservableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    /// <summary>一个 Mod 在界面上的完整状态。</summary>
    public sealed class ModStatus : ObservableBase
    {
        private LocalMod _mod;

        /// <summary>unknown / checking / latest / update / manual / error / ignored / disabled</summary>
        private string _status = "unknown";
        private string _latestVersion;
        private string _sourceName;
        private string _pageUrl;
        private string _message;
        private DownloadPlan _plan;
        private bool _manualDownload;
        private int _progress;
        private string _phase;

        public LocalMod Mod { get => _mod; set { if (Set(ref _mod, value)) NotifyAll(); } }

        public string Status
        {
            get => _status;
            set { if (Set(ref _status, value)) { Raise(nameof(HasUpdate)); Raise(nameof(CanUpdate)); Raise(nameof(StatusText)); Raise(nameof(IsBusy)); Raise(nameof(DetailText)); } }
        }

        public string LatestVersion
        {
            get => _latestVersion;
            set { if (Set(ref _latestVersion, value)) Raise(nameof(StatusText)); }
        }

        public string SourceName { get => _sourceName; set { if (Set(ref _sourceName, value)) Raise(nameof(SourceText)); } }

        public string PageUrl { get => _pageUrl; set => Set(ref _pageUrl, value); }

        public string Message { get => _message; set { if (Set(ref _message, value)) Raise(nameof(DetailText)); } }

        public DownloadPlan Plan { get => _plan; set => Set(ref _plan, value); }

        public bool ManualDownload { get => _manualDownload; set { if (Set(ref _manualDownload, value)) Raise(nameof(CanUpdate)); } }

        public int Progress
        {
            get => _progress;
            set { if (Set(ref _progress, value)) { Raise(nameof(PhaseText)); Raise(nameof(IsBusy)); } }
        }

        public string Phase
        {
            get => _phase;
            set { if (Set(ref _phase, value)) { Raise(nameof(PhaseText)); Raise(nameof(IsBusy)); } }
        }

        // ---------------- 供界面直接绑定 ----------------
        public bool HasUpdate => _status == "update";

        public bool CanUpdate => HasUpdate && !ManualDownload && _mod?.IsDuplicate != true;

        public bool IsBusy => _status == "checking" || _phase != null;

        public string Name => _mod?.Name ?? "";
        public string Id => _mod?.Id ?? "";
        public string VersionText => _mod?.Version ?? "";
        public string AuthorText => string.IsNullOrEmpty(_mod?.Author) ? "—" : _mod.Author;
        public string EnabledText => _mod == null ? "" : (_mod.Disabled ? "已禁用" : "启用中");

        public string StatusText => _status switch
        {
            "checking" => "检查中…",
            "latest" => "已是最新",
            "update" => $"可更新 → {_latestVersion}",
            "manual" => "无法自动检查",
            "smapi" => "SMAPI 自带",
            "error" => "检查失败",
            "ignored" => "已忽略",
            "disabled" => "已禁用",
            _ => "未检查"
        };

        public string SourceText => string.IsNullOrEmpty(_sourceName) ? "—" : _sourceName;

        public string DetailText
        {
            get
            {
                if (!string.IsNullOrEmpty(Message))
                    return Message;
                if (IsBusy)
                    return PhaseText;
                if (_mod == null)
                    return "";
                if (_mod.Disabled)
                    return "已禁用，不会被游戏加载";
                if (_status == "smapi")
                    return "SMAPI 自带的工具，跟着 SMAPI 一起更新";
                if (_mod.Keys.Count == 0 && _status != "update")
                    return "manifest 未写 UpdateKeys，可点「补写更新源」";
                return "";
            }
        }

        public string PhaseText
        {
            get
            {
                if (!string.IsNullOrEmpty(_phase))
                    return Progress > 0 ? $"{_phase} {Progress}%" : _phase;
                return Progress > 0 ? $"{Progress}%" : "";
            }
        }

        public void NotifyAll()
        {
            Raise(nameof(Name));
            Raise(nameof(Id));
            Raise(nameof(VersionText));
            Raise(nameof(AuthorText));
            Raise(nameof(EnabledText));
            Raise(nameof(HasUpdate));
            Raise(nameof(CanUpdate));
            Raise(nameof(DetailText));
        }
    }
}
