using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Types;

namespace AElf.OS.Network.Infrastructure
{
    public interface IPeer
    {
        bool IsBest { get; set; }
        Hash CurrentBlockHash { get; }
        long CurrentBlockHeight { get; }
        
        string PeerIpAddress { get; }
        string PubKey { get; }
        int ProtocolVersion { get; }
        long ConnectionTime { get; }
        bool Inbound { get; }
        long StartHeight { get; }
        
        IReadOnlyDictionary<long, Hash> RecentBlockHeightAndHashMappings { get; }
        
        IReadOnlyDictionary<long, Hash> PreLibBlockHeightAndHashMappings { get; }

        Dictionary<string, List<RequestMetric>> GetRequestMetrics();

        void HandlerRemoteAnnounce(PeerNewBlockAnnouncement peerNewBlockAnnouncement);
        
        void HandlerRemotePreLibAnnounce(PeerPreLibAnnouncement peerPreLibAnnouncement);

        Task SendDisconnectAsync();
        Task StopAsync();

        Task AnnounceAsync(PeerNewBlockAnnouncement an);
        Task PreLibAnnounceAsync(PeerPreLibAnnouncement peerPreLibAnnouncement);
        Task SendTransactionAsync(Transaction tx);
        Task<BlockWithTransactions> RequestBlockAsync(Hash hash);
        Task<List<BlockWithTransactions>> GetBlocksAsync(Hash previousHash, int count);
    }
}