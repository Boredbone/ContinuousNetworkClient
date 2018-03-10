using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Boredbone.ContinuousNetworkClient.Packet
{
    public interface IReceivePacket
    {
        int HeaderLength { get; }
        int DataLengthWithoutHeader { get; }
        void SetHeader(byte[] header);
        void SetData(byte[] data);
    }
}
