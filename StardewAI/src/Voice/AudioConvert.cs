using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewAI.Voice
{
    /// <summary>
    /// 把 mp3 转成 16bit 单声道 wav（借 Python 的 miniaudio，不需要 ffmpeg）。
    /// 为什么要转：mp3 在 Windows 上只能由 MCI 的 MPEGVideo（DirectShow + COM）播放，
    /// 而游戏主线程不是 STA 单元，那个设备在游戏进程里起不来；wav 可以直接交给游戏自己的音频引擎。
    /// </summary>
    internal static class AudioConvert
    {
        public static string LastError { get; private set; } = "";

        // 第 4 个参数 = 是否做音量归一：麦克风录进来的通常偏小，归一之后好认不少
        private const string Script =
            "import sys,wave,array,miniaudio;" +
            "src,dst,rate,norm=sys.argv[1],sys.argv[2],int(sys.argv[3]),int(sys.argv[4]);" +
            "d=miniaudio.decode_file(src,output_format=miniaudio.SampleFormat.SIGNED16,nchannels=1,sample_rate=rate);" +
            "s=d.samples;" +
            "p=max(abs(x) for x in s) or 1;" +
            "g=(24000.0/p if (norm and p<24000) else 1.0);" +
            "g=min(g,6.0);" +
            "s=array.array('h',(max(-32768,min(32767,int(x*g))) for x in s));" +
            "w=wave.open(dst,'wb');" +
            "w.setnchannels(1);w.setsampwidth(2);w.setframerate(rate);" +
            "w.writeframes(s.tobytes());" +
            "w.close()";

        /// <summary>播放用：转成 24kHz 单声道 wav。</summary>
        public static Task<string> ToWavAsync(ModConfig cfg, string source, CancellationToken ct)
            => ToWavAsync(cfg, source, 24000, false, ct);

        /// <summary>识别用：转成 16kHz 单声道 wav，并顺手把音量归一。</summary>
        public static Task<string> ToAsrWavAsync(ModConfig cfg, string source, CancellationToken ct)
            => ToWavAsync(cfg, source, 16000, true, ct);

        /// <summary>转成功返回 wav 路径，失败返回 null（调用方应该退回原文件）。</summary>
        public static async Task<string> ToWavAsync(ModConfig cfg, string source, int sampleRate, bool normalize, CancellationToken ct)
        {
            string dir = Path.GetDirectoryName(source) ?? Path.GetTempPath();
            string dst = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(source) + "_cv" + sampleRate + (normalize ? "_n" : "") + ".wav");

            foreach (string py in PythonCandidates(cfg))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (File.Exists(dst))
                        File.Delete(dst);
                }
                catch
                {
                    // 忽略
                }

                var psi = new ProcessStartInfo
                {
                    FileName = py,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(Script);
                psi.ArgumentList.Add(source);
                psi.ArgumentList.Add(dst);
                psi.ArgumentList.Add(sampleRate.ToString());
                psi.ArgumentList.Add(normalize ? "1" : "0");

                try
                {
                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        LastError = "无法启动 " + py;
                        continue;
                    }

                    string err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (proc.ExitCode == 0 && File.Exists(dst) && new FileInfo(dst).Length > 1000)
                    {
                        LastError = "";
                        return dst;
                    }

                    LastError = py + " 退出码 " + proc.ExitCode + "：" + Short(err);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LastError = py + "：" + ex.Message;
                }
            }

            ModEntry.Log?.Log("mp3→wav 转换失败（会退回直接播 mp3）：" + LastError, LogLevel.Warn);
            return null;
        }

        private static List<string> PythonCandidates(ModConfig cfg)
        {
            var list = new List<string>();
            string cmd = (cfg?.EdgeTtsCommand ?? "").Trim();

            // EdgeTtsCommand 填的就是 python.exe 路径时直接复用，省得再配一个
            if (cmd.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0)
                list.Add(cmd);

            list.Add("python");
            list.Add("py");
            return list;
        }

        private static string Short(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
        }
    }
}
