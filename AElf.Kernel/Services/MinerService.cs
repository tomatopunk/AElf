using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Common;
using AElf.Kernel.Account.Application;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Consensus.Application;
using AElf.Kernel.Miner.Application;
using AElf.Kernel.SmartContractExecution.Application;
using AElf.Kernel.TransactionPool.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.EventBus.Local;
using ByteString = Google.Protobuf.ByteString;

namespace AElf.Kernel.Services
{
    public class MinerService : IMinerService
    {
        public ILogger<MinerService> Logger { get; set; }
        private ITxHub _txHub;
        private IMiningService _miningService;
        public ILocalEventBus EventBus { get; set; }

        private const float RatioMine = 0.3f;

        public MinerService(IMiningService miningService, ITxHub txHub)
        {
            _miningService = miningService;
            _txHub = txHub;

            EventBus = NullLocalEventBus.Instance;
        }

        /// <inheritdoc />
        /// <summary>
        /// Mine process.
        /// </summary>
        /// <returns></returns>
        public async Task<Block> MineAsync(Hash previousBlockHash, long previousBlockHeight, DateTime dateTime,
            TimeSpan timeSpan)
        {
            Logger.LogTrace($"I have {(timeSpan.Milliseconds - DateTime.UtcNow.Millisecond)} ms for mining");

            var executableTransactionSet = await Stopwatch.StartNew().Measure(() => _txHub.GetExecutableTransactionSetAsync(),
                elapsed => Logger.LogInformation($"Get transaction from perf: {elapsed.Milliseconds} ms"));
            var pending = new List<Transaction>();
            if (executableTransactionSet.PreviousBlockHash == previousBlockHash)
            {
                pending = executableTransactionSet.Transactions;
            }
            else
            {
                Logger.LogWarning($"Transaction pool gives transactions to be appended to " +
                                  $"{executableTransactionSet.PreviousBlockHash} which doesn't match the current " +
                                  $"best chain hash {previousBlockHash}.");
            }

            Logger.LogTrace($"Get {pending.Count} transactions, have {timeSpan.Milliseconds - DateTime.UtcNow.Millisecond}ms for mining");

            return await _miningService.MineAsync(previousBlockHash, previousBlockHeight, pending, dateTime, timeSpan);
        }
    }

    public class MiningService : IMiningService
    {
        public ILogger<MiningService> Logger { get; set; }
        private readonly ISystemTransactionGenerationService _systemTransactionGenerationService;
        private readonly IBlockGenerationService _blockGenerationService;
        private readonly IAccountService _accountService;

        private readonly IBlockExecutingService _blockExecutingService;

        public ILocalEventBus EventBus { get; set; }

        public MiningService(IAccountService accountService,
            IBlockGenerationService blockGenerationService,
            ISystemTransactionGenerationService systemTransactionGenerationService,
            IBlockExecutingService blockExecutingService)
        {
            Logger = NullLogger<MiningService>.Instance;
            _blockGenerationService = blockGenerationService;
            _systemTransactionGenerationService = systemTransactionGenerationService;
            _blockExecutingService = blockExecutingService;
            _accountService = accountService;

            EventBus = NullLocalEventBus.Instance;
        }

        private async Task<List<Transaction>> GenerateSystemTransactions(Hash previousBlockHash,
            long previousBlockHeight)
        {
            //var previousBlockPrefix = previousBlockHash.Value.Take(4).ToArray();
            var address = Address.FromPublicKey(await _accountService.GetPublicKeyAsync());

            var generatedTxns =
                _systemTransactionGenerationService.GenerateSystemTransactions(address, previousBlockHeight,
                    previousBlockHash);

            foreach (var txn in generatedTxns)
            {
                await SignAsync(txn);
            }

            return generatedTxns;
        }

        private async Task SignAsync(Transaction notSignerTransaction)
        {
            if (notSignerTransaction.Sigs.Count > 0)
                return;
            // sign tx
            var signature = await _accountService.SignAsync(notSignerTransaction.GetHash().DumpByteArray());
            notSignerTransaction.Sigs.Add(ByteString.CopyFrom(signature));
        }

        /// <summary>
        /// Generate block
        /// </summary>
        /// <returns></returns>
        private async Task<Block> GenerateBlock(Hash preBlockHash, long preBlockHeight, DateTime expectedMiningTime)
        {
            var block = await _blockGenerationService.GenerateBlockBeforeExecutionAsync(new GenerateBlockDto
            {
                PreviousBlockHash = preBlockHash,
                PreviousBlockHeight = preBlockHeight,
                BlockTime = expectedMiningTime
            });
            return block;
        }

        private async Task SignBlockAsync(Block block)
        {
            var publicKey = await _accountService.GetPublicKeyAsync();
            block.Sign(publicKey, data => _accountService.SignAsync(data));
        }

        public async Task<Block> MineAsync(Hash previousBlockHash, long previousBlockHeight,
            List<Transaction> transactions, DateTime blockTime, TimeSpan timeSpan)
        {
            var block = await Stopwatch.StartNew().Measure(() => GenerateBlock(previousBlockHash, previousBlockHeight, blockTime),
                elapsed => Logger.LogInformation($"Gen block perf: {elapsed.Milliseconds} ms"));
            var systemTransactions = await  Stopwatch.StartNew().Measure(() => GenerateSystemTransactions(previousBlockHash, previousBlockHeight),
                elapsed => Logger.LogInformation($"Gen system tx perf: {elapsed.Milliseconds} ms"));
            var pending = transactions;

            using (var cts = new CancellationTokenSource())
            {
                cts.CancelAfter(timeSpan);
                block = await _blockExecutingService.ExecuteBlockAsync(block.Header,
                    systemTransactions, pending, cts.Token);
            }

            Logger.LogInformation($"Generated block: {block.ToDiagnosticString()}, " +
                                  $"previous: {block.Header.PreviousBlockHash}, " +
                                  $"transactions: {block.Body.TransactionsCount}");

            await SignBlockAsync(block);
            // TODO: TxHub needs to be updated when BestChain is found/extended, so maybe the call should be centralized
            //await _txHub.OnNewBlock(block);

            return block;
        }
    }
}