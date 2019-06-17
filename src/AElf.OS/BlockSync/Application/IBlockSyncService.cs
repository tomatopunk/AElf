using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Blockchain.Application;
using AElf.OS.BlockSync.Infrastructure;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AElf.OS.BlockSync.Application
{
    public interface IBlockSyncService
    {
        Task SyncBlockAsync(Hash blockHash, long blockHeight, int batchRequestBlockCount, string suggestedPeerPubKey);
    }

    public class BlockSyncService : IBlockSyncService
    {
        private readonly IBlockchainService _blockchainService;
        private readonly IBlockFetchService _blockFetchService;
        private readonly IBlockDownloadService _blockDownloadService;
        private readonly IAnnouncementCacheProvider _announcementCacheProvider;
        private readonly IBlockSyncStateProvider _blockSyncStateProvider;
        private readonly ITaskQueueManager _taskQueueManager;

        public ILogger<BlockSyncService> Logger { get; set; }

        public BlockSyncService(IBlockchainService blockchainService,
            IBlockFetchService blockFetchService,
            IBlockDownloadService blockDownloadService,
            IAnnouncementCacheProvider announcementCacheProvider,
            IBlockSyncStateProvider blockSyncStateProvider,
            ITaskQueueManager taskQueueManager)
        {
            Logger = NullLogger<BlockSyncService>.Instance;

            _blockchainService = blockchainService;
            _blockFetchService = blockFetchService;
            _blockDownloadService = blockDownloadService;
            _announcementCacheProvider = announcementCacheProvider;
            _blockSyncStateProvider = blockSyncStateProvider;
            _taskQueueManager = taskQueueManager;
        }

        public async Task SyncBlockAsync(Hash blockHash, long blockHeight, int batchRequestBlockCount,
            string suggestedPeerPubKey)
        {
            Logger.LogTrace($"Receive announcement and sync block {{ hash: {blockHash}, height: {blockHeight} }} from {suggestedPeerPubKey}.");

            if (_blockSyncStateProvider.BlockSyncAnnouncementEnqueueTime != null && TimestampHelper.GetUtcNow() >
                _blockSyncStateProvider.BlockSyncAnnouncementEnqueueTime +
                TimestampHelper.DurationFromMilliseconds(BlockSyncConstants.BlockSyncAnnouncementAgeLimit))
            {
                Logger.LogWarning(
                    $"Block sync queue is too busy, enqueue timestamp: {_blockSyncStateProvider.BlockSyncAnnouncementEnqueueTime.ToDateTime()}");
                return;
            }

            if (_blockSyncStateProvider.BlockSyncAttachBlockEnqueueTime != null && TimestampHelper.GetUtcNow() >
                _blockSyncStateProvider.BlockSyncAttachBlockEnqueueTime +
                TimestampHelper.DurationFromMilliseconds(BlockSyncConstants.BlockSyncAttachBlockAgeLimit))
            {
                Logger.LogWarning(
                    $"Block sync attach queue is too busy, enqueue timestamp: {_blockSyncStateProvider.BlockSyncAttachBlockEnqueueTime.ToDateTime()}");
                return;
            }

            var chain = await _blockchainService.GetChainAsync();
            if (blockHeight <= chain.LastIrreversibleBlockHeight)
            {
                Logger.LogTrace($"Receive lower announcement {{ hash: {blockHash}, height: {blockHeight} }} " +
                                $"form {suggestedPeerPubKey}, ignore.");
                return;
            }

            EnqueueBlockSyncJob(blockHash, blockHeight, batchRequestBlockCount, suggestedPeerPubKey);
        }
        
        private void EnqueueBlockSyncJob(Hash blockHash, long blockHeight, int batchRequestBlockCount,
            string suggestedPeerPubKey)
        {
            var enqueueTimestamp = TimestampHelper.GetUtcNow();
            _taskQueueManager.Enqueue(async () =>
            {
                try
                {
                    _blockSyncStateProvider.BlockSyncAnnouncementEnqueueTime = enqueueTimestamp;
                    await ProcessBlockSyncAsync(blockHash, blockHeight,batchRequestBlockCount, suggestedPeerPubKey);
                }
                finally
                {
                    _blockSyncStateProvider.BlockSyncAnnouncementEnqueueTime = null;
                }
            }, BlockSyncConstants.BlockSyncQueueName);
        }  
        

        private async Task ProcessBlockSyncAsync(Hash blockHash, long blockHeight, int batchRequestBlockCount,
            string suggestedPeerPubKey)
        {
            if (_blockSyncStateProvider.BlockAttachAndExecutingEnqueueTime != null && TimestampHelper.GetUtcNow() >
                _blockSyncStateProvider.BlockAttachAndExecutingEnqueueTime +
                TimestampHelper.DurationFromMilliseconds(BlockSyncConstants.BlockSyncJobAgeLimit))
            {
                Logger.LogWarning(
                    $"Queue is too busy, block sync job enqueue timestamp: {_blockSyncStateProvider.BlockAttachAndExecutingEnqueueTime.ToDateTime()}");
                return;
            }

            if (_announcementCacheProvider.ContainsAnnouncement(blockHash, blockHeight))
            {
                Logger.LogWarning($"The block have been synchronized, block hash: {blockHash}");
                return;
            }
            
            Logger.LogDebug(
                $"Start block sync job, target height: {blockHash}, target block hash: {blockHeight}, peer: {suggestedPeerPubKey}");

            var chain = await _blockchainService.GetChainAsync();
            _announcementCacheProvider.ClearCache(chain.LastIrreversibleBlockHeight);
                        
            bool syncResult;
            if (blockHash != null && blockHeight < chain.BestChainHeight + 8)
            {
                syncResult = await _blockFetchService.FetchBlockAsync(blockHash, blockHeight, suggestedPeerPubKey);
            }
            else
            {
                var syncBlockCount = await _blockDownloadService.DownloadBlocksAsync(chain.BestChainHash,
                    chain.BestChainHeight, batchRequestBlockCount, suggestedPeerPubKey);

                if (syncBlockCount == 0 && blockHeight > chain.LongestChainHeight)
                {
                    Logger.LogDebug($"Resynchronize from lib, lib height: {chain.LastIrreversibleBlockHeight}.");
                    syncBlockCount = await _blockDownloadService.DownloadBlocksAsync(chain.LastIrreversibleBlockHash,
                        chain.LastIrreversibleBlockHeight, batchRequestBlockCount, suggestedPeerPubKey);
                }

                syncResult = syncBlockCount > 0;
            }

            if (syncResult)
            {
                _announcementCacheProvider.CacheAnnouncement(blockHash, blockHeight);
            }

            Logger.LogDebug($"Finishing block sync job, longest chain height: {chain.LongestChainHeight}");
        }
    }
}