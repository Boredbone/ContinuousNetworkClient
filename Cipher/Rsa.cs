using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security;


namespace Boredbone.ContinuousNetworkClient.Cipher
{

    public class Rsa
    {

        const string rsaPrivatePemHeader = "-----BEGIN RSA PRIVATE KEY-----";
        const string rsaPrivatePemFooter = "-----END RSA PRIVATE KEY-----";
        const string publicPemHeader = "-----BEGIN PUBLIC KEY-----";
        const string publicPemFooter = "-----END PUBLIC KEY-----";
        const string pkcs8PemHeader = "-----BEGIN PRIVATE KEY-----";
        const string pkcs8PemFooter = "-----END PRIVATE KEY-----";
        const string encryptedPkcs8PemHeader = "-----BEGIN ENCRYPTED PRIVATE KEY-----";
        const string encryptedPkcs8PemFooter = "-----END ENCRYPTED PRIVATE KEY-----";

        // encoded OID sequence for  PKCS #1 rsaEncryption szOID_RSA_RSA = "1.2.840.113549.1.1.1"
        private static readonly byte[] Pkcs1SeqOID
            = { 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00 };


        public static byte[] Encrypt(string text, string publickey)
            => Encrypt(text, publickey, Encoding.UTF8);

        public static byte[] Encrypt(string text, string publickey, Encoding encoding)
        {
            using (var rsa = CreateProvider(publickey.Trim()))
            {
                return rsa.Encrypt(encoding.GetBytes(text), true);
            }
        }


        public static string Decrypt(byte[] data, string privatekey)
            => Decrypt(data, privatekey, Encoding.UTF8);

        public static string Decrypt(byte[] data, string privatekey, Encoding encoding)
        {
            using (var rsa = CreateProvider(privatekey.Trim()))
            {
                return encoding.GetString(rsa.Decrypt(data, true));
            }
        }

        private static RSACryptoServiceProvider CreateProvider(string key)
        {
            if (key.StartsWith("<"))
            {
                var rsa = new RSACryptoServiceProvider();
                rsa.FromXmlString(key);
                return rsa;
            }
            else if (key.StartsWith("-"))
            {
                return DecodePem(key);
            }
            throw new ArgumentException("invalid key");
        }


        // ------- Decode PEM pubic, private or pkcs8 key ----------------
        private static RSACryptoServiceProvider DecodePem(string pemstr)
        {
            if (pemstr.StartsWith(publicPemHeader))
            {
                return DecodeX509PublicKey(Convert.FromBase64String
                    (Extract(pemstr, publicPemHeader, publicPemFooter)));
            }
            else if (pemstr.StartsWith(rsaPrivatePemHeader))
            {
                return DecodeRSAPrivateKey(DecodeOpenSSLPrivateKey(pemstr));
            }
            else if (pemstr.StartsWith(pkcs8PemHeader))
            {
                return DecodePrivateKeyInfo(Convert.FromBase64String
                    (Extract(pemstr, pkcs8PemHeader, pkcs8PemFooter)));
            }
            else if (pemstr.StartsWith(encryptedPkcs8PemHeader))
            {
                return DecodeEncryptedPrivateKeyInfo(Convert.FromBase64String
                    (Extract(pemstr, encryptedPkcs8PemHeader, encryptedPkcs8PemFooter)));
            }
            else
            {
                //Console.WriteLine("Not a PEM public, private key or a PKCS #8");
                //return;
            }
            throw new ArgumentException("invalid pem");
        }



        private static string Extract(string source, string header, string footer)
        {
            if (!source.StartsWith(header) || !source.EndsWith(footer))
            {
                throw new ArgumentException("string has no header or footer");
            }
            return source.Substring(header.Length, source.Length - header.Length - footer.Length).Trim();
        }


