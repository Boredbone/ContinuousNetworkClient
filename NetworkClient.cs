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
using Boredbone.Utility.Extensions;
using System.Collections.Concurrent;
using System.Reactive.Threading.Tasks;
using System.Reactive;

namespace Boredbone.ContinuousNetworkClient
{
    public class NetworkOptions
    {
        public IPAddress[] IpAddresss { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; }
    }

    public static class NetworkExtensions
    {
        public static bool Resembles(this EndPoint e1, EndPoint e2)
        {
            if (e1 is IPEndPoint ip1 && e2 is IPEndPoint ip2)
            {
                return ip1.Port == ip2.Port && ip1.Address.Equals(ip2.Address);
            }
            return e1 == e2;
        }
    }

    public class QueueingNetworkClient<TReceivePacket, TTransmitPacket> : IDisposable
        where TReceivePacket : IReceivePacket, new()
        where TTransmitPacket : ITransmitPacket
    {
        private CompositeDisposable disposables;


        public IObservable<TReceivePacket> Received => this.networkClient.Received;

        public IObservable<EndPoint> Connected => this.networkClient.Connected;

        public IServerCertificate ServerCertificate
        {
            get => this.networkClient.ServerCertificate;
            set => this.networkClient.ServerCertificate = value;
        }

        public int MaxRetryCountOfConnectingSameServer
        {
            get => this.networkClient.MaxRetryCountOfConnectingSameServer;
            set => this.networkClient.MaxRetryCountOfConnectingSameServer = value;
        }
        public TimeSpan ConnectionStableTimeThreshold
        {
            get => this.networkClient.ConnectionStableTimeThreshold;
            set => this.networkClient.ConnectionStableTimeThreshold = value;
        }
        public TimeSpan TransmitTimeout
        {
            get => this.networkClient.TransmitTimeout;
            set => this.networkClient.TransmitTimeout = value;
        }


        private PacketTransmissionQueue<TTransmitPacket> queue;
        public IPriorityQueue<TTransmitPacket> Queue => this.queue;

        private NetworkClient<TReceivePacket, TTransmitPacket> networkClient;

        private AsyncSubject<Unit> queueSubscription;

        public QueueingNetworkClient()
        {
            this.disposables = new CompositeDisposable();

            this.networkClient = new NetworkClient<TReceivePacket, TTransmitPacket>().AddTo(this.disposables);
            this.queue = new PacketTransmissionQueue<TTransmitPacket>().AddTo(this.disposables);

            this.queueSubscription = this.queue.Requested
                .SelectMany(x => this.networkClient.SendAsync(x).ToObservable())
                .GetAwaiter()
                .AddTo(this.disposables);
        }

        public void Enqueue(TTransmitPacket packet)
        {
            this.queue.Enqueue(packet);
        }

        public async Task DisconnectAsync(TimeSpan timeout)
        {
            this.queue.Close();
            await this.queueSubscription.Timeout(timeout);
            this.networkClient.Cancel();
        }

        public void Cancel() => this.networkClient.Cancel();

        public Task WorkAsync(NetworkOptions options) => this.networkClient.WorkAsync(options);


        public Task ResetConnectionAsync() => this.networkClient.ResetConnectionAsync();

        public Task RedirectAsync(NetworkOptions options) => this.networkClient.RedirectAsync(options);

        public void Dispose()
        {
            this.disposables.Dispose();
        }
    }

    public interface IPriorityQueue<T>
    {
        void Enqueue(T packet);
    }

    internal class PacketTransmissionQueue<T> : IDisposable, IPriorityQueue<T>
        where T : ITransmitPacket
    {
        private CompositeDisposable disposables;

        private Subject<T> requestedSubject;
        public IObservable<T> Requested => this.requestedSubject.AsObservable();

        public PacketTransmissionQueue()
        {
            this.disposables = new CompositeDisposable();

            this.requestedSubject = new Subject<T>().AddTo(this.disposables);


        }

        public void Enqueue(T packet)
        {
            if (this.requestedSubject.IsDisposed)
            {
                return;
            }
            this.requestedSubject.OnNext(packet);
        }

        public void Close()
        {
            if (this.requestedSubject.IsDisposed)
            {
                return;
            }
            this.requestedSubject.OnCompleted();
        }

        public void Dispose()
        {
            this.disposables.Dispose();
        }
    }

