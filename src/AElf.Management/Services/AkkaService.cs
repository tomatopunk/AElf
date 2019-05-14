using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Management.Interfaces;
using AElf.Management.Models;
using AElf.Management.Request;
using Microsoft.Extensions.Options;

namespace AElf.Management.Services
{
    public class AkkaService : IAkkaService
    {
        private readonly ManagementOptions _managementOptions;

        public AkkaService(IOptionsSnapshot<ManagementOptions> options)
        {
            _managementOptions = options.Value;
        }

        public async Task<List<ActorStateResult>> GetState(string chainId)
        {
            // Change to get method
            var url = $"{_managementOptions.ServiceUrls[chainId].MonitorRpcAddress}/api/akka/state";
            var state = await HttpRequestHelper.Get<JsonRpcResult<List<ActorStateResult>>>(url);
            return state.Result;
        }
    }
}