        //------- Parses binary asn.1 PKCS #8 PrivateKeyInfo; returns RSACryptoServiceProvider ---
        private static RSACryptoServiceProvider DecodePrivateKeyInfo(byte[] pkcs8)
        {
            // encoded OID sequence for  PKCS #1 rsaEncryption szOID_RSA_RSA = "1.2.840.113549.1.1.1"
            // this byte[] includes the sequence byte and terminal encoded null 
            byte[] SeqOID = { 0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00 };
            byte[] seq = new byte[15];
            // ---------  Set up stream to read the asn.1 encoded SubjectPublicKeyInfo blob  ------
            using (var mem = new MemoryStream(pkcs8))
            using (var binr = new BinaryReader(mem))
            {
                int lenstream = (int)mem.Length;
                byte bt = 0;
                ushort twobytes = 0;

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                {
                    //data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();   //advance 2 bytes
                }
                else
                {
                    return null;
                }


                bt = binr.ReadByte();
                if (bt != 0x02)
                {
                    return null;
                }

                twobytes = binr.ReadUInt16();

                if (twobytes != 0x0001)
                {
                    return null;
                }

                seq = binr.ReadBytes(15);       //read the Sequence OID
                if (!CompareBytearrays(seq, SeqOID))
                {    //make sure Sequence for OID is correct
                    return null;
                }

                bt = binr.ReadByte();
                if (bt != 0x04)
                {//expect an Octet string 
                    return null;
                }

                bt = binr.ReadByte();       //read next byte, or next 2 bytes is  0x81 or 0x82; otherwise bt is the byte count
                if (bt == 0x81)
                {
                    binr.ReadByte();
                }
                else if (bt == 0x82)
                {
                    binr.ReadUInt16();
                }
                //------ at this stage, the remaining sequence should be the RSA private key

                return DecodeRSAPrivateKey(binr.ReadBytes((int)(lenstream - mem.Position)));
            }
        }









