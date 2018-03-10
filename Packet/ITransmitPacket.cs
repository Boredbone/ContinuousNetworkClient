using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Boredbone.ContinuousNetworkClient.Packet
{
    public interface ITransmitPacket
    {
        int Length { get; }
        IEnumerable<byte[]> GetTransmitData();
    }
}
