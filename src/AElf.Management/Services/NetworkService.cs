using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Management.Database;
using AElf.Management.Interfaces;
using AElf.Management.Models;
using AElf.Management.Request;
using Microsoft.Extensions.Options;

namespace AElf.Management.Services
{
    public class NetworkService : INetworkService
    {
        private readonly ManagementOptions _managementOptions;
        private readonly IInfluxDatabase _influxDatabase;

        public NetworkService(IOptionsSnapshot<ManagementOptions> options, IInfluxDatabase influxDatabase)
        {
            _managementOptions = options.Value;
            _influxDatabase = influxDatabase;
        }

        public async Task<PeerResult> GetPeers(string chainId)
        {
            var url = $"_managementOptions.ServiceUrls[chainId].RpcAddress/api/net/peers";
            var peers = await HttpRequestHelper.Get<PeerResult>(url);
            return peers;
        }
    }
}