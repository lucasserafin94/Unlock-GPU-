// ============================================================================
// RomSlot.cs — Unlock GPU · patch kit
// Namespace: UnlockGpu.Patch (C# 5 safe for PowerShell 5.1 Add-Type)
//
// MECANISMO: o driver NVIDIA lê valores REG_BINARY chamados romOverrideXX na
// chave de classe da GPU por slot e os aplica como patches de "shadow" na vBIOS
// a cada boot do dispositivo. É isso que permite passar do teto de potência da
// vBIOS (factory MAX) sem reescrever a ROM inteira.
//
// Chave: HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\000x
// Aberta via cfgmgr32 a partir do PnP instance ID exato (PCI\VEN_10DE&...).
// Requer admin. Depois de gravar, o valor só vira LIVE com um restart do device
// (ver GpuHotRestart.cs).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace UnlockGpu.Patch
{
    public static class RomSlot
    {
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Open_DevNode_Key(uint dnDevNode, int samDesired, uint dwRegOptions, uint ulHwProfile, out IntPtr phkDevice, uint ulWhichKey);

        public const string GpuClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        public const int DefaultMaxSlot = 15; // prefixo denso observado: 00..0F

        /// <summary>Abre a chave de software da GPU para um PnP instance ID exato. Dispose libera o handle.</summary>
        public static RegistryKey OpenDeviceKey(string pnpInstanceId, bool writable, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(pnpInstanceId))
            {
                error = "PnP instance ID is empty.";
                return null;
            }
            uint devInst;
            uint rc = CM_Locate_DevNodeW(out devInst, pnpInstanceId, 0u);
            if (rc != 0)
            {
                error = "CM_Locate_DevNodeW failed CONFIGRET=0x" + rc.ToString("X8", CultureInfo.InvariantCulture) + " for " + pnpInstanceId;
                return null;
            }
            int samDesired = 1 | (writable ? 2 : 0); // KEY_QUERY_VALUE [+ KEY_SET_VALUE]
            IntPtr handle;
            rc = CM_Open_DevNode_Key(devInst, samDesired, 0u, 1u, out handle, 1u); // whichKey=CM_REGISTRY_SOFTWARE
            if (rc != 0 || handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                error = "CM_Open_DevNode_Key failed CONFIGRET=0x" + rc.ToString("X8", CultureInfo.InvariantCulture) + ".";
                return null;
            }
            try
            {
                // SafeRegistryHandle funciona tanto no .NET Framework (PS 5.1) quanto em .NET moderno.
                return RegistryKey.FromHandle(new SafeRegistryHandle(handle, true));
            }
            catch (Exception ex)
            {
                error = "RegistryKey.FromHandle: " + ex.Message;
                return null;
            }
        }

        // ------------------------- nomes de slot ----------------------------

        public static string SlotName(int index)
        {
            return "romOverride" + index.ToString("X2", CultureInfo.InvariantCulture);
        }

        public static bool TryParseSlot(string name, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(name) || !name.StartsWith("romOverride", StringComparison.OrdinalIgnoreCase) || name.Length != 13)
            {
                return false;
            }
            return int.TryParse(name.Substring(11, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out index);
        }

        // ---------------------- parse / build de valores --------------------

        // Canonical (exatamente 14 bytes):
        //   [5B header wildcard zerado][int32 LE shadowOffset @5][width=4 @9][int32 LE rawValue @10]
        public static bool TryParseCanonical(byte[] data, out int shadowOffset, out int rawValue)
        {
            shadowOffset = 0;
            rawValue = 0;
            if (data == null || data.Length != 14 || data[9] != 4)
            {
                return false;
            }
            for (int i = 0; i < 5; i++)
            {
                if (data[i] != 0)
                {
                    return false;
                }
            }
            shadowOffset = BitConverter.ToInt32(data, 5);
            rawValue = BitConverter.ToInt32(data, 10);
            return shadowOffset > 0 && rawValue >= 0;
        }

        // Sized (10+N bytes):
        //   [5B header: wildcard zerado OU id exato da vBIOS][int32 LE shadowOffset @5][width=N(1..16) @9][payload @10]
        public static bool TryParseSized(byte[] data, byte[] vbiosHeader, out int shadowOffset, out int payloadWidth, out byte[] payload, out string headerMode)
        {
            shadowOffset = 0;
            payloadWidth = 0;
            payload = null;
            headerMode = "";
            if (data == null || data.Length < 11)
            {
                return false;
            }
            bool wildcard = true;
            for (int i = 0; i < 5; i++)
            {
                if (data[i] != 0)
                {
                    wildcard = false;
                    break;
                }
            }
            bool exactMatch = false;
            if (!wildcard && vbiosHeader != null && vbiosHeader.Length == 5)
            {
                exactMatch = true;
                for (int j = 0; j < 5; j++)
                {
                    if (data[j] != vbiosHeader[j])
                    {
                        exactMatch = false;
                        break;
                    }
                }
            }
            if (!wildcard && !exactMatch)
            {
                return false;
            }
            shadowOffset = BitConverter.ToInt32(data, 5);
            payloadWidth = data[9];
            if (shadowOffset <= 0 || payloadWidth <= 0 || payloadWidth > 16 || data.Length != 10 + payloadWidth)
            {
                return false;
            }
            payload = new byte[payloadWidth];
            Buffer.BlockCopy(data, 10, payload, 0, payloadWidth);
            headerMode = exactMatch ? "VBIOS_EXACT_5BYTE" : "WILDCARD_5BYTE";
            return true;
        }

        // Monta um blob sized. Com vbiosHeader (5 bytes) fica vinculado àquela vBIOS; sem, usa wildcard zerado.
        public static bool BuildVbiosBound(int shadowOffset, byte[] payload, byte[] vbiosHeader, out byte[] blob, out string error)
        {
            blob = null;
            error = "";
            if (shadowOffset <= 0)
            {
                error = "Semantic shadow offset is invalid.";
                return false;
            }
            if (payload == null || payload.Length == 0 || payload.Length > 16)
            {
                error = "Persistent payload width is invalid.";
                return false;
            }
            byte[] header = new byte[5];
            if (vbiosHeader != null)
            {
                if (vbiosHeader.Length != 5)
                {
                    error = "VBIOS header must be exactly 5 bytes.";
                    return false;
                }
                Buffer.BlockCopy(vbiosHeader, 0, header, 0, 5);
            }
            blob = new byte[10 + payload.Length];
            Buffer.BlockCopy(header, 0, blob, 0, 5);
            Buffer.BlockCopy(BitConverter.GetBytes(shadowOffset), 0, blob, 5, 4);
            blob[9] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, blob, 10, payload.Length);
            return true;
        }

        // "95.02.4F.78.01" -> 5 bytes crus do id da vBIOS.
        public static bool TryBuildVbiosHeader(string vbiosVersion, out byte[] header)
        {
            header = null;
            if (string.IsNullOrWhiteSpace(vbiosVersion))
            {
                return false;
            }
            string[] parts = vbiosVersion.Trim().Split('.');
            if (parts.Length != 5)
            {
                return false;
            }
            byte[] bytes = new byte[5];
            for (int i = 0; i < 5; i++)
            {
                if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                {
                    return false;
                }
            }
            header = bytes;
            return true;
        }

        // ------------------- extração / reconstrução por slot --------------

        // Blob de slot já parseado: cabeçalho, offset shadow e payload.
        public struct RomSlotBlob
        {
            public byte[] Header;       // 5 bytes (wildcard zerado ou id exato da vBIOS)
            public int ShadowOffset;    // offset semântico dentro da imagem da vBIOS
            public int Width;           // largura do payload em bytes (4 = um int32, 8 = par duplicado)
            public byte[] Payload;      // bytes crus do payload

            public RomSlotBlob(byte[] header, int shadowOffset, int width, byte[] payload)
            {
                Header = header;
                ShadowOffset = shadowOffset;
                Width = width;
                Payload = payload;
            }
        }

        // Faz o parse de um blob de slot (canônico 14B ou sized 10+N). Aceita tanto o cabeçalho wildcard zerado quanto o id exato da vBIOS.
        public static bool TryParseBlob(byte[] data, out RomSlotBlob blob)
        {
            blob = new RomSlotBlob();
            if (data == null || data.Length < 10)
            {
                return false;
            }
            int width = data[9];
            int shadow = BitConverter.ToInt32(data, 5);
            if (shadow <= 0 || width <= 0 || width > 16 || data.Length != 10 + width)
            {
                return false;
            }
            byte[] header = new byte[5];
            Buffer.BlockCopy(data, 0, header, 0, 5);
            byte[] payload = new byte[width];
            Buffer.BlockCopy(data, 10, payload, 0, width);
            blob = new RomSlotBlob(header, shadow, width, payload);
            return true;
        }

        // Primeiro int32 LE do payload — o valor gravado no offset shadow (mW para slots de potência, mA para correntes).
        public static long? BlobValue(RomSlotBlob blob)
        {
            if (blob.Width < 4 || blob.Payload == null)
            {
                return null;
            }
            return BitConverter.ToInt32(blob.Payload, 0);
        }

        // Reconstrói o blob com um novo valor bruto. Largura 4 grava um int32 LE; largura 8 grava o valor duas vezes
        // (a placa mantém um campo duplicado adjacente em sincronia). Cabeçalho e offset shadow são preservados.
        public static bool BuildValueBlob(RomSlotBlob original, long newValue, out byte[] blob)
        {
            blob = null;
            if (original.Width != 4 && original.Width != 8)
            {
                return false;
            }
            if (newValue < int.MinValue || newValue > int.MaxValue)
            {
                return false;
            }
            byte[] value = BitConverter.GetBytes((int)newValue);
            blob = new byte[10 + original.Width];
            Buffer.BlockCopy(original.Header, 0, blob, 0, 5);
            Buffer.BlockCopy(BitConverter.GetBytes(original.ShadowOffset), 0, blob, 5, 4);
            blob[9] = (byte)original.Width;
            Buffer.BlockCopy(value, 0, blob, 10, 4);
            if (original.Width == 8)
            {
                Buffer.BlockCopy(value, 0, blob, 14, 4);
            }
            return true;
        }

        // ------------------------- inventário de slots ----------------------

        // Lê todos os romOverrideXX. Entradas não-BINARY ficam null (contam para o prefixo denso).
        public static Dictionary<int, byte[]> ReadAllSlots(RegistryKey key, int maxSlot)
        {
            Dictionary<int, byte[]> result = new Dictionary<int, byte[]>();
            if (key == null)
            {
                return result;
            }
            string[] names;
            try
            {
                names = key.GetValueNames();
            }
            catch
            {
                return result;
            }
            foreach (string name in names)
            {
                int index;
                if (!TryParseSlot(name, out index) || index < 0 || index > maxSlot)
                {
                    continue;
                }
                try
                {
                    if (key.GetValueKind(name) != RegistryValueKind.Binary)
                    {
                        result[index] = null;
                    }
                    else
                    {
                        result[index] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                    }
                }
                catch
                {
                    result[index] = null;
                }
            }
            return result;
        }

        // Regra do prefixo denso: slots 0..N devem existir todos. firstMissing = primeiro índice ausente.
        public static bool IsDensePrefix(Dictionary<int, byte[]> existing, out int firstMissing)
        {
            firstMissing = 0;
            while (existing.ContainsKey(firstMissing))
            {
                firstMissing++;
            }
            foreach (KeyValuePair<int, byte[]> item in existing)
            {
                if (item.Key > firstMissing)
                {
                    return false; // sparse: existe slot depois de um buraco
                }
            }
            return true;
        }

        // Próximo slot livre do prefixo denso, ou -1 se cheio/sparse.
        public static int AllocateNextSlot(Dictionary<int, byte[]> existing, int maxSlot)
        {
            int firstMissing;
            if (!IsDensePrefix(existing, out firstMissing))
            {
                return -1;
            }
            if (firstMissing > maxSlot)
            {
                return -1;
            }
            return firstMissing;
        }

        // Captura única: despeja todos os slots com campos parseados. RODE ISSO na máquina alvo enquanto o NvpwrControl ainda estiver instalado e salve a saída como config/evidência (os shadow offsets reais da SUA vBIOS).
        public static string CaptureExisting(RegistryKey key, int maxSlot)
        {
            StringBuilder sb = new StringBuilder();
            Dictionary<int, byte[]> slots = ReadAllSlots(key, maxSlot);
            if (slots.Count == 0)
            {
                return "no romOverride values present";
            }
            List<int> indices = new List<int>(slots.Keys);
            indices.Sort();
            foreach (int idx in indices)
            {
                byte[] data = slots[idx];
                sb.Append(SlotName(idx));
                if (data == null)
                {
                    sb.AppendLine("  <non-binary or unreadable>");
                    continue;
                }
                sb.Append("  ").Append(data.Length).Append("B ");
                for (int i = 0; i < data.Length; i++)
                {
                    sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
                    if (i + 1 < data.Length)
                    {
                        sb.Append(' ');
                    }
                }
                int shadow;
                int raw;
                string parsed = "";
                if (TryParseCanonical(data, out shadow, out raw))
                {
                    parsed = " canonical shadow=0x" + shadow.ToString("X", CultureInfo.InvariantCulture) + " raw=" + raw.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    int pw;
                    byte[] payload;
                    string mode;
                    if (TryParseSized(data, null, out shadow, out pw, out payload, out mode))
                    {
                        parsed = " sized[" + mode + "] shadow=0x" + shadow.ToString("X", CultureInfo.InvariantCulture) + " width=" + pw.ToString(CultureInfo.InvariantCulture);
                    }
                }
                sb.Append(parsed).AppendLine();
            }
            return sb.ToString();
        }
    }
}