        //------- Parses binary asn.1 EncryptedPrivateKeyInfo; returns RSACryptoServiceProvider ---
        private static RSACryptoServiceProvider DecodeEncryptedPrivateKeyInfo
            (byte[] encpkcs8, SecureString password = null)
        {
            // encoded OID sequence for  PKCS #1 rsaEncryption szOID_RSA_RSA = "1.2.840.113549.1.1.1"
            // this byte[] includes the sequence byte and terminal encoded null 
            byte[] OIDpkcs5PBES2 = { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x05, 0x0D };
            byte[] OIDpkcs5PBKDF2 = { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x05, 0x0C };
            byte[] OIDdesEDE3CBC = { 0x06, 0x08, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x03, 0x07 };
            byte[] seqdes = new byte[10];
            byte[] seq = new byte[11];


            // ---------  Set up stream to read the asn.1 encoded SubjectPublicKeyInfo blob  ------
            using (var mem = new MemoryStream(encpkcs8))
            using (var binr = new BinaryReader(mem))
            {
                int lenstream = (int)mem.Length;
                byte bt = 0;
                ushort twobytes = 0;


                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                { //data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();   //advance 2 bytes
                }
                else
                {
                    return null;
                }

                twobytes = binr.ReadUInt16();   //inner sequence
                if (twobytes == 0x8130)
                {
                    binr.ReadByte();
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();
                }


                seq = binr.ReadBytes(11);       //read the Sequence OID
                if (!CompareBytearrays(seq, OIDpkcs5PBES2))
                { //is it a OIDpkcs5PBES2 ?
                    return null;
                }

                twobytes = binr.ReadUInt16();   //inner sequence for pswd salt
                if (twobytes == 0x8130)
                {
                    binr.ReadByte();
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();
                }

                twobytes = binr.ReadUInt16();   //inner sequence for pswd salt
                if (twobytes == 0x8130)
                {
                    binr.ReadByte();
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();
                }

                seq = binr.ReadBytes(11);       //read the Sequence OID
                if (!CompareBytearrays(seq, OIDpkcs5PBKDF2))
                {    //is it a OIDpkcs5PBKDF2 ?
                    return null;
                }

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                {
                    binr.ReadByte();
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();
                }

                bt = binr.ReadByte();
                if (bt != 0x04)
                {     //expect octet string for salt
                    return null;
                }
                var saltsize = binr.ReadByte();
                var salt = binr.ReadBytes(saltsize);

                bt = binr.ReadByte();
                if (bt != 0x02)
                {     //expect an integer for PBKF2 interation count
                    return null;
                }

                int iterations;
                int itbytes = binr.ReadByte();  //PBKD2 iterations should fit in 2 bytes.
                if (itbytes == 1)
                {
                    iterations = binr.ReadByte();
                }
                else if (itbytes == 2)
                {
                    iterations = 256 * binr.ReadByte() + binr.ReadByte();
                }
                else
                {
                    return null;
                }

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                {
                    binr.ReadByte();
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();
                }


                seqdes = binr.ReadBytes(10);        //read the Sequence OID
                if (!CompareBytearrays(seqdes, OIDdesEDE3CBC))
                {  //is it a OIDdes-EDE3-CBC ?
                    return null;
                }

                bt = binr.ReadByte();
                if (bt != 0x04)
                {    //expect octet string for IV
                    return null;
                }
                var ivsize = binr.ReadByte();   // IV byte size should fit in one byte (24 expected for 3DES)
                var IV = binr.ReadBytes(ivsize);

                bt = binr.ReadByte();
                if (bt != 0x04)
                {    // expect octet string for encrypted PKCS8 data
                    return null;
                }


                bt = binr.ReadByte();
                int encblobsize;
                if (bt == 0x81)
                {
                    encblobsize = binr.ReadByte();  // data size in next byte
                }
                else if (bt == 0x82)
                {
                    encblobsize = 256 * binr.ReadByte() + binr.ReadByte();
                }
                else
                {
                    encblobsize = bt;       // we already have the data size
                }


                var encryptedpkcs8 = binr.ReadBytes(encblobsize);
                //if(verbose)
                //	showBytes("Encrypted PKCS8 blob", encryptedpkcs8) ;


                var pkcs8 = DecryptPBDK2(encryptedpkcs8, salt, IV, password, iterations);
                if (pkcs8 == null)
                {  // probably a bad pswd entered.
                    return null;
                }

                //if(verbose)
                //	showBytes("Decrypted PKCS #8", pkcs8) ;
                //----- With a decrypted pkcs #8 PrivateKeyInfo blob, decode it to an RSA ---
                return DecodePrivateKeyInfo(pkcs8);
            }
        }





