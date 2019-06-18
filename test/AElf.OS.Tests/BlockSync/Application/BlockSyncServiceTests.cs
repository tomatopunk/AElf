using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Blockchain.Application;
using AElf.OS.BlockSync.Infrastructure;
using AElf.OS.Network.Application;
using AElf.Sdk.CSharp;
using AElf.Types;
using Shouldly;
using Xunit;

namespace AElf.OS.BlockSync.Application
{
    public class BlockSyncServiceTests : BlockSyncTestBase
    {
        private readonly IBlockSyncService _blockSyncService;
        private readonly IBlockchainService _blockchainService;
        private readonly INetworkService _networkService;
        private readonly IAnnouncementCacheProvider _announcementCacheProvider;
        private readonly IBlockSyncStateProvider _blockSyncStateProvider;
        private readonly OSTestHelper _osTestHelper;
        
        public BlockSyncServiceTests()
        {
            _blockSyncService = GetRequiredService<IBlockSyncService>();
            _blockchainService = GetRequiredService<IBlockchainService>();
            _networkService = GetRequiredService<INetworkService>();
            _announcementCacheProvider = GetRequiredService<IAnnouncementCacheProvider>();
            _blockSyncStateProvider = GetRequiredService<IBlockSyncStateProvider>();
            _osTestHelper = GetRequiredService<OSTestHelper>();
        }
        
        [Fact]
        public async Task SyncBlock_Success()
        {
            var chain = await _blockchainService.GetChainAsync();
            var peerBlocks = await _networkService.GetBlocksAsync(chain.BestChainHash, 20);

            var block = peerBlocks[0];

            {
                // Sync one block to best chain
                // BestChainHeight: 12
                await _blockSyncService.SyncBlockAsync(block.GetHash(), block.Header.Height, 10, null);
                chain = await _blockchainService.GetChainAsync();
                chain.BestChainHeight.ShouldBe(12);
                chain.BestChainHash.ShouldBe(peerBlocks[0].GetHash());
            }

            {
                // Handle the same announcement again
                // BestChainHeight: 12
                await _blockSyncService.SyncBlockAsync(block.GetHash(), block.Header.Height, 10, null);
                chain = await _blockchainService.GetChainAsync();
                chain.BestChainHash.ShouldBe(peerBlocks[0].GetHash());
                chain.BestChainHeight.ShouldBe(12);
            }

            Hash forkedBlockHash;
            {
                // Mined one block, and fork
                _osTestHelper.MinedOneBlock();
                chain = await _blockchainService.GetChainAsync();
                chain.BestChainHeight.ShouldBe(13);
                forkedBlockHash = chain.BestChainHash;
            }

            {
                // Receive a higher fork block, sync from the lib
                // BestChainHeight: 21
                block = peerBlocks[9];

                await _blockSyncService.SyncBlockAsync(block.GetHash(), block.Header.Height, 10, null);

                chain = await _blockchainService.GetChainAsync();
                chain.BestChainHash.ShouldBe(peerBlocks.Last().GetHash());
                chain.BestChainHeight.ShouldBe(21);

                var block13 = await _blockchainService.GetBlockByHeightInBestChainBranchAsync(13);
                block13.GetHash().ShouldNotBe(forkedBlockHash);
            }
        }
        
        [Fact]
        public async Task SyncBlock_BlockSyncQueueIsBusy()
        {
            var chain = await _blockchainService.GetChainAsync();
            var bestChainHash = chain.BestChainHash;
            var bestChainHeight = chain.BestChainHeight;
            
            var peerBlocks = await _networkService.GetBlocksAsync(chain.BestChainHash, 20);

            var block = peerBlocks.First();
            _blockSyncStateProvider.BlockSyncAnnouncementEnqueueTime = TimestampHelper.GetUtcNow().AddSeconds(-5);
            
            await _blockSyncService.SyncBlockAsync(block.GetHash(), block.Header.Height, 10, null);
            
            chain = await _blockchainService.GetChainAsync();
            chain.BestChainHash.ShouldBe(bestChainHash);
            chain.BestChainHeight.ShouldBe(bestChainHeight);
        }
        
