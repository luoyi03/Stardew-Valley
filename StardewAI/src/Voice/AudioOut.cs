using System;

namespace StardewAI.Voice
{
    /// <summary>
    /// 播放口：wav 先走游戏自己的音频引擎（最稳），不行再退回 MCI；mp3 只能走 MCI。
    /// </summary>
    internal static class AudioOut
    {
        public static string LastError { get; private set; } = "";

        /// <summary>上一次实际用的播放通道（写日志用）。</summary>
        public static string Backend { get; private set; } = "";

        public static bool Play(string file, int volumePercent)
        {
            Backend = "";
            LastError = "";

            bool wav = file != null && file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

            if (wav && XnaAudio.Play(file, volumePercent))
            {
                Backend = "游戏音频(XNA)";
                return true;
            }

            if (MciAudio.Play(file, volumePercent))
            {
                Backend = "MCI";
                return true;
            }

            LastError = (wav ? "游戏音频(XNA)：" + XnaAudio.LastError + "；" : "")
                        + "MCI：" + MciAudio.LastError;

            return false;
        }

        public static bool IsPlaying => XnaAudio.IsPlaying || MciAudio.IsPlaying;

        public static void Stop()
        {
            XnaAudio.Stop();
            MciAudio.Stop();
        }
    }
}