        //  ------  Uses PBKD2 to derive a 3DES key and decrypts data --------
        private static byte[] DecryptPBDK2(byte[] edata, byte[] salt, byte[] IV, SecureString secpswd, int iterations)
        {
            IntPtr unmanagedPswd = IntPtr.Zero;
            byte[] psbytes = new byte[secpswd.Length];
            unmanagedPswd = Marshal.SecureStringToGlobalAllocAnsi(secpswd);
            Marshal.Copy(unmanagedPswd, psbytes, 0, psbytes.Length);
            Marshal.ZeroFreeGlobalAllocAnsi(unmanagedPswd);

            try
            {
                using (var kd = new Rfc2898DeriveBytes(psbytes, salt, iterations))
                using (var decAlg = TripleDES.Create())
                {
                    decAlg.Key = kd.GetBytes(24);
                    decAlg.IV = IV;
                    using (var memstr = new MemoryStream())
                    using (var decrypt = new CryptoStream(memstr, decAlg.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        decrypt.Write(edata, 0, edata.Length);
                        decrypt.Flush();
                        return memstr.ToArray();
                    }
                }
            }
            catch (Exception)
            {
                //Console.WriteLine("Problem decrypting: {0}", e.Message);
                return null;
            }
        }


        //------- Parses binary asn.1 X509 SubjectPublicKeyInfo; returns RSACryptoServiceProvider ---
        private static RSACryptoServiceProvider DecodeX509PublicKey(byte[] x509key)
        {
            // ---------  Set up stream to read the asn.1 encoded SubjectPublicKeyInfo blob  ------
            using (var mem = new MemoryStream(x509key))
            using (var binr = new BinaryReader(mem))
            {
                ushort twobytes = 0;

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                {//data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();   //advance 2 bytes
                }
                else
                {
                    return null;
                }

                var seq = binr.ReadBytes(15);       //read the Sequence OID
                if (!CompareBytearrays(seq, Pkcs1SeqOID))
                {    //make sure Sequence for OID is correct
                    return null;
                }

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8103)
                { //data read as little endian order (actual data order for Bit string is 03 81)
                    binr.ReadByte();    //advance 1 byte
                }
                else if (twobytes == 0x8203)
                {
                    binr.ReadInt16();   //advance 2 bytes
                }
                else
                {
                    return null;
                }

                var bt = binr.ReadByte();
                if (bt != 0x00)
                {     //expect null byte next
                    return null;
                }

                twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                { //data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();   //advance 2 bytes
                }
                else
                {
                    return null;
                }

                twobytes = binr.ReadUInt16();
                byte lowbyte = 0x00;
                byte highbyte = 0x00;

                if (twobytes == 0x8102)
                { //data read as little endian order (actual data order for Integer is 02 81)
                    lowbyte = binr.ReadByte();  // read next bytes which is bytes in modulus
                }
                else if (twobytes == 0x8202)
                {
                    highbyte = binr.ReadByte(); //advance 2 bytes
                    lowbyte = binr.ReadByte();
                }
                else
                {
                    return null;
                }
                byte[] modint = { lowbyte, highbyte, 0x00, 0x00 };   //reverse byte order since asn.1 key uses big endian order
                int modsize = BitConverter.ToInt32(modint, 0);

                byte firstbyte = binr.ReadByte();
                binr.BaseStream.Seek(-1, SeekOrigin.Current);

                if (firstbyte == 0x00)
                {   //if first byte (highest order) of modulus is zero, don't include it
                    binr.ReadByte();    //skip this null byte
                    modsize -= 1;   //reduce modulus buffer size by 1
                }

                byte[] modulus = binr.ReadBytes(modsize);   //read the modulus bytes

                if (binr.ReadByte() != 0x02)
                {           //expect an Integer for the exponent data
                    return null;
                }
                int expbytes = (int)binr.ReadByte();        // should only need one byte for actual exponent data (for all useful values)
                byte[] exponent = binr.ReadBytes(expbytes);


                // ------- create RSACryptoServiceProvider instance and initialize with public key -----
                var RSA = new RSACryptoServiceProvider();
                var RSAKeyInfo = new RSAParameters
                {
                    Modulus = modulus,
                    Exponent = exponent
                };
                RSA.ImportParameters(RSAKeyInfo);
                return RSA;
            }
        }



        //------- Parses binary ans.1 RSA private key; returns RSACryptoServiceProvider  ---
        private static RSACryptoServiceProvider DecodeRSAPrivateKey(byte[] privkey)
        {
            // ---------  Set up stream to decode the asn.1 encoded RSA private key  ------
            using (var mem = new MemoryStream(privkey))
            using (var binr = new BinaryReader(mem))
            {
                var twobytes = binr.ReadUInt16();
                if (twobytes == 0x8130)
                { //data read as little endian order (actual data order for Sequence is 30 81)
                    binr.ReadByte();    //advance 1 byte
                }
                else if (twobytes == 0x8230)
                {
                    binr.ReadInt16();   //advance 2 bytes
                }
                else
                {
                    return null;
                }

                if (binr.ReadUInt16() != 0x0102)
                { //version number
                    return null;
                }
                if (binr.ReadByte() != 0x00)
                {
                    return null;
                }

                //------  all private key components are Integer sequences ----
                var MODULUS = binr.ReadBytes(GetIntegerSize(binr));
                var E = binr.ReadBytes(GetIntegerSize(binr));
                var D = binr.ReadBytes(GetIntegerSize(binr));
                var P = binr.ReadBytes(GetIntegerSize(binr));
                var Q = binr.ReadBytes(GetIntegerSize(binr));
                var DP = binr.ReadBytes(GetIntegerSize(binr));
                var DQ = binr.ReadBytes(GetIntegerSize(binr));
                var IQ = binr.ReadBytes(GetIntegerSize(binr));

                // ------- create RSACryptoServiceProvider instance and initialize with public key -----
                var RSA = new RSACryptoServiceProvider();
                var RSAparams = new RSAParameters
                {
                    Modulus = MODULUS,
                    Exponent = E,
                    D = D,
                    P = P,
                    Q = Q,
                    DP = DP,
                    DQ = DQ,
                    InverseQ = IQ
                };
                RSA.ImportParameters(RSAparams);
                return RSA;
            }
        }



