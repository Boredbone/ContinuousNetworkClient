using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IO;

namespace Boredbone.ContinuousNetworkClient.Cipher
{

    public class Aes
    {
        const int AesBlockSize = 128;
        const int aesKeySize = 128;


        public static byte[] Encrypt(string plainText, string key)
            => Encrypt(plainText, key, true, Encoding.UTF8);

        public static byte[] Encrypt(string plainText, string key, bool nullEndMark, Encoding encoding)
        {
            var csp = new AesCryptoServiceProvider()
            {
                BlockSize = AesBlockSize,
                KeySize = aesKeySize,
                Mode = CipherMode.CBC,
                Padding = PaddingMode.PKCS7,
            };

            csp.GenerateIV();
            csp.Key = Convert.FromBase64String(key);

            byte[] bytesIV = csp.IV;

            using (var outms = new MemoryStream())
            using (var encryptor = csp.CreateEncryptor())
            using (var cs = new CryptoStream(outms, encryptor, CryptoStreamMode.Write))
            using (var writer = new StreamWriter(cs))
            {
                outms.Write(bytesIV, 0, 16);

                byte[] toEncrypt = encoding.GetBytes(plainText);

                cs.Write(toEncrypt, 0, toEncrypt.Length);
                if (nullEndMark)
                {
                    cs.Write(new byte[] { 0 }, 0, 1);
                }
                cs.FlushFinalBlock();

                return outms.ToArray();
            }
        }

        public static string Decrypt(byte[] data, string key)
            => Decrypt(data, key, Encoding.UTF8);

        public static string Decrypt(byte[] data, string key, Encoding encoding)
        {
            using (var inms = new MemoryStream(data))
            {
                byte[] iv = new byte[16];
                inms.Read(iv, 0, iv.Length);

                var csp = new AesCryptoServiceProvider()
                {
                    BlockSize = AesBlockSize,
                    KeySize = aesKeySize,
                    Mode = CipherMode.CBC,
                    Padding = PaddingMode.PKCS7,
                    IV = iv,
                    Key = Convert.FromBase64String(key),
                };

                using (var decryptor = csp.CreateDecryptor())
                using (var cs = new CryptoStream(inms, decryptor, CryptoStreamMode.Read))
                using (var reader = new StreamReader(cs, encoding))
                {
                    return reader.ReadToEnd();
                }
            }
        }


        public static (string iv, string key) CreateAesKey()
        {
            var csp = new AesCryptoServiceProvider
            {
                BlockSize = AesBlockSize,
                KeySize = aesKeySize,
                Mode = CipherMode.CBC,
                Padding = PaddingMode.PKCS7
            };

            csp.GenerateIV();
            csp.GenerateKey();

            var iv = Convert.ToBase64String(csp.IV);
            var key = Convert.ToBase64String(csp.Key);
            return (iv, key);
        }
    }
}