    public class NetworkClient<TReceivePacket, TTransmitPacket> : IDisposable
        where TReceivePacket : IReceivePacket, new()
        where TTransmitPacket : ITransmitPacket
    {
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
        private CompositeDisposable disposables;

        private Subject<TReceivePacket> ReceivedSubject { get; }
        public IObservable<TReceivePacket> Received => this.ReceivedSubject.AsObservable();

        private Subject<EndPoint> ConnectedSubject { get; }
        public IObservable<EndPoint> Connected => this.ConnectedSubject.AsObservable();

        public IServerCertificate ServerCertificate { get; set; } = null;


        private readonly AsyncLock asyncLock;

        private readonly CancellationTokenSource cancellationTokenSource;

        private NetworkClientWorker clientWorker;

        private NetworkOptions nextOptions = null;

        private int retryToConnectSameServerCount;
        public int MaxRetryCountOfConnectingSameServer { get; set; } = 10;
        public TimeSpan ConnectionStableTimeThreshold { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan TransmitTimeout { get; set; } = TimeSpan.FromSeconds(30);


        public NetworkClient()
        {
            this.asyncLock = new AsyncLock();
            this.cancellationTokenSource = new CancellationTokenSource();

            this.disposables = new CompositeDisposable();

            this.ReceivedSubject = new Subject<TReceivePacket>();

            this.clientWorker = null;

            this.ConnectedSubject = new Subject<EndPoint>().AddTo(this.disposables);


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
                logger.Info("start connection");
                await this.RedirectAsync(options);
                logger.Info($"connect to {this.nextOptions.HostName}:{this.nextOptions.Port}");
                this.retryToConnectSameServerCount++;
                await this.WorkMainAsync();
            }
            catch (Exception e)
            {
                logger.Error(e);
                throw;
            }
        }

        private async Task WorkMainAsync()
        {

            var cancellationToken = this.cancellationTokenSource.Token;


            while (!cancellationToken.IsCancellationRequested)
            {
                logger.Info($"try to connect server. {this.retryToConnectSameServerCount}th try");
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
                    logger.Error("client excep\nOperationCanceledException :" + e.Message);
                    if (e.InnerException != null)
                    {
                        logger.Error("client excep inner\n" + e.InnerException.ToString());
                    }
                    break;
                }
                catch (Exception e)
                {
                    logger.Error("client excep\n" + e.ToString());
                    if (e.InnerException != null)
                    {
                        logger.Error("client excep inner\n" + e.InnerException.ToString());
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

                if (this.retryToConnectSameServerCount > 1)
                {
                    await Task.Delay(5000);
                }
            }
        }

        private bool CheckConnectionRetryCount(in DateTimeOffset startTime)
        {
            var now = DateTimeOffset.UtcNow;

            if (now - startTime > this.ConnectionStableTimeThreshold)
            {
                this.retryToConnectSameServerCount = 1;
                return true;
            }

            this.retryToConnectSameServerCount++;
            return (this.retryToConnectSameServerCount <= this.MaxRetryCountOfConnectingSameServer);
        }

        private async Task<NetworkClientWorker> GetWorkerAsync()
        {
            using (var locking = await this.asyncLock.LockAsync().ConfigureAwait(false))
            {
                return this.clientWorker;
            }
        }


        public async Task ResetConnectionAsync()
        {
            logger.Warn("reset connection");
            this.retryToConnectSameServerCount = 0;
            await this.CloseWorkerAsync(null);
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
                this.clientWorker.Connected.Subscribe(this.ConnectedSubject).AddTo(this.disposables);
            }
        }

        public async Task SendAsync(TTransmitPacket packet)
        {
            this.cancellationTokenSource.Token.ThrowIfCancellationRequested();
            var worker = await this.GetWorkerAsync().ConfigureAwait(false);
            if (worker != null)
            {
                if (this.TransmitTimeout.TotalMilliseconds >= 1)
                {
                    await worker.SendAsync(packet, this.TransmitTimeout, this.cancellationTokenSource.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await worker.SendAsync(packet, this.cancellationTokenSource.Token)
                        .ConfigureAwait(false);
                }
                logger.Info("completed");
            }
            else
            {
                logger.Error("There is no connction. request was ignored");
            }
        }

        private static async Task SendAnywayAsync
            (NetworkClientWorker worker, TTransmitPacket packet, TimeSpan timeout)
        {
            if (timeout.TotalMilliseconds < 1)
            {
                throw new ArgumentException("timeout is required");
            }
            //var worker = await this.GetWorkerAsync().ConfigureAwait(false);
            if (worker != null)
            {
                await worker.SendAnywayAsync(packet, timeout).ConfigureAwait(false);
                logger.Info("completed to send");
            }
            else
            {
                logger.Error("There is no connction. request was ignored");
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

            private Subject<EndPoint> ConnectedSubject { get; }
            public IObservable<EndPoint> Connected => this.ConnectedSubject.AsObservable();

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

                this.ConnectedSubject = new Subject<EndPoint>().AddTo(this.disposables);

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
                                await client.ConnectAsync(this.options.IpAddresss, this.options.Port)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                await client.ConnectAsync(this.options.HostName, this.options.Port)
                                    .ConfigureAwait(false);
                            }

                            logger.Info($"connected to {client.Client.RemoteEndPoint} " +
                                $"from {client.Client.LocalEndPoint}");



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
                                //var sslStream = new SslStream(client.GetStream(), false);
                                //var sslStream =
                                //    new SslStream(client.GetStream(), false,
                                //    RemoteCertificateValidationCallback, SelectLocalCertificate,
                                //    EncryptionPolicy.RequireEncryption);
                                var sslStream = new SslStream
                                    (client.GetStream(), false, ValidateRemoteCertificate);

                                await sslStream.AuthenticateAsClientAsync(this.serverCertificate.ServerName,
                                    this.serverCertificate.CertificateCollection,
                                    SslProtocols.Ssl2 | SslProtocols.Ssl3
                                    | SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
                                    false)
                                    .ConfigureAwait(false);

                                this.stream = sslStream;

                                if (sslStream.LocalCertificate == null)
                                {
                                    //logger.Warn("local cert null");
                                }
                                else
                                {
                                    logger.Info(sslStream.LocalCertificate);
                                }

                            }
                            else
                            {
                                this.stream = client.GetStream();
                            }
                        }

                        logger.Info("connection start, stream " + ((this.stream == null) ? "null" : "active")
                            + ", client " + ((this.client.Connected) ? "connected" : "disconnected"));

                        this.ConnectedSubject.OnNext(this.client.Client.RemoteEndPoint);

                        while (this.client.Connected)
                        {
                            await this.ReadAsync(cancellationToken).ConfigureAwait(false);

                            //TODO test
                            await Task.Delay(100).ConfigureAwait(false);

                            var socket = this.client?.Client;
                            if (socket == null || (socket.Poll(100_000, SelectMode.SelectRead) && (socket.Available == 0)))
                            {
                                break;
                            }
                        }
                    }

