using System.Diagnostics;

namespace StardewAI.Voice
{
    /// <summary>按住说话时的录音（底层是 MCI 的 waveaudio 录音）。</summary>
    internal sealed class MicRecorder
    {
        private readonly Stopwatch _watch = new();

        public bool Active { get; private set; }

        public float Seconds => Active ? (float)_watch.Elapsed.TotalSeconds : 0f;

        public string LastError { get; private set; } = "";

        public bool Start(int sampleRate)
        {
            if (Active)
                return true;

            LastError = "";

            if (!MciAudio.StartRecord(sampleRate))
            {
                LastError = "打不开麦克风：" + MciAudio.LastError + "（检查系统麦克风权限/设备）";
                return false;
            }

            _watch.Restart();
            Active = true;
            return true;
        }

        /// <summary>停止并保存成 wav；录得太短或保存失败返回 null。</summary>
        public string Stop(string wavPath, float minSeconds)
        {
            if (!Active)
                return null;

            Active = false;
            _watch.Stop();
            float sec = (float)_watch.Elapsed.TotalSeconds;

            if (sec < minSeconds)
            {
                MciAudio.CancelRecord();
                LastError = "录得太短了（" + sec.ToString("0.0") + " 秒）——按住说话键，说完再松开";
                return null;
            }

            if (!MciAudio.StopRecordTo(wavPath))
            {
                LastError = "录音保存失败：" + MciAudio.LastError;
                return null;
            }

            LastError = "";
            return wavPath;
        }

        public void Cancel()
        {
            if (!Active)
                return;

            Active = false;
            _watch.Reset();
            MciAudio.CancelRecord();
        }
    }
}
