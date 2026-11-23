// ============================================================================
// RailCurrents.cs — Unlock GPU · patch kit
// Namespace: UnlockGpu.Patch (C# 5 safe for PowerShell 5.1 Add-Type)
//
// MECANISMO: métodos privados "Type19 current policy" do RM expõem correntes OCP
// por rail em mA (NVVDD/MSVDD). INFO devolve default/máx por rail; CONTROL GET
// devolve o valor live; CONTROL SET grava novos limites. Valores em miliampères,
// mínimo 1000 mA. Índices de referência: NVVDD=13, MSVDD=14.
// ============================================================================
using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace UnlockGpu.Patch
{
    public static class RailCurrents
    {
        // IDs dos métodos RM (resolvidos via NvApiPrivate.Resolve).
        public const uint MethodInfo = 545302040u;      // A618 PowerPolicy INFO
        public const uint MethodStatus = 545302041u;    // A619 status
        public const uint MethodControlGet = 545302042u;// A61A CONTROL GET
        public const uint MethodControlSet = 545318427u;// E61B CONTROL SET

        // Layout INFO.
        public const uint InfoVersion = 2859056u;
        public const int InfoSize = 2727984;
        public const int InfoRecordBase = 1200;
        public const int InfoRecordStride = 10604;

        // Layout CONTROL (dois variantes de driver, tentadas em ordem).
        public const uint ControlVersionA = 2590432u;   // size 2393824, record base 2664
        public const int ControlSizeA = 2393824;
        public const int ControlRecordBaseA = 2664;
        public const uint ControlVersionB = 372912u;    // size 307376, record base 2608
        public const int ControlSizeB = 307376;
        public const int ControlRecordBaseB = 2608;
        public const int ControlMaskOffset = 136;
        public const int ControlRecordStride = 9288;
        public const int ControlCurrentOffset = 68;

        // Semântica.
        public const int ReferenceNvvddIndex = 13;
        public const int ReferenceMsvddIndex = 14;
        public const int TypeCurrentPolicy = 19;
        public const int MinimumCurrentMa = 1000;
        public const int MaximumSanityMa = 5001000;

        public sealed class RailInfo
        {
            public string Name = "";
            public int Index;
            public int Channel;
            public int DefaultMa;
            public int MaximumMa;
            public int CurrentMa; // preenchido por GetCurrents/SetCurrents
        }

        private static uint BuildMask(int a, int b)
        {
            if (a < 0 || a >= 32 || b < 0 || b >= 32 || a == b)
            {
                return 0u;
            }
            return (uint)((1 << a) | (1 << b));
        }

        private static void PutU32(byte[] buffer, int offset, uint value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
        }

        private static uint GetU32(byte[] buffer, int offset)
        {
            return BitConverter.ToUInt32(buffer, offset);
        }

        private static void PutI32(byte[] buffer, int offset, int value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
        }

        private static int GetI32(byte[] buffer, int offset)
        {
            return BitConverter.ToInt32(buffer, offset);
        }

        // Chama o método RM com o byte[] pinado.
        private static int Call(NvApiPrivate.GpuBufferDelegate fn, IntPtr gpu, byte[] buffer)
        {
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                return fn(gpu, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        private static string RailName(int index)
        {
            if (index == ReferenceNvvddIndex)
            {
                return "NVVDD";
            }
            if (index == ReferenceMsvddIndex)
            {
                return "MSVDD";
            }
            return "Rail" + index.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------- INFO --------------------------------

        // Lê o buffer de policy INFO e valida os dois descritores de rail.
        public static bool Probe(NvApiPrivate nv, int nvvddIndex, int msvddIndex, out RailInfo nvvdd, out RailInfo msvdd, out string error)
        {
            nvvdd = null;
            msvdd = null;
            error = "";
            if (nv == null || nv.Gpu == IntPtr.Zero)
            {
                error = "NVAPI session is unavailable.";
                return false;
            }
            uint mask = BuildMask(nvvddIndex, msvddIndex);
            if (mask == 0u)
            {
                error = "Rail index pair is invalid (need two distinct indices in 0..31).";
                return false;
            }
            NvApiPrivate.GpuBufferDelegate infoFn = nv.Resolve(MethodInfo);
            if (infoFn == null)
            {
                error = "PowerPolicy INFO method not exported by this driver.";
                return false;
            }

            byte[] info = new byte[InfoSize];
            PutU32(info, 0, InfoVersion);
            int rc = Call(infoFn, nv.Gpu, info);
            if (rc != 0 || GetU32(info, 0) != InfoVersion)
            {
                error = "PowerPolicy INFO failed or layout changed. rc=" + rc.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            uint capabilityMask = GetU32(info, 4);
            if ((capabilityMask & mask) != mask)
            {
                error = "Driver does not advertise both requested rails (cap=0x" + capabilityMask.ToString("X8", CultureInfo.InvariantCulture) + ").";
                return false;
            }

            RailInfo a = ParseInfoRecord(info, nvvddIndex);
            RailInfo b = ParseInfoRecord(info, msvddIndex);
            if (a == null || b == null)
            {
                error = "Rail descriptor failed validation for index " + (a == null ? nvvddIndex.ToString(CultureInfo.InvariantCulture) : msvddIndex.ToString(CultureInfo.InvariantCulture)) + ".";
                return false;
            }
            if (a.Channel <= 0 || a.Channel == b.Channel)
            {
                error = "Rail channel binding is not unique.";
                return false;
            }

            nvvdd = a;
            msvdd = b;
            return true;
        }

        private static RailInfo ParseInfoRecord(byte[] info, int index)
        {
            int off = InfoRecordBase + index * InfoRecordStride;
            if (off < 0 || off + 24 > info.Length)
            {
                return null;
            }
            int type = GetI32(info, off + 0);
            int channel = GetI32(info, off + 4);
            int defaultMa = GetI32(info, off + 16);
            int maxMa = GetI32(info, off + 20);
            if (type != TypeCurrentPolicy)
            {
                return null;
            }
            if (channel <= 0)
            {
                return null;
            }
            if (defaultMa < MinimumCurrentMa || defaultMa > MaximumSanityMa)
            {
                return null;
            }
            if (maxMa < defaultMa || maxMa > MaximumSanityMa)
            {
                return null;
            }
            RailInfo rail = new RailInfo();
            rail.Name = RailName(index);
            rail.Index = index;
            rail.Channel = channel;
            rail.DefaultMa = defaultMa;
            rail.MaximumMa = maxMa;
            return rail;
        }

        // --------------------------- CONTROL GET/SET --------------------------

        // Tenta o variante A e depois o B do buffer de controle. Devolve o buffer live + a base de record aplicável.
        private static bool TryReadControl(NvApiPrivate nv, int indexA, int indexB, out byte[] buffer, out int recordBase, out string error)
        {
            buffer = null;
            recordBase = -1;
            error = "";
            uint mask = BuildMask(indexA, indexB);
            if (mask == 0u)
            {
                error = "Rail index pair is invalid (need two distinct indices in 0..31).";
                return false;
            }
            NvApiPrivate.GpuBufferDelegate getFn = nv.Resolve(MethodControlGet);
            if (getFn == null)
            {
                error = "PowerPolicy CONTROL GET method not exported by this driver.";
                return false;
            }

            int lastRcA = -1;
            int lastRcB = -1;

            byte[] bufA = new byte[ControlSizeA];
            PutU32(bufA, 0, ControlVersionA);
            PutU32(bufA, ControlMaskOffset, mask);
            lastRcA = Call(getFn, nv.Gpu, bufA);
            if (lastRcA == 0 && GetU32(bufA, 0) == ControlVersionA && (GetU32(bufA, ControlMaskOffset) & mask) == mask)
            {
                buffer = bufA;
                recordBase = ControlRecordBaseA;
                return true;
            }

            byte[] bufB = new byte[ControlSizeB];
            PutU32(bufB, 0, ControlVersionB);
            PutU32(bufB, ControlMaskOffset, mask);
            lastRcB = Call(getFn, nv.Gpu, bufB);
            if (lastRcB == 0 && GetU32(bufB, 0) == ControlVersionB && (GetU32(bufB, ControlMaskOffset) & mask) == mask)
            {
                buffer = bufB;
                recordBase = ControlRecordBaseB;
                return true;
            }

            error = "CONTROL GET rejected both driver variants. rcA=" + lastRcA.ToString(CultureInfo.InvariantCulture) + " rcB=" + lastRcB.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        // Lê as correntes live dos dois rails (mA).
        public static bool GetCurrents(NvApiPrivate nv, int indexA, int indexB, out RailInfo railA, out RailInfo railB, out string error)
        {
            railA = null;
            railB = null;
            byte[] buffer;
            int recordBase;
            if (!TryReadControl(nv, indexA, indexB, out buffer, out recordBase, out error))
            {
                return false;
            }
            RailInfo a = ReadCurrent(buffer, recordBase, indexA);
            RailInfo b = ReadCurrent(buffer, recordBase, indexB);
            if (a == null || b == null)
            {
                error = "Live current failed sanity check for index " + (a == null ? indexA.ToString(CultureInfo.InvariantCulture) : indexB.ToString(CultureInfo.InvariantCulture)) + ".";
                return false;
            }
            railA = a;
            railB = b;
            return true;
        }

        private static RailInfo ReadCurrent(byte[] buffer, int recordBase, int index)
        {
            int off = recordBase + index * ControlRecordStride + ControlCurrentOffset;
            if (off < 0 || off + 4 > buffer.Length)
            {
                return null;
            }
            int currentMa = GetI32(buffer, off);
            if (currentMa < MinimumCurrentMa || currentMa > MaximumSanityMa)
            {
                return null;
            }
            RailInfo rail = new RailInfo();
            rail.Name = RailName(index);
            rail.Index = index;
            rail.CurrentMa = currentMa;
            return rail;
        }

        // Grava novas correntes (mA) nos rails. targetA/targetB null = mantém o valor atual daquele rail.
        // validateFirst: faz uma escrita no-op com o buffer intacto antes da primeira mudança real (comportamento do driver original).
        // verify: relê via CONTROL GET e confirma que os valores aplicados batem com os alvos.
        public static bool SetCurrents(NvApiPrivate nv, int indexA, int indexB, int? targetMaA, int? targetMaB, bool validateFirst, bool verify, out string error)
        {
            error = "";
            byte[] live;
            int recordBase;
            if (!TryReadControl(nv, indexA, indexB, out live, out recordBase, out error))
            {
                return false;
            }

            uint mask = BuildMask(indexA, indexB);
            NvApiPrivate.GpuBufferDelegate setFn = nv.Resolve(MethodControlSet);
            if (setFn == null)
            {
                error = "PowerPolicy CONTROL SET method not exported by this driver.";
                return false;
            }

            int rc;
            if (validateFirst)
            {
                byte[] noop = new byte[live.Length];
                Buffer.BlockCopy(live, 0, noop, 0, live.Length);
                PutU32(noop, ControlMaskOffset, mask);
                rc = Call(setFn, nv.Gpu, noop);
                if (rc != 0)
                {
                    error = "No-op write validation failed. rc=" + rc.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            byte[] staged = new byte[live.Length];
            Buffer.BlockCopy(live, 0, staged, 0, live.Length);

            if (targetMaA.HasValue)
            {
                if (!ApplyTarget(staged, recordBase, indexA, targetMaA.Value))
                {
                    error = "Target A out of range (" + MinimumCurrentMa.ToString(CultureInfo.InvariantCulture) + ".." + MaximumSanityMa.ToString(CultureInfo.InvariantCulture) + " mA).";
                    return false;
                }
            }
            if (targetMaB.HasValue)
            {
                if (!ApplyTarget(staged, recordBase, indexB, targetMaB.Value))
                {
                    error = "Target B out of range (" + MinimumCurrentMa.ToString(CultureInfo.InvariantCulture) + ".." + MaximumSanityMa.ToString(CultureInfo.InvariantCulture) + " mA).";
                    return false;
                }
            }
            PutU32(staged, ControlMaskOffset, mask);

            rc = Call(setFn, nv.Gpu, staged);
            if (rc != 0)
            {
                error = "CONTROL SET failed. rc=" + rc.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (verify)
            {
                byte[] after;
                int baseAfter;
                string readError;
                if (!TryReadControl(nv, indexA, indexB, out after, out baseAfter, out readError))
                {
                    error = "Post-set re-read failed: " + readError;
                    return false;
                }
                int currentA = GetI32(after, baseAfter + indexA * ControlRecordStride + ControlCurrentOffset);
                int currentB = GetI32(after, baseAfter + indexB * ControlRecordStride + ControlCurrentOffset);
                if (targetMaA.HasValue && currentA != targetMaA.Value)
                {
                    error = "Verify A: applied " + targetMaA.Value.ToString(CultureInfo.InvariantCulture) + ", read back " + currentA.ToString(CultureInfo.InvariantCulture) + " mA.";
                    return false;
                }
                if (targetMaB.HasValue && currentB != targetMaB.Value)
                {
                    error = "Verify B: applied " + targetMaB.Value.ToString(CultureInfo.InvariantCulture) + ", read back " + currentB.ToString(CultureInfo.InvariantCulture) + " mA.";
                    return false;
                }
            }
            return true;
        }

        private static bool ApplyTarget(byte[] buffer, int recordBase, int index, int targetMa)
        {
            if (targetMa < MinimumCurrentMa || targetMa > MaximumSanityMa)
            {
                return false;
            }
            int off = recordBase + index * ControlRecordStride + ControlCurrentOffset;
            if (off < 0 || off + 4 > buffer.Length)
            {
                return false;
            }
            PutI32(buffer, off, targetMa);
            return true;
        }
    }
}