        private static int GetIntegerSize(BinaryReader binr)
        {
            byte bt = 0;
            byte lowbyte = 0x00;
            byte highbyte = 0x00;
            int count = 0;
            bt = binr.ReadByte();
            if (bt != 0x02)     //expect integer
                return 0;
            bt = binr.ReadByte();

            if (bt == 0x81)
            {
                count = binr.ReadByte();    // data size in next byte
            }
            else
            {
                if (bt == 0x82)
                {
                    highbyte = binr.ReadByte(); // data size in next 2 bytes
                    lowbyte = binr.ReadByte();
                    byte[] modint = { lowbyte, highbyte, 0x00, 0x00 };
                    count = BitConverter.ToInt32(modint, 0);
                }
                else
                {
                    count = bt;     // we already have the data size
                }
            }

            while (binr.ReadByte() == 0x00)
            {   //remove high order zeros in data
                count -= 1;
            }
            binr.BaseStream.Seek(-1, SeekOrigin.Current);       //last ReadByte wasn't a removed zero, so back up a byte
            return count;
        }




        //-----  Get the binary RSA PRIVATE key, decrypting if necessary ----
        private static byte[] DecodeOpenSSLPrivateKey(string instr, SecureString password = null)
        {
            var pvkstr = Extract(instr, rsaPrivatePemHeader, rsaPrivatePemFooter);
            try
            {
                // if there are no PEM encryption info lines, this is an UNencrypted PEM private key
                return Convert.FromBase64String(pvkstr);
            }
            catch (System.FormatException)
            {
                //if can't b64 decode, it must be an encrypted private key
                //Console.WriteLine("Not an unencrypted OpenSSL PEM private key");  
            }

            byte[] binkey;
            byte[] salt;

            using (var str = new StringReader(pvkstr))
            {

                //-------- read PEM encryption info. lines and extract salt -----
                if (!str.ReadLine().StartsWith("Proc-Type: 4,ENCRYPTED"))
                {
                    return null;
                }
                string saltline = str.ReadLine();
                if (!saltline.StartsWith("DEK-Info: DES-EDE3-CBC,"))
                {
                    return null;
                }
                string saltstr = saltline.Substring(saltline.IndexOf(",") + 1).Trim();
                salt = new byte[saltstr.Length / 2];
                for (int i = 0; i < salt.Length; i++)
                {
                    salt[i] = Convert.ToByte(saltstr.Substring(i * 2, 2), 16);
                }
                if (!(str.ReadLine() == ""))
                {
                    return null;
                }

                //------ remaining b64 data is encrypted RSA key ----
                string encryptedstr = str.ReadToEnd();

                try
                {   //should have b64 encrypted RSA key now
                    binkey = Convert.FromBase64String(encryptedstr);
                }
                catch (System.FormatException)
                {  // bad b64 data.
                    return null;
                }
            }
            //------ Get the 3DES 24 byte key using PDK used by OpenSSL ----

            //Console.Write("\nEnter password to derive 3DES key: ");
            //string pswd = Console.ReadLine();
            byte[] deskey = GetOpenSSL3deskey(salt, password, 1, 2);    // count=1 (for OpenSSL implementation); 2 iterations to get at least 24 bytes
            if (deskey == null)
            {
                return null;
            }

            //------ Decrypt the encrypted 3des-encrypted RSA private key ------
            return DecryptKey(binkey, deskey, salt);   //OpenSSL uses salt value in PEM header also as 3DES IV
        }




