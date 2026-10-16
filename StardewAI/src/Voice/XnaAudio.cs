using System;
using System.IO;
using Microsoft.Xna.Framework.Audio;

namespace StardewAI.Voice
{
    /// <summary>
    /// 用游戏自己的音频引擎（XNA/MonoGame 的 SoundEffect）播放 wav。
    /// 这条路最稳：不经过 MCI，也就绕开了「游戏主线程不是 STA 单元、DirectShow（mp3）起不来」的问题。
    /// </summary>
    internal static class XnaAudio
    {
        private static SoundEffect _effect;
        private static SoundEffectInstance _instance;

        public static string LastError { get; private set; } = "";

        public static bool Play(string file, int volumePercent)
        {
            Stop();
            LastError = "";

            try
            {
                using FileStream fs = File.OpenRead(file);
                _effect = SoundEffect.FromStream(fs);

                if (_effect == null)
                {
                    LastError = "SoundEffect.FromStream 返回空";
                    return false;
                }

                _instance = _effect.CreateInstance();
                _instance.Volume = Math.Clamp(volumePercent / 100f, 0f, 1f);
                _instance.Play();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message ?? ex.GetType().Name;
                Stop();
                return false;
            }
        }

        public static bool IsPlaying
        {
            get
            {
                try
                {
                    return _instance != null && _instance.State == SoundState.Playing;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void Stop()
        {
            try { _instance?.Stop(true); } catch { /* ignore */ }
            try { _instance?.Dispose(); } catch { /* ignore */ }
            try { _effect?.Dispose(); } catch { /* ignore */ }

            _instance = null;
            _effect = null;
        }
    }
}
