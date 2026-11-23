// ============================================================================
// GpuHotRestart.cs — Unlock GPU · patch kit
// Namespace: UnlockGpu.Patch (C# 5 safe for PowerShell 5.1 Add-Type)
//
// MECANISMO: depois de gravar os romOverride no registro, o driver só relê eles
// quando o dispositivo reinicia. pnputil /restart-device <PnP ID> faz um ciclo
// quente SÓ da GPU (sem reboot do Windows). Depois verificamos via NVML que o
// MAX live == valor staged (tolerância 0.75 W). Se /restart-device falhar, há
// fallback de hard cycle: disable → enable.
// ============================================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace UnlockGpu.Patch
{
    public static class GpuHotRestart
    {
        public const int RestartTimeoutMs = 45000;
        public const double MaxToleranceW = 0.75;
        public const double IdleThresholdPct = 7.5;

        // PnP IDs têm formato PCI\VEN_10DE&DEV_xxxx&... — só caracteres seguros antes de entrar no argumento do pnputil.
        public static bool ValidPnpId(string pnpId)
        {
            if (string.IsNullOrEmpty(pnpId))
            {
                return false;
            }
            if (!pnpId.StartsWith("PCI\\VEN_10DE&", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            for (int i = 0; i < pnpId.Length; i++)
            {
                char c = pnpId[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '&' && c != '\\' && c != '-' && c != '.')
                {
                    return false;
                }
            }
            return true;
        }

        // Polla utilização até a GPU ficar ociosa (<7.5%) ou estourar o timeout.
        public static bool WaitForIdle(Func<double?> utilizationPct, int timeoutMs, out double lastUtilization)
        {
            lastUtilization = -1;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                double? u = null;
                try
                {
                    u = utilizationPct();
                }
                catch
                {
                    // telemetria momentaneamente indisponível; tenta de novo
                }
                if (u.HasValue)
                {
                    lastUtilization = u.Value;
                    if (u.Value <= IdleThresholdPct)
                    {
                        return true;
                    }
                }
                Thread.Sleep(500);
            }
            return lastUtilization >= 0 && lastUtilization <= IdleThresholdPct;
        }

        // Se o MAX live já está no alvo (±0.75 W), o restart pode ser pulado.
        public static bool ShouldSkipRestart(double liveMaxW, double targetW)
        {
            return Math.Abs(liveMaxW - targetW) <= MaxToleranceW;
        }

        // Verificação pós-restart: MAX live dentro da tolerância do valor staged.
        public static bool VerifyMaxW(double liveMaxW, double targetW)
        {
            return Math.Abs(liveMaxW - targetW) <= MaxToleranceW;
        }

        // Roda pnputil.exe /restart-device "<pnpId>" e espera até 45 s. exitCode==0 = sucesso.
        public static bool RestartDevice(string pnpInstanceId, out int exitCode, out string detail)
        {
            exitCode = -1;
            detail = "PnP process never started";
            if (!ValidPnpId(pnpInstanceId))
            {
                detail = "PnP device instance contains unexpected characters or wrong prefix.";
                return false;
            }
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
            if (!File.Exists(exe))
            {
                detail = "Windows pnputil.exe not found at " + exe;
                return false;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "/restart-device \"" + pnpInstanceId + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process process = Process.Start(psi))
                {
                    string so = process.StandardOutput.ReadToEnd();
                    string se = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(RestartTimeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        detail = "pnputil timed out after " + RestartTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms.";
                        return false;
                    }
                    exitCode = process.ExitCode;
                    detail = (so + se).Trim();
                    return exitCode == 0;
                }
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Fallback de hard cycle: disable → 900 ms → enable → 1600 ms.
        public static bool HardCycle(string pnpInstanceId, out string detail)
        {
            detail = "";
            int code;
            if (!RunAction(pnpInstanceId, "/disable-device", out code))
            {
                detail = "disable failed (exit=" + code.ToString(CultureInfo.InvariantCulture) + ")";
                return false;
            }
            Thread.Sleep(900);
            if (!RunAction(pnpInstanceId, "/enable-device", out code))
            {
                detail = "disable succeeded; enable failed (exit=" + code.ToString(CultureInfo.InvariantCulture) + ")";
                return false;
            }
            Thread.Sleep(1600);
            detail = "hard cycle complete";
            return true;
        }

        private static bool RunAction(string pnpInstanceId, string action, out int exitCode)
        {
            exitCode = -1;
            if (!ValidPnpId(pnpInstanceId))
            {
                return false;
            }
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, action + " \"" + pnpInstanceId + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process process = Process.Start(psi))
                {
                    if (!process.WaitForExit(RestartTimeoutMs))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        return false;
                    }
                    exitCode = process.ExitCode;
                    return exitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
