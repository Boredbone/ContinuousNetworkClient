using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Boredbone.ContinuousNetworkClient.Packet
{
    public interface ITransmitPacket
    {
        int Length { get; }
        //IEnumerable<byte[]> GetTransmitData();
        Task WriteToStreamAsync(Stream destination, CancellationToken cancellationToken);
    }
}