        // ----- Decrypt the 3DES encrypted RSA private key ----------

        private static byte[] DecryptKey(byte[] cipherData, byte[] desKey, byte[] IV)
        {
            using (var memst = new MemoryStream())
            using (var alg = TripleDES.Create())
            {
                alg.Key = desKey;
                alg.IV = IV;
                try
                {
                    using (var cs = new CryptoStream(memst, alg.CreateDecryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(cipherData, 0, cipherData.Length);
                    }
                    return memst.ToArray();
                }
                catch (Exception)
                {
                    //Console.WriteLine(exc.Message);
                    return null;
                }
            }
        }




        //-----   OpenSSL PBKD uses only one hash cycle (count); miter is number of iterations required to build sufficient bytes ---
        private static byte[] GetOpenSSL3deskey(byte[] salt, SecureString secpswd, int count, int miter)
        {
            IntPtr unmanagedPswd = IntPtr.Zero;
            int HASHLENGTH = 16;    //MD5 bytes
            byte[] keymaterial = new byte[HASHLENGTH * miter];     //to store contatenated Mi hashed results


            byte[] psbytes = new byte[secpswd.Length];
            unmanagedPswd = Marshal.SecureStringToGlobalAllocAnsi(secpswd);
            Marshal.Copy(unmanagedPswd, psbytes, 0, psbytes.Length);
            Marshal.ZeroFreeGlobalAllocAnsi(unmanagedPswd);

            //UTF8Encoding utf8 = new UTF8Encoding();
            //byte[] psbytes = utf8.GetBytes(pswd);

            // --- contatenate salt and pswd bytes into fixed data array ---
            byte[] data00 = new byte[psbytes.Length + salt.Length];
            Array.Copy(psbytes, data00, psbytes.Length);        //copy the pswd bytes
            Array.Copy(salt, 0, data00, psbytes.Length, salt.Length);   //concatenate the salt bytes

            // ---- do multi-hashing and contatenate results  D1, D2 ...  into keymaterial bytes ----
            using (var md5 = new MD5CryptoServiceProvider())
            {
                byte[] result = null;
                byte[] hashtarget = new byte[HASHLENGTH + data00.Length];   //fixed length initial hashtarget

                for (int j = 0; j < miter; j++)
                {
                    // ----  Now hash consecutively for count times ------
                    if (j == 0)
                        result = data00;    //initialize 
                    else
                    {
                        Array.Copy(result, hashtarget, result.Length);
                        Array.Copy(data00, 0, hashtarget, result.Length, data00.Length);
                        result = hashtarget;
                        //Console.WriteLine("Updated new initial hash target:") ;
                        //showBytes(result) ;
                    }

                    for (int i = 0; i < count; i++)
                        result = md5.ComputeHash(result);
                    Array.Copy(result, 0, keymaterial, j * HASHLENGTH, result.Length);  //contatenate to keymaterial
                }
                //showBytes("Final key material", keymaterial);
                byte[] deskey = new byte[24];
                Array.Copy(keymaterial, deskey, deskey.Length);

                Array.Clear(psbytes, 0, psbytes.Length);
                Array.Clear(data00, 0, data00.Length);
                Array.Clear(result, 0, result.Length);
                Array.Clear(hashtarget, 0, hashtarget.Length);
                Array.Clear(keymaterial, 0, keymaterial.Length);

                return deskey;
            }
        }

        private static bool CompareBytearrays(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            int i = 0;
            foreach (byte c in a)
            {
                if (c != b[i])
                    return false;
                i++;
            }
            return true;
        }

    }
}
