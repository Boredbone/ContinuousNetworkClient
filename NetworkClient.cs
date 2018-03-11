﻿using System;
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

namespace Boredbone.ContinuousNetworkClient
{
    public class DisconnectingEventArgs<TTransmitPacket>
        where TTransmitPacket : ITransmitPacket
    {
        public ConcurrentQueue<Tuple<TTransmitPacket, TimeSpan>> FinallyTransmitPackets { get; }
        = new ConcurrentQueue<Tuple<TTransmitPacket, TimeSpan>>();
    }

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

        private Subject<DisconnectingEventArgs<TTransmitPacket>> DisconnectingSubject { get; }
        public IObservable<DisconnectingEventArgs<TTransmitPacket>> Disconnecting
            => this.DisconnectingSubject.AsObservable();

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
            this.DisconnectingSubject = new Subject<DisconnectingEventArgs<TTransmitPacket>>();

            this.clientWorker = null;


            Disposable.Create(() =>
            {
                this.Cancel();

                //this.clientWorker?.Dispose();
                this.DisconnectWorker(this.clientWorker, true);

                this.ReceivedSubject.Dispose();
                //this.DisconnectingSubject.Dispose();
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
            using (var locking = await this.asyncLock.LockAsync().ConfigureAwait(false))
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
            this.DisconnectWorker(currentWorker, false);

            //currentWorker?.Dispose();
        }


        private void DisconnectWorker(NetworkClientWorker worker, bool disposeSubject)
        {
            this.DisconnectWorkerAsync(worker, disposeSubject).FireAndForget(e =>
            {
            });
        }
        private async Task DisconnectWorkerAsync(NetworkClientWorker worker, bool disposeSubject)
        {
            try
            {;
                var args = new DisconnectingEventArgs<TTransmitPacket>();
                this.DisconnectingSubject.OnNext(args);
                if (worker != null)
                {
                    Console.WriteLine("disconnecting");
                    foreach (var item in args.FinallyTransmitPackets)
                    {
                        await SendAnywayAsync(worker, item.Item1, item.Item2).ConfigureAwait(false);
                        //Console.WriteLine("send disconnecting packet");
                    }
                    Console.WriteLine("disconnected");
                }
            }
            catch (AggregateException e)
            {
                if (e != null)
                {
                    Console.WriteLine(e);
                    if (e.InnerExceptions != null)
                    {
                        foreach (var item in e.InnerExceptions)
                        {
                            Console.WriteLine(item);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (e != null)
                {
                    Console.WriteLine(e);
                }
            }
            finally
            {
                worker?.Dispose();
                if (disposeSubject)
                {
                    this.DisconnectingSubject.Dispose();
                }
            }
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
                Console.WriteLine("completed");
            }
            else
            {
                Console.WriteLine("There is no connction. request was ignored");
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
                                await client.ConnectAsync(this.options.IpAddresss, this.options.Port)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                await client.ConnectAsync(this.options.HostName, this.options.Port)
                                    .ConfigureAwait(false);
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
                                    false)
                                    .ConfigureAwait(false);

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

            public async Task SendAsync
                (TTransmitPacket packet, TimeSpan timeout, CancellationToken parentCancellationToken)
            {
                using (var timeoutCancellation = new CancellationTokenSource(timeout))
                using (var linkedCancellationTokenSource = CancellationTokenSource
                    .CreateLinkedTokenSource(this.cancellationTokenSource.Token,
                    parentCancellationToken, timeoutCancellation.Token))
                {
                    await this.streamHelper.WriteAsync(this.stream, packet, linkedCancellationTokenSource.Token)
                        .ConfigureAwait(false);
                }
            }

            public async Task SendAsync
                (TTransmitPacket packet, CancellationToken parentCancellationToken)
            {
                using (var linkedCancellationTokenSource = CancellationTokenSource
                    .CreateLinkedTokenSource(this.cancellationTokenSource.Token, parentCancellationToken))
                {
                    await this.streamHelper.WriteAsync(this.stream, packet, linkedCancellationTokenSource.Token)
                        .ConfigureAwait(false);
                }
            }

            public async Task SendAnywayAsync(TTransmitPacket packet, TimeSpan timeout)
            {
                using (var timeoutCancellation = new CancellationTokenSource(timeout))
                {
                    await this.streamHelper.WriteAsync(this.stream, packet, timeoutCancellation.Token)
                        .ConfigureAwait(false);
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
                    Console.WriteLine("receive failed");
                }
            }

            public void Dispose()
            {
                this.disposables.Dispose();
            }



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
