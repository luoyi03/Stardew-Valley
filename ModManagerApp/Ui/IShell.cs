using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace SMM
{
    /// <summary>主窗口提供给各个页面的能力。</summary>
    public interface IShell
    {
        AppEnv Env { get; }

        /// <summary>所有 Mod 的实时状态（各页面共享）。</summary>
        ObservableCollection<ModStatus> Mods { get; }

        /// <summary>重新扫描 Mods 目录并刷新列表。</summary>
        Task ReloadModsAsync();

        void SetStatus(string text);
        void SetBusy(string text);
        void SetProgress(int percent);
        void OpenPage(string key);
        void Toast(string message);

        /// <summary>刷新顶栏（游戏目录 / SMAPI 版本）。</summary>
        void RefreshHeader();

        /// <summary>按最新配置重启「下载文件夹监控」。</summary>
        void RestartDownloadWatcher();

        /// <summary>立刻扫一遍监控目录里已有的 zip（设置页「立即检查」用）。</summary>
        Task<int> CheckWatchFolderNowAsync();
    }
}
