using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Management.Models;

namespace AElf.Management.Interfaces
{
    public interface INodeService
    {
        Task<List<NodeStateHistory>> GetHistoryStateAsync(string chainId);

        Task RecordBlockInfoAsync(string chainId);
        
        Task RecordGetCurrentChainStatusAsync(string chainId);

        Task RecordTaskQueueStatusAsync(string chainId);
    }
}