                    logger.Error("socket disconnection is detected");
                }
                catch (OperationCanceledException e)
                {
                    logger.Error("client worker excep\nOperationCanceledException :" + e.Message);
                    if (e.InnerException != null)
                    {
                        logger.Error("client worker excep inner\n" + e.InnerException.ToString());
                    }
                    logger.Error("parent" + parentCancellationToken.IsCancellationRequested);
                    logger.Error("child" + this.cancellationTokenSource.Token.IsCancellationRequested);
                    throw;

                }
                catch (Exception e)
                {
                    logger.Error("client worker excep\n" + e.ToString());
                    if (e.InnerException != null)
                    {
                        logger.Error("client worker excep inner\n" + e.InnerException.ToString());
                    }
                    logger.Error("parent" + parentCancellationToken.IsCancellationRequested);
                    logger.Error("child" + this.cancellationTokenSource.Token.IsCancellationRequested);
                    throw;
                }
            }

            public Task SendAsync
                (TTransmitPacket packet, TimeSpan timeout, CancellationToken parentCancellationToken)
            {
                using (var timeoutCancellation = new CancellationTokenSource(timeout))
                using (var linkedCancellationTokenSource = CancellationTokenSource
                    .CreateLinkedTokenSource(this.cancellationTokenSource.Token,
                    parentCancellationToken, timeoutCancellation.Token))
                {
                    return this.streamHelper.WriteAsync(this.stream, packet, linkedCancellationTokenSource.Token);
                }
            }

            public Task SendAsync
                (TTransmitPacket packet, CancellationToken parentCancellationToken)
            {
                using (var linkedCancellationTokenSource = CancellationTokenSource
                    .CreateLinkedTokenSource(this.cancellationTokenSource.Token, parentCancellationToken))
                {
                    return this.streamHelper.WriteAsync(this.stream, packet, linkedCancellationTokenSource.Token);
                }
            }

            public Task SendAnywayAsync(TTransmitPacket packet, TimeSpan timeout)
            {
                using (var timeoutCancellation = new CancellationTokenSource(timeout))
                {
                    return this.streamHelper.WriteAsync(this.stream, packet, timeoutCancellation.Token);
                }
            }


            private async Task ReadAsync(CancellationToken cancellationToken)
            {

                var (isSucceeded, packet) = await this.streamHelper.ReadAsync(this.stream, cancellationToken)
                    .ConfigureAwait(false);

                if (isSucceeded)
                {
                    this.ReceivedSubject.OnNext(packet);
                }
                else
                {
                    logger.Error("receive failed");
                }
            }

            public void Dispose()
            {
                this.disposables.Dispose();
            }


            private bool ValidateRemoteCertificate
                (Object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                {
                    logger.Info("server certificate is effective");

                    if (this.serverCertificate.CertificateHashs == null)
                    {
                        return true;
                    }

                    foreach (var item in chain.ChainElements)
                    {
                        //logger.Info("chain cert = " + item.Certificate.GetCertHashString());
                        if (this.serverCertificate.CertificateHashs
                            .Contains(item.Certificate.GetCertHashString()))
                        {
                            return true;
                        }
                    }

                    logger.Error("unknown certificate");
                    return false;
                }
                else
                {
                    logger.Error($"certificaten validate error {sslPolicyErrors}");
                    return false;
                }
            }

            //public static void SetTcpKeepAlive(Socket socket, uint keepaliveTime, uint keepaliveInterval)
            //{
            //    /* the native structure
            //    struct tcp_keepalive {
            //    ULONG onoff;
            //    ULONG keepalivetime;
            //    ULONG keepaliveinterval;
            //    };
            //    */
            //
            //    // marshal the equivalent of the native structure into a byte array
            //    // uint dummy = 0;
            //    byte[] inOptionValues = new byte[sizeof(UInt32) * 3];
            //    BitConverter.GetBytes((uint)keepaliveTime).CopyTo(inOptionValues, 0);
            //    BitConverter.GetBytes((uint)keepaliveTime).CopyTo(inOptionValues, sizeof(UInt32));
            //    BitConverter.GetBytes((uint)keepaliveInterval).CopyTo(inOptionValues, sizeof(UInt32) * 2);
            //
            //    // write SIO_VALS to Socket IOControl
            //    socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
            //}
        }
    }
}
