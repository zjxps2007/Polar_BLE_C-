// Ported from Polar BLE SDK. Copyright Polar Electro Oy. See LICENSES/Polar_SDK_License.txt.
using System;
using System.Security.Cryptography;

namespace PolarBle.Protocol.Pmd
{
    public enum PmdSecurityStrategy : byte { None = 0, Xor = 1, Aes128 = 2, Aes256 = 3 }
    public sealed class PmdSecret
    {
        private readonly byte[] _key;
        public PmdSecurityStrategy Strategy { get; }
        public PmdSecret(PmdSecurityStrategy strategy, byte[] key)
        {
            if (strategy > PmdSecurityStrategy.Aes256 ||
                (strategy == PmdSecurityStrategy.None && key.Length != 0) ||
                (strategy == PmdSecurityStrategy.Xor && key.Length == 0) ||
                (strategy == PmdSecurityStrategy.Aes128 && key.Length != 16) ||
                (strategy == PmdSecurityStrategy.Aes256 && key.Length != 32))
                throw new ArgumentException("Invalid PMD security strategy/key length.");
            Strategy = strategy; _key = (byte[])key.Clone();
        }
        public byte[] ExportKey() => (byte[])_key.Clone();
        public byte[] SerializeToSettings()
        {
            var result = new byte[3 + _key.Length]; result[0] = 6; result[1] = 1; result[2] = (byte)Strategy;
            Array.Copy(_key, 0, result, 3, _key.Length); return result;
        }
        public byte[] Decrypt(byte[] ciphertext)
        {
            if (Strategy == PmdSecurityStrategy.None) return (byte[])ciphertext.Clone();
            if (Strategy == PmdSecurityStrategy.Xor)
            { var result = new byte[ciphertext.Length]; for (int i = 0; i < result.Length; i++) result[i] = (byte)(ciphertext[i] ^ _key[0]); return result; }
            if (ciphertext.Length % 16 != 0) throw new PolarProtocolException("AES PMD payload is not block aligned.");
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = _key;
                using (var decryptor = aes.CreateDecryptor()) return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
        }
    }
}
