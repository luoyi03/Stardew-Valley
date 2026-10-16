using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StardewAI.Voice
{
    /// <summary>
    /// 用 Windows 自带的 MCI（winmm.dll）播放音频文件、从麦克风录音。
    /// 这样不用引入 NAudio 之类的第三方库：mp3 / wav 都能放，走的是共享模式，能和游戏声音共存。
    /// </summary>
    internal static class MciAudio
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder reply, int replyLength, IntPtr hwnd);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciGetErrorString(int error, StringBuilder text, int length);

        private const string PlayerAlias = "sdvai_out";
        private const string RecorderAlias = "sdvai_in";

        private static readonly object Gate = new();
        private static bool _playerOpen;

        /// <summary>最近一次失败的原文（写日志用）。</summary>
        public static string LastError { get; private set; } = "";

        private static string Send(string command, bool wantReply = false)
        {
            try
            {
                StringBuilder sb = wantReply ? new StringBuilder(512) : null;
                int rc = mciSendString(command, sb, wantReply ? sb.Capacity : 0, IntPtr.Zero);

                if (rc == 0)
                    return wantReply ? sb.ToString() : "";

                var err = new StringBuilder(256);
                mciGetErrorString(rc, err, err.Capacity);
                LastError = err.ToString();
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        // ---------------- 播放 ----------------

        public static bool Play(string file, int volumePercent)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                LastError = "音频文件不存在：" + file;
                return false;
            }

            lock (Gate)
            {
                ClosePlayer();
                LastError = "";

                string path = file.Replace("\"", "");
                var attempts = new List<string> { "open \"" + path + "\" alias " + PlayerAlias };
                var labels = new List<string> { "自动识别" };

                // 按扩展名挑设备：给 mp3 试 waveaudio、给 wav 试 mpegvideo 只会得到误导性的报错
                if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    attempts.Add("open \"" + path + "\" type waveaudio alias " + PlayerAlias);
                    labels.Add("waveaudio");
                }
                else
                {
                    attempts.Add("open \"" + path + "\" type mpegvideo alias " + PlayerAlias);
                    labels.Add("mpegvideo");
                }

                bool opened = false;
                var errors = new List<string>();

                for (int i = 0; i < attempts.Count; i++)
                {
                    if (Send(attempts[i]) != null)
                    {
                        opened = true;
                        break;
                    }

                    errors.Add(labels[i] + "：" + LastError);
                }

                if (!opened)
                {
                    LastError = string.Join("；", errors);
                    return false;
                }

                _playerOpen = true;
                SetVolume(volumePercent);

                if (Send("play " + PlayerAlias) == null)
                {
                    ClosePlayer();
                    return false;
                }

                return true;
            }
        }

        public static bool IsPlaying
        {
            get
            {
                lock (Gate)
                {
                    if (!_playerOpen)
                        return false;

                    string mode = Send("status " + PlayerAlias + " mode", true);
                    return mode != null && mode.Trim().StartsWith("play", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                if (_playerOpen)
                    Send("stop " + PlayerAlias);

                ClosePlayer();
            }
        }

        public static void SetVolume(int percent)
        {
            lock (Gate)
            {
                if (!_playerOpen)
                    return;

                // MCI 音量范围是 0~1000
                int v = Math.Clamp(percent, 0, 100) * 10;
                Send("setaudio " + PlayerAlias + " volume to " + v);
            }
        }

        private static void ClosePlayer()
        {
            Send("close " + PlayerAlias);
            _playerOpen = false;
        }

        // ---------------- 录音 ----------------

        /// <summary>上一次录音实际用的格式（写日志用）。</summary>
        public static string LastRecordFormat { get; private set; } = "";

        /// <summary>
        /// 开始录音。声卡不一定支持 16k 单声道（有的机器只吃 44.1k 立体声），
        /// 所以按「想要的格式 → 44.1k 立体声 → 驱动默认」逐个试，成功为止。
        /// </summary>
        public static bool StartRecord(int sampleRate)
        {
            lock (Gate)
            {
                LastRecordFormat = "";
                Send("close " + RecorderAlias);

                if (Send("open new type waveaudio alias " + RecorderAlias) == null)
                    return false;

                var formats = new List<(string set, string label)>
                {
                    ("set " + RecorderAlias + " time format ms bitspersample 16 channels 1 samplespersec " + sampleRate,
                        sampleRate + "Hz 单声道 16bit"),
                    ("set " + RecorderAlias + " time format ms bitspersample 16 channels 2 samplespersec 44100",
                        "44100Hz 立体声 16bit"),
                    (null, "驱动默认格式")
                };

                foreach ((string set, string label) in formats)
                {
                    if (set == null)
                    {
                        // 默认格式：重新开一次设备，免得前面失败的 set 留下半个格式
                        Send("close " + RecorderAlias);
                        if (Send("open new type waveaudio alias " + RecorderAlias) == null)
                            return false;
                    }
                    else
                    {
                        Send("set " + RecorderAlias + " time format ms");

                        if (Send(set) == null)
                            continue;
                    }

                    if (Send("record " + RecorderAlias) != null)
                    {
                        LastRecordFormat = label;
                        return true;
                    }
                }

                Send("close " + RecorderAlias);
                LastError = "这台机器的录音设备不支持常见录音格式：" + LastError;
                return false;
            }
        }

        public static bool StopRecordTo(string wavPath)
        {
            lock (Gate)
            {
                bool ok = Send("stop " + RecorderAlias) != null;

                try
                {
                    if (File.Exists(wavPath))
                        File.Delete(wavPath);
                }
                catch
                {
                    // 旧文件删不掉就覆盖写
                }

                if (ok)
                    ok = Send("save " + RecorderAlias + " \"" + wavPath.Replace("\"", "") + "\"") != null;

                Send("close " + RecorderAlias);

                if (!ok || !File.Exists(wavPath))
                    return false;

                try
                {
                    return new FileInfo(wavPath).Length > 1024;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void CancelRecord()
        {
            lock (Gate)
            {
                Send("stop " + RecorderAlias);
                Send("close " + RecorderAlias);
            }
        }
    }
}
