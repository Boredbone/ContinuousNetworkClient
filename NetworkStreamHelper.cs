using Boredbone.ContinuousNetworkClient.Packet;
using Boredbone.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Boredbone.ContinuousNetworkClient
{

    public class NetworkStreamHelper<TReceivePacket, TTransmitPacket> : IDisposable
        where TReceivePacket : IReceivePacket, new()
        where TTransmitPacket : ITransmitPacket
    {
        private readonly SteppingStreamReader receiveStreamReader;
        private readonly AsyncLock readLock;
        private readonly AsyncLock writeLock;
        private readonly CompositeDisposable disposables;

        public NetworkStreamHelper()
        {
            this.disposables = new CompositeDisposable();
            this.receiveStreamReader = new SteppingStreamReader();
            this.readLock = new AsyncLock();
            this.writeLock = new AsyncLock();

        }

        private async Task WriteWithCancellationAsync
            (Stream stream, byte[] data, CancellationToken cancellationToken)
        {
            var task = stream.WriteAsync(data, 0, data.Length, cancellationToken);
            task.Wait(cancellationToken);
            await task;
        }

        public async Task WriteAsync(Stream stream, TTransmitPacket packet, CancellationToken cancellationToken)
        {
            using (var ms = new MemoryStream(packet.Length))
            {
                foreach (var item in packet.GetTransmitData())
                {
                    ms.Write(item, 0, item.Length);
                }
                ms.Position = 0;
                using (var locking = await this.writeLock.LockAsync().ConfigureAwait(false))
                {
                    var task = ms.CopyToAsync(stream, packet.Length, cancellationToken)
                        .ContinueWith(_ => stream.FlushAsync());
                    task.Wait(cancellationToken);
                    await task;
                }
            }
            return;
            using (var locking = await this.writeLock.LockAsync().ConfigureAwait(false))
            {
                foreach (var item in packet.GetTransmitData())
                {
                    stream.Write(item, 0, item.Length);
                }
                cancellationToken.ThrowIfCancellationRequested();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                /*
                using (var bw = new BinaryWriter(stream, Encoding.Default, true))
                {
                    Task task = null;

                    foreach (var data in packet.GetTransmitData())
                    {
                        if (task == null)
                        {
                            task=bw.Write
                            task = stream.WriteAsync(data, 0, data.Length, cancellationToken);
                        }
                        else
                        {
                            task = task
                                .ContinueWith(_ => stream.WriteAsync(data, 0, data.Length, cancellationToken));
                        }
                    }
                    if (task != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        task.Wait(cancellationToken);
                        await task;
                    }
                }*/


                //var item = packet.GetTransmitData().SelectMany(x => x).ToArray();
                ////foreach (var item in packet.GetTransmitData())
                //{
                //    cancellationToken.ThrowIfCancellationRequested();
                //    await WriteWithCancellationAsync(stream, item, cancellationToken).ConfigureAwait(false);
                //}

                //var length = BitConverter.GetBytes(data.Length);
                //
                ////Console.WriteLine($"send [{length}({length.Length})] <({data.Length})>");
                //
                //await WriteWithCancellationAsync(stream, length, cancellationToken).ConfigureAwait(false);
                //cancellationToken.ThrowIfCancellationRequested();
                //await WriteWithCancellationAsync(stream, data, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<(bool isSucceeded, TReceivePacket packet)> ReadAsync
            (Stream stream, CancellationToken cancellationToken)
        {
            using (var locking = await this.readLock.LockAsync().ConfigureAwait(false))
            {
                var resultPacket = new TReceivePacket();

                var headerLength = resultPacket.HeaderLength;
                var header = await this.receiveStreamReader
                    .ReadArrayAsync(stream, headerLength, cancellationToken)
                    .ConfigureAwait(false);

                if (header == null || header.Length != headerLength)
                {
                    return (false, default);
                }

                resultPacket.SetHeader(header);

                var dataLength = resultPacket.DataLengthWithoutHeader;

                //if (length > (512 * 1024 * 1024))
                //{
                //    //Console.WriteLine("long length");
                //    return (false, default, 0);
                //}
                //if (length < 0)
                //{
                //    //Console.WriteLine("long length");
                //    return (false, default, 0);
                //}
                //Console.WriteLine($"receive length [{length}({lengthDataLength})]");

                var resultData = await this.receiveStreamReader
                    .ReadArrayAsync(stream, dataLength, cancellationToken)
                    .ConfigureAwait(false);

                if (resultData == null || resultData.Length != dataLength)
                {
                    return (false, default);
                }

                resultPacket.SetData(resultData);

                return (true, resultPacket);

                //this.ReceivedValue.Value = enc.GetString(result_byte);

                //ReadValue();
            }
        }

        public void Dispose()
        {
            this.disposables.Dispose();
        }


        private class SteppingStreamReader
        {
            private readonly byte[] receiveBuffer = new byte[256];

            private int index = 0;
            private int length = 0;

            public async ValueTask<(byte data, bool isSucceeded)> ReadNextAsync
                (Stream stream, CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested
                    && (this.length == 0 || this.index >= this.length))
                {
                    var readTask = stream.ReadAsync
                        (this.receiveBuffer, 0, this.receiveBuffer.Length, cancellationToken);

                    readTask.Wait(cancellationToken);
                    var receivedLength = await readTask;

                    //var receivedLength = await stream.ReadAsync(this.receiveBuffer, 0, this.receiveBuffer.Length);
                    Console.WriteLine($"reader received {receivedLength}");
                    if (receivedLength <= 0)
                    {
                        //continue;
                        return (default(byte), false);
                    }
                    this.length = receivedLength;
                    this.index = 0;
                }
                return (this.receiveBuffer[index++], true);
            }


            public async ValueTask<byte[]> ReadArrayAsync
                (Stream stream, int length, CancellationToken cancellationToken)
            {
                var resultBuffer = new byte[length];

                for (int i = 0; i < resultBuffer.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (data, isSucceeded) = await this.ReadNextAsync(stream, cancellationToken);

                    if (!isSucceeded)
                    {
                        return null;
                    }
                    resultBuffer[i] = data;
                }
                return resultBuffer;
            }

        }
    }
}
