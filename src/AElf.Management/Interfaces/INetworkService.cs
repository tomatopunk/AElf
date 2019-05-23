using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Management.Models;

namespace AElf.Management.Interfaces
{
    public interface INetworkService
    {
        Task<PeerResult> GetPeers(string chainId);
    }
}