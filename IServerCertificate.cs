using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Boredbone.ContinuousNetworkClient
{
    public interface IServerCertificate
    {
        X509CertificateCollection CertificateCollection { get; }
        HashSet<string> CertificateHashs { get; }

        string ServerName { get; }
    }
}
