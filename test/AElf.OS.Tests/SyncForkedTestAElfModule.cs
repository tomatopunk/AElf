using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.SmartContractExecution.Application;
using AElf.Modularity;
using AElf.OS.Network;
using AElf.OS.Network.Application;
using AElf.OS.Network.Infrastructure;
using AElf.Types;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Threading;

namespace AElf.OS
{
    [DependsOn(typeof(OSTestAElfModule))]
    public class SyncForkedTestAElfModule : AElfModule
    {
        private readonly List<Block> _blockList = new List<Block>();
        
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            context.Services.AddSingleton<INetworkService, NetworkService>();

            context.Services.AddSingleton<IPeerPool>(o =>
            {
                Mock<IPeer> peerMock = new Mock<IPeer>();

                peerMock.Setup(p => p.CurrentBlockHeight).Returns(15);
                peerMock.Setup(p => p.PubKey).Returns("PubKey");
                peerMock.Setup(p => p.GetBlocksAsync(It.IsAny<Hash>(), It.IsAny<int>()))
                    .Returns<Hash, int>((hash, cnt) => 
                    {
                        var requested = _blockList.FirstOrDefault(b => b.GetHash() == hash);
                        
                        if (requested == null)
                            return Task.FromResult(new List<BlockWithTransactions>());
                        
                        var selection = _blockList.Where(b => b.Height > requested.Height).Select(b => new BlockWithTransactions {Header = b.Header}).OrderBy(b => b.Height).Take(cnt).ToList();
                        return Task.FromResult(selection);
                    });

                Mock<IPeerPool> peerPoolMock = new Mock<IPeerPool>();
                peerPoolMock.Setup(p => p.FindPeerByAddress(It.IsAny<string>())).Returns<string>(adr => peerMock.Object);
                peerPoolMock.Setup(p => p.GetPeers(It.IsAny<bool>())).Returns(new List<IPeer> { peerMock.Object });

                return peerPoolMock.Object;
            });
        }

        public override void OnApplicationInitialization(ApplicationInitializationContext context)
        {
            base.OnApplicationInitialization(context);
            
            var blockchainService = context.ServiceProvider.GetRequiredService<IBlockchainService>();
            var genService = context.ServiceProvider.GetRequiredService<IBlockGenerationService>();
            var exec = context.ServiceProvider.GetRequiredService<IBlockExecutingService>();
            var osTestHelper = context.ServiceProvider.GetService<OSTestHelper>();
            
            var chain = AsyncHelper.RunSync(() => blockchainService.GetChainAsync());
            var previousBlockHash = osTestHelper.ForkBranchBlockList.Last().GetHash();
            long height = osTestHelper.ForkBranchBlockList.Last().Height;

            _blockList.Add(osTestHelper.BestBranchBlockList[4]);
            _blockList.AddRange(osTestHelper.ForkBranchBlockList);
            var forkBranchHeight = height;

            for (var i = forkBranchHeight; i < forkBranchHeight + 5; i++)
            {
                var newBlock = AsyncHelper.RunSync(() => genService.GenerateBlockBeforeExecutionAsync(new GenerateBlockDto
                {
                    PreviousBlockHash = previousBlockHash,
                    PreviousBlockHeight = height,
                    BlockTime = TimestampHelper.GetUtcNow()
                }));

                // no choice need to execute the block to finalize it.
                var newNewBlock = AsyncHelper.RunSync(() => exec.ExecuteBlockAsync(newBlock.Header, new List<Transaction>(), new List<Transaction>(), CancellationToken.None));

                previousBlockHash = newNewBlock.GetHash();
                height++;
                        
                _blockList.Add(newNewBlock);
            }
            
        }
    }
}