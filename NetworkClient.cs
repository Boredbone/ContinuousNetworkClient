using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using Reactive.Bindings;
using System.Reactive.Disposables;
using Reactive.Bindings.Extensions;
using System.Reactive.Linq;
using System.Net;
using System.Threading;
using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.IO;
using System.Security.Authentication;
using Boredbone.ContinuousNetworkClient.Packet;
using Boredbone.Utility;

namespace Boredbone.ContinuousNetworkClient
{

    public class NetworkOptions
    {
        public IPAddress[] IpAddresss { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; }
    }

    public class NetworkClient<TReceivePacket, TTransmitPacket> : IDisposable
        where TReceivePacket : IReceivePacket, new()
        where TTransmitPacket : ITransmitPacket
    {
        private CompositeDisposable disposables;

        private Subject<TReceivePacket> ReceivedSubject { get; }
        public IObservable<TReceivePacket> Received => this.ReceivedSubject.AsObservable();

        public IServerCertificate ServerCertificate { get; set; } = null;


        private readonly AsyncLock asyncLock;

        private readonly CancellationTokenSource cancellationTokenSource;

        private NetworkClientWorker clientWorker;

        private NetworkOptions nextOptions = null;

        private int retryToConnectSameServerCount;
        public int MaxRetryCountOfConnectingSameServer { get; set; } = 10;
        public TimeSpan ConnectionStableTimeThreshold { get; set; } = TimeSpan.FromMinutes(5);


        public NetworkClient()
        {
            this.asyncLock = new AsyncLock();
            this.cancellationTokenSource = new CancellationTokenSource();

            this.disposables = new CompositeDisposable();

            this.ReceivedSubject = new Subject<TReceivePacket>();

            this.clientWorker = null;


            Disposable.Create(() =>
            {
                this.Cancel();

                this.clientWorker?.Dispose();

                this.ReceivedSubject.Dispose();
            })
            .AddTo(this.disposables);
        }




        public void Cancel()
        {
            this.cancellationTokenSource.Cancel();
            //await this.SetWorkerAsync(null);
        }

        public async Task WorkAsync(NetworkOptions options)
        {
            try
            {
                Console.WriteLine("start connection");
                await this.RedirectAsync(options);
                Console.WriteLine($"connect to {this.nextOptions.HostName}:{this.nextOptions.Port}");
                this.retryToConnectSameServerCount++;
                await this.WorkMainAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        private async Task WorkMainAsync()
        {

            var cancellationToken = this.cancellationTokenSource.Token;


            while (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"try to connect server. {this.retryToConnectSameServerCount}th try");
                var startTime = DateTimeOffset.UtcNow;

                try
                {
                    await this.ChangeWorkerAsync().ConfigureAwait(false);

                    var worker = await this.GetWorkerAsync().ConfigureAwait(false);
                    if (worker != null)
                    {
                        await worker.WorkAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException e)
                {
                    Console.WriteLine("client excep\nOperationCanceledException :" + e.Message);
                    if (e.InnerException != null)
                    {
                        Console.WriteLine("client excep inner\n" + e.InnerException.ToString());
                    }
                    break;
                }
                catch (Exception e)
                {
                    Console.WriteLine("client excep\n" + e.ToString());
                    if (e.InnerException != null)
                    {
                        Console.WriteLine("client excep inner\n" + e.InnerException.ToString());
                    }
                    if (!CheckConnectionRetryCount(startTime))
                    {
                        throw;
                    }
                    await Task.Delay(5000);
                    continue;
                }

                if (!CheckConnectionRetryCount(startTime))
                {
                    throw new Exception("cannnot connect to server");
                }
            }
        }

        private bool CheckConnectionRetryCount(in DateTimeOffset startTime)
        {
            var now = DateTimeOffset.UtcNow;

            if (now - startTime > this.ConnectionStableTimeThreshold)
            {
                this.retryToConnectSameServerCount = 0;
                return true;
            }

            this.retryToConnectSameServerCount++;
            return (this.retryToConnectSameServerCount <= this.MaxRetryCountOfConnectingSameServer);
        }

        private async Task<NetworkClientWorker> GetWorkerAsync()
        {
            using (var locking = await this.asyncLock.LockAsync())
            {
                return this.clientWorker;
            }
        }



        public Task RedirectAsync(NetworkOptions options)
            => (options != null) ? this.CloseWorkerAsync(options) : throw new ArgumentNullException();

        private Task ChangeWorkerAsync() => this.CloseWorkerAsync(null);

        private async Task CloseWorkerAsync(NetworkOptions nextOptions)
        {
            NetworkClientWorker currentWorker;
            using (var locking = await this.asyncLock.LockAsync().ConfigureAwait(false))
            {
                currentWorker = this.clientWorker;
                this.clientWorker = null;

                if (nextOptions != null)
                {
                    // set next server information, not prepare new client
                    this.nextOptions = nextOptions;
                    this.retryToConnectSameServerCount = 0;
                }
                else
                {
                    // create new client using next server information
                    this.clientWorker = new NetworkClientWorker(this.nextOptions, this.ServerCertificate);
                    this.SubscribeWorker();
                }
            }
            currentWorker?.Dispose();
        }

        private void SubscribeWorker()
        {
            if (this.clientWorker != null)
            {
                this.clientWorker.Received.Subscribe(this.ReceivedSubject).AddTo(this.disposables);
            }
        }

        public async Task SendAsync(TTransmitPacket packet)
        {
            this.cancellationTokenSource.Token.ThrowIfCancellationRequested();
            var worker = await this.GetWorkerAsync().ConfigureAwait(false);
            if (worker != null)
            {
                await worker.SendAsync(packet, this.cancellationTokenSource.Token);
                Console.WriteLine("completed");
            }
            else
            {
                Console.WriteLine("There is no connction. request was ignored");
            }
        }


        public void Dispose()
        {
            this.disposables.Dispose();
        }


        private class NetworkClientWorker : IDisposable
        {

            private readonly CompositeDisposable disposables;

            private Stream stream;
            private TcpClient client;

            private Subject<TReceivePacket> ReceivedSubject { get; }
            public IObservable<TReceivePacket> Received => this.ReceivedSubject.AsObservable();

            private readonly NetworkStreamHelper<TReceivePacket, TTransmitPacket> streamHelper;

            private readonly NetworkOptions options;

            private readonly AsyncLock asyncLock;

            private readonly CancellationTokenSource cancellationTokenSource;

            private readonly IServerCertificate serverCertificate;

            public NetworkClientWorker(NetworkOptions options, IServerCertificate serverCertificate)
            {
                this.options = options;
                this.serverCertificate = serverCertificate;

                this.asyncLock = new AsyncLock();
                this.cancellationTokenSource = new CancellationTokenSource();

                this.disposables = new CompositeDisposable();

                this.ReceivedSubject = new Subject<TReceivePacket>();

                this.streamHelper = new NetworkStreamHelper<TReceivePacket, TTransmitPacket>();


                this.client = null;
                this.stream = null;

                Disposable.Create(() =>
                {
                    this.Cancel();

                    if (this.stream != null)
                    {
                        this.stream.Dispose();
                        this.stream = null;
                    }
                    if (this.client != null)
                    {
                        this.client.Dispose();
                        this.client = null;
                    }
                    this.streamHelper.Dispose();
                    this.ReceivedSubject.Dispose();
                })
                .AddTo(this.disposables);
            }

            ~NetworkClientWorker()
            {
                this.Dispose();
            }

            public void Cancel()
            {
                this.cancellationTokenSource.Cancel();
            }


            public async Task WorkAsync(CancellationToken parentCancellationToken)
            {
                try
                {
                    using (var cancellationTokenSource = CancellationTokenSource
                        .CreateLinkedTokenSource(this.cancellationTokenSource.Token, parentCancellationToken))
                    {
                        var cancellationToken = cancellationTokenSource.Token;

                        if (this.client == null)
                        {
                            if (this.stream != null)
                            {
                                this.stream.Dispose();
                                this.stream = null;
                            }
                            this.client = new TcpClient();
                            if (this.options.IpAddresss != null)
                            {
                                await client.ConnectAsync(this.options.IpAddresss, this.options.Port);
                            }
                            else
                            {
                                await client.ConnectAsync(this.options.HostName, this.options.Port);
                            }

                            Console.WriteLine(client.Client.LocalEndPoint);
                            Console.WriteLine(client.Client.RemoteEndPoint);



                            //client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                            /*
                            //TODO keep alive
                            {
                                byte[] inBuffer = new byte[12];

                                BitConverter.GetBytes(1).CopyTo(inBuffer, 0); // スイッチ
                                BitConverter.GetBytes(30000).CopyTo(inBuffer, 4); // Interval

                                client.Client.IOControl(IOControlCode.KeepAliveValues, inBuffer, null);
                            }*/
                        }

                        if (this.stream == null)
                        {
                            bool useSsl = true;
                            if (useSsl && this.serverCertificate != null)
                            {
                                var sslStream = new SslStream(client.GetStream(), false);
                                //var sslStream =
                                //    new SslStream(client.GetStream(), false,
                                //    RemoteCertificateValidationCallback, SelectLocalCertificate,
                                //    EncryptionPolicy.RequireEncryption);
                                //var sslStream =
                                //    new SslStream(client.GetStream(), false,
                                //    RemoteCertificateValidationCallback);

                                await sslStream.AuthenticateAsClientAsync(this.serverCertificate.ServerName,
                                    this.serverCertificate.CertificateCollection,
                                    SslProtocols.Ssl2 | SslProtocols.Ssl3 | SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
                                    false);

                                this.stream = sslStream;

                                if (sslStream.LocalCertificate == null)
                                {
                                    Console.WriteLine("local cert null");
                                }
                                else
                                {
                                    Console.WriteLine(sslStream.LocalCertificate);
                                }

                            }
                            else
                            {
                                this.stream = client.GetStream();
                            }
                        }



                        Console.WriteLine("connection start");

                        Console.WriteLine("stream " + ((this.stream == null) ? "null" : "active"));
                        Console.WriteLine("client " + ((this.client.Connected) ? "connected" : "disconnected"));

                        while (this.client.Connected)
                        {
                            await this.ReadAsync(cancellationToken);

                            await Task.Delay(100);

                            var socket = this.client?.Client;
                            if (socket == null || (socket.Poll(100_000, SelectMode.SelectRead) && (socket.Available == 0)))
                            {
                                break;
                            }
                        }
                    }

                    Console.WriteLine("socket disconnection is detected");
                }
                catch (OperationCanceledException e)
                {
                    Console.WriteLine("client worker excep\nOperationCanceledException :" + e.Message);
                    if (e.InnerException != null)
                    {
                        Console.WriteLine("client worker excep inner\n" + e.InnerException.ToString());
                    }
                    Console.WriteLine("parent" + parentCancellationToken.IsCancellationRequested);
                    Console.WriteLine("child" + this.cancellationTokenSource.Token.IsCancellationRequested);
                    throw;

                }
                catch (Exception e)
                {
                    Console.WriteLine("client worker excep\n" + e.ToString());
                    if (e.InnerException != null)
                    {
                        Console.WriteLine("client worker excep inner\n" + e.InnerException.ToString());
                    }
                    Console.WriteLine("parent" + parentCancellationToken.IsCancellationRequested);
                    Console.WriteLine("child" + this.cancellationTokenSource.Token.IsCancellationRequested);
                    throw;
                }
            }

            public async Task SendAsync(TTransmitPacket packet, CancellationToken parentCancellationToken)
            {
                //Console.WriteLine("write start");
                //Console.WriteLine("stream " + ((this.stream == null) ? "null" : "active"));
                //Console.WriteLine("client " + ((this.client.Connected) ? "connected" : "disconnected"));

                //var enc = System.Text.Encoding.UTF8;
                //var send_bytes = enc.GetBytes(text);

                using (var cancellationTokenSource = CancellationTokenSource
                    .CreateLinkedTokenSource(this.cancellationTokenSource.Token, parentCancellationToken))
                {
                    var cancellationToken = cancellationTokenSource.Token;

                    await this.streamHelper.WriteAsync(this.stream, packet, cancellationToken);

                    //await stream.WriteAsync(send_bytes, 0, send_bytes.Length);
                }
                //Console.WriteLine("stream " + ((this.stream == null) ? "null" : "active"));
                //Console.WriteLine("client " + ((this.client.Connected) ? "connected" : "disconnected"));
                //Console.WriteLine("write completed");
            }

            private async Task ReadAsync(CancellationToken cancellationToken)
            {

                var (isSucceeded, packet) = await this.streamHelper.ReadAsync(this.stream, cancellationToken);

                if (isSucceeded)
                {
                    this.ReceivedSubject.OnNext(packet);
                }
                else
                {
                    Console.WriteLine("receive failed");
                }
            }

            public void Dispose()
            {
                this.disposables.Dispose();
            }


            ////証明書の内容を表示するメソッド
            //private static void PrintCertificate(X509Certificate certificate)
            //{
            //    if (certificate == null)
            //    {
            //        return;
            //    }
            //    Console.WriteLine("===========================================");
            //    Console.WriteLine("Subject={0}", certificate.Subject);
            //    Console.WriteLine("Issuer={0}", certificate.Issuer);
            //    Console.WriteLine("Format={0}", certificate.GetFormat());
            //    Console.WriteLine("ExpirationDate={0}", certificate.GetExpirationDateString());
            //    Console.WriteLine("EffectiveDate={0}", certificate.GetEffectiveDateString());
            //    Console.WriteLine("KeyAlgorithm={0}", certificate.GetKeyAlgorithm());
            //    Console.WriteLine("PublicKey={0}", certificate.GetPublicKeyString());
            //    Console.WriteLine("SerialNumber={0}", certificate.GetSerialNumberString());
            //    Console.WriteLine("===========================================");
            //}
            //
            ////サーバー証明書を検証するためのコールバックメソッド
            //private static Boolean RemoteCertificateValidationCallback(Object sender,
            //    X509Certificate certificate,
            //    X509Chain chain,
            //    SslPolicyErrors sslPolicyErrors)
            //{
            //    //PrintCertificate(certificate);
            //    //Console.WriteLine(certificate.ToString(true));
            //
            //    if (sslPolicyErrors == SslPolicyErrors.None)
            //    {
            //        Console.WriteLine("サーバー証明書の検証に成功しました\n");
            //        return true;
            //    }
            //    else
            //    {
            //        //何かサーバー証明書検証エラーが発生している
            //
            //        //SslPolicyErrors列挙体には、Flags属性があるので、
            //        //エラーの原因が複数含まれているかもしれない。
            //        //そのため、&演算子で１つ１つエラーの原因を検出する。
            //        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) ==
            //            SslPolicyErrors.RemoteCertificateChainErrors)
            //        {
            //            Console.WriteLine("ChainStatusが、空でない配列を返しました");
            //        }
            //
            //        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) ==
            //            SslPolicyErrors.RemoteCertificateNameMismatch)
            //        {
            //            Console.WriteLine("証明書名が不一致です");
            //        }
            //
            //        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateNotAvailable) ==
            //            SslPolicyErrors.RemoteCertificateNotAvailable)
            //        {
            //            Console.WriteLine("証明書が利用できません");
            //        }
            //
            //
            //        foreach (var item in X509Certificates)
            //        {
            //            if (certificate.Issuer == item.Issuer)
            //            {
            //                Console.WriteLine("ok");
            //                PrintCertificate(certificate);
            //                PrintCertificate(item);
            //            }
            //        }
            //
            //        return true;
            //        //検証失敗とする
            //        return false;
            //    }
            //}
            //
            //public static X509Certificate SelectLocalCertificate(
            //    object sender,
            //    string targetHost,
            //    X509CertificateCollection localCertificates,
            //    X509Certificate remoteCertificate,
            //    string[] acceptableIssuers)
            //{
            //    /*
            //    foreach(var item in acceptableIssuers)
            //    {
            //        Console.WriteLine(item);
            //    }*/
            //    //foreach (var item in localCertificates)
            //    //{
            //    //    PrintCertificate(item);
            //    //}
            //    //PrintCertificate(remoteCertificate);
            //
            //
            //
            //    Console.WriteLine("Client is selecting a local certificate.");
            //    if (acceptableIssuers != null &&
            //        acceptableIssuers.Length > 0 &&
            //        X509Certificates != null &&
            //        X509Certificates.Count > 0)
            //    {
            //        // Use the first certificate that is from an acceptable issuer.
            //        foreach (X509Certificate certificate in X509Certificates)
            //        {
            //            string issuer = certificate.Issuer;
            //            if (Array.IndexOf(acceptableIssuers, issuer) != -1)
            //            {
            //                Console.WriteLine("selected");
            //                return certificate;
            //            }
            //        }
            //    }
            //
            //    var n = 1;
            //    if (X509Certificates != null &&
            //        X509Certificates.Count > n)
            //    {
            //        Console.WriteLine("use first cert");
            //        return X509Certificates[n];
            //    }
            //    Console.WriteLine("no cert");
            //
            //    return null;
            //}

            public static void SetTcpKeepAlive(Socket socket, uint keepaliveTime, uint keepaliveInterval)
            {
                /* the native structure
                struct tcp_keepalive {
                ULONG onoff;
                ULONG keepalivetime;
                ULONG keepaliveinterval;
                };
                */

                // marshal the equivalent of the native structure into a byte array
                // uint dummy = 0;
                byte[] inOptionValues = new byte[sizeof(UInt32) * 3];
                BitConverter.GetBytes((uint)keepaliveTime).CopyTo(inOptionValues, 0);
                BitConverter.GetBytes((uint)keepaliveTime).CopyTo(inOptionValues, sizeof(UInt32));
                BitConverter.GetBytes((uint)keepaliveInterval).CopyTo(inOptionValues, sizeof(UInt32) * 2);

                // write SIO_VALS to Socket IOControl
                socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
            }
        }
    }
}
