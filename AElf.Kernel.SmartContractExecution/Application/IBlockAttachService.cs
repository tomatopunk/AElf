using System.Diagnostics;
using System.Threading.Tasks;
using AElf.Common;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Blockchain.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Threading;

namespace AElf.Kernel.SmartContractExecution.Application
{
    public interface IBlockAttachService
    {
        Task AttachBlockAsync(Block block);
    }

    public class BlockAttachService : IBlockAttachService, ITransientDependency
    {
        private readonly IBlockchainService _blockchainService;
        private readonly IBlockchainExecutingService _blockchainExecutingService;
        public ILogger<BlockAttachService> Logger { get; set; }

        public BlockAttachService(IBlockchainService blockchainService,
            IBlockchainExecutingService blockchainExecutingService)
        {
            _blockchainService = blockchainService;
            _blockchainExecutingService = blockchainExecutingService;

            Logger = NullLogger<BlockAttachService>.Instance;
        }

        public async Task AttachBlockAsync(Block block)
        {
            var existBlock = await _blockchainService.GetBlockHeaderByHashAsync(block.GetHash());
            if (existBlock == null)
            {
                await Stopwatch.StartNew().Measure(() => _blockchainService.AddBlockAsync(block), elapsed => Logger.LogInformation($"Add block perf {elapsed.Milliseconds} ms"));
                var chain = await _blockchainService.GetChainAsync();
                var status = await Stopwatch.StartNew().Measure(() => _blockchainService.AttachBlockToChainAsync(chain, block),
                    elapsed => Logger.LogInformation($"Attach block to longest chain perf {elapsed.Milliseconds} ms"));
                await Stopwatch.StartNew().Measure(() => _blockchainExecutingService.ExecuteBlocksAttachedToLongestChain(chain, status),
                    elapsed => Logger.LogInformation($"Attach block to best chain perf {elapsed.Milliseconds} ms"));
            }
        }
    }
}