        [Fact]
        public async Task SyncBlock_BlockSyncAttachQueueIsBusy()
        {
            var chain = await _blockchainService.GetChainAsync();
            var bestChainHash = chain.BestChainHash;
            var bestChainHeight = chain.BestChainHeight;
            
            var peerBlocks = await _networkService.GetBlocksAsync(chain.BestChainHash, 20);

            var block = peerBlocks.First();
            _blockSyncStateProvider.BlockSyncAttachBlockEnqueueTime = TimestampHelper.GetUtcNow().AddSeconds(-3);
            
            await _blockSyncService.SyncBlockAsync(block.GetHash(), block.Header.Height, 10, null);
            
            chain = await _blockchainService.GetChainAsync();
            chain.BestChainHash.ShouldBe(bestChainHash);
            chain.BestChainHeight.ShouldBe(bestChainHeight);
        }

        [Fact]
        public async Task SyncBlock_LessThenFetchLimit_Success()
        {
            var peerBlock = await _networkService.GetBlockByHashAsync(Hash.FromString("PeerBlock"));

            var block = await _blockchainService.GetBlockByHashAsync(peerBlock.GetHash());
            block.ShouldBeNull();

            await _blockSyncService.SyncBlockAsync(peerBlock.GetHash(), peerBlock.Height, 5, null);

            block = await _blockchainService.GetBlockByHashAsync(peerBlock.GetHash());
            block.GetHash().ShouldBe(peerBlock.GetHash());

            var chain = await _blockchainService.GetChainAsync();
            chain.BestChainHash.ShouldBe(peerBlock.GetHash());
            chain.BestChainHeight.ShouldBe(peerBlock.Height);
             
            _announcementCacheProvider.ContainsAnnouncement(peerBlock.GetHash(),peerBlock.Height).ShouldBeTrue();
        }
        
        [Fact]
        public async Task SyncBlock_LessThenFetchLimit_FetchReturnFalse()
        {
            var chain = await _blockchainService.GetChainAsync();
            var bestChainHash = chain.BestChainHash;
            var bestChainHeight = chain.BestChainHeight;
            
            await _blockSyncService.SyncBlockAsync(Hash.Empty, 15, 5, null);

            chain = await _blockchainService.GetChainAsync();
            chain.BestChainHash.ShouldBe(bestChainHash);
            chain.BestChainHeight.ShouldBe(bestChainHeight);
             
            _announcementCacheProvider.ContainsAnnouncement(Hash.Empty,15).ShouldBeFalse();
        }

        [Fact]
        public async Task SyncBlock_MoreThenFetchLimit_Success()
        {
            var chain = await _blockchainService.GetChainAsync();

            var peerBlockHash = Hash.FromString("PeerBlock");
            var peerBlockHeight = chain.BestChainHeight + 8;

            await _blockSyncService.SyncBlockAsync(peerBlockHash, peerBlockHeight, 5, null);
            
            chain = await _blockchainService.GetChainAsync();
            chain.BestChainHeight.ShouldBe(21);

            _announcementCacheProvider.ContainsAnnouncement(peerBlockHash, peerBlockHeight).ShouldBeTrue();
        }
        
        [Fact]
        public async Task SyncBlock_QueueIsBusy()
        {
            _blockSyncStateProvider.BlockAttachAndExecutingEnqueueTime = TimestampHelper.GetUtcNow().AddMilliseconds(-600);
            
            var peerBlock = await _networkService.GetBlockByHashAsync(Hash.FromString("PeerBlock"));

            await _blockSyncService.SyncBlockAsync(peerBlock.GetHash(), peerBlock.Height, 5, null);
            
            var block = await _blockchainService.GetBlockByHashAsync(peerBlock.GetHash());
            block.ShouldBeNull();

            _announcementCacheProvider.ContainsAnnouncement(peerBlock.GetHash(),peerBlock.Height).ShouldBeFalse();
        }
        
        [Fact]
        public async Task SyncBlock_AlreadySynchronized()
        {
            var peerBlock = await _networkService.GetBlockByHashAsync(Hash.FromString("PeerBlock"));
            _announcementCacheProvider.CacheAnnouncement(peerBlock.GetHash(), peerBlock.Height);

            await _blockSyncService.SyncBlockAsync(peerBlock.GetHash(), peerBlock.Height, 5, null);

            var block = await _blockchainService.GetBlockByHashAsync(peerBlock.GetHash());
            block.ShouldBeNull();
        }
    }
}