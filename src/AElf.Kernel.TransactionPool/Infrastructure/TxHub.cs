using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Blockchain.Domain;
using AElf.Kernel.Blockchain.Events;
using AElf.Types;
using AElf.Kernel.SmartContractExecution.Application;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.TransactionPool.Infrastructure
{
    public class TxHub : ITxHub, ISingletonDependency
    {
        public ILogger<TxHub> Logger { get; set; }
        private readonly TransactionOptions _transactionOptions;

        private readonly ITransactionManager _transactionManager;
        private readonly IBlockchainService _blockchainService;

        private readonly ConcurrentDictionary<Hash, TransactionReceipt> _allTransactions =
            new ConcurrentDictionary<Hash, TransactionReceipt>();

        private ConcurrentDictionary<Hash, TransactionReceipt> _validated = new ConcurrentDictionary<Hash, TransactionReceipt>();

        private ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>> _invalidatedByBlock =
            new ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>>();

        private ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>> _expiredByExpiryBlock =
            new ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>>();

        private ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>> _futureByBlock =
            new ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>>();

        private long _bestChainHeight = Constants.GenesisBlockHeight - 1;
        private Hash _bestChainHash = Hash.Empty;

        public ILocalEventBus LocalEventBus { get; set; }

        public TxHub(ITransactionManager transactionManager, IBlockchainService blockchainService, 
            IOptionsSnapshot<TransactionOptions> transactionOptions)
        {
            Logger = NullLogger<TxHub>.Instance;
            _transactionManager = transactionManager;
            _blockchainService = blockchainService;
            LocalEventBus = NullLocalEventBus.Instance;
            _transactionOptions = transactionOptions.Value;
        }

        public async Task<ExecutableTransactionSet> GetExecutableTransactionSetAsync()
        {
            var chain = await _blockchainService.GetChainAsync();
            if (chain.BestChainHash != _bestChainHash)
            {
                Logger.LogWarning($"Attempting to retrieve executable transactions while best chain records don't match.");
                return new ExecutableTransactionSet
                {
                    PreviousBlockHash = _bestChainHash,
                    PreviousBlockHeight = _bestChainHeight
                };
            }

            var output = new ExecutableTransactionSet()
            {
                PreviousBlockHash = _bestChainHash,
                PreviousBlockHeight = _bestChainHeight
            };
            output.Transactions.AddRange(_validated.Values.Select(x => x.Transaction));
            
            Logger.LogWarning($"TxPool: _allTransactions: {_allTransactions.Count}");
            Logger.LogWarning($"TxPool: _validated: {_validated.Count}");
            if (_invalidatedByBlock.Count > 0)
            {
                var count = 0;
                foreach (var item in _invalidatedByBlock.Values)
                {
                    count += item.Count;
                }
                Logger.LogWarning($"TxPool: _invalidatedByBlock: {_invalidatedByBlock.Count}");
            }
            if (_expiredByExpiryBlock.Count > 0)
            {
                var count = 0;
                foreach (var item in _expiredByExpiryBlock.Values)
                {
                    count += item.Count;
                }
                Logger.LogWarning($"TxPool: _expiredByExpiryBlock: {_expiredByExpiryBlock.Count}");
            }
            if (_futureByBlock.Count > 0)
            {
                var count = 0;
                foreach (var item in _futureByBlock.Values)
                {
                    count += item.Count;
                }
                Logger.LogWarning($"TxPool: _futureByBlock: {_futureByBlock.Count}");
            }

            return output;
        }

        public Task<TransactionReceipt> GetTransactionReceiptAsync(Hash transactionId)
        {
            _allTransactions.TryGetValue(transactionId, out var receipt);
            return Task.FromResult(receipt);
        }

        #region Private Methods

        #region Private Static Methods

        private static void AddToCollection(ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>> collection,
            TransactionReceipt receipt)
        {
            if (!collection.TryGetValue(receipt.Transaction.RefBlockNumber, out var receipts))
            {
                receipts = new ConcurrentDictionary<Hash, TransactionReceipt>();
                collection.TryAdd(receipt.Transaction.RefBlockNumber, receipts);
            }

            receipts.TryAdd(receipt.TransactionId, receipt);
        }

        private void CheckPrefixForOne(TransactionReceipt receipt, ByteString prefix, long bestChainHeight)
        {
            if (receipt.Transaction.GetExpiryBlockNumber() <= bestChainHeight)
            {
                receipt.RefBlockStatus = RefBlockStatus.RefBlockExpired;
                return;
            }

            if (prefix == null)
            {
                receipt.RefBlockStatus = RefBlockStatus.FutureRefBlock;
                return;
            }

            if (receipt.Transaction.RefBlockPrefix == prefix)
            {
                receipt.RefBlockStatus = RefBlockStatus.RefBlockValid;
                return;
            }

            Logger.LogWarning($"TxPool: RefBlockInvalid: ExpiryBlockNumber: {receipt.Transaction.GetExpiryBlockNumber()}, bestChainHeight: {bestChainHeight}, prefix : {prefix==null}, RefBlockPrefix: {receipt.Transaction.RefBlockPrefix.ToHex()}");
            receipt.RefBlockStatus = RefBlockStatus.RefBlockInvalid;
        }

        #endregion

        private async Task<ByteString> GetPrefixByHeightAsync(Chain chain, long height, Hash bestChainHash)
        {
            var hash = await _blockchainService.GetBlockHashByHeightAsync(chain, height, bestChainHash);
            return hash == null ? null : ByteString.CopyFrom(hash.DumpByteArray().Take(4).ToArray());
        }

        private async Task<ByteString> GetPrefixByHeightAsync(long height, Hash bestChainHash)
        {
            var chain = await _blockchainService.GetChainAsync();
            return await GetPrefixByHeightAsync(chain, height, bestChainHash);
        }

        private async Task<Dictionary<long, ByteString>> GetPrefixesByHeightAsync(IEnumerable<long> heights, Hash bestChainHash)
        {
            var prefixes = new Dictionary<long, ByteString>();
            var chain = await _blockchainService.GetChainAsync();
            foreach (var h in heights)
            {
                var prefix = await GetPrefixByHeightAsync(chain, h, bestChainHash);
                prefixes.Add(h, prefix);
            }

            return prefixes;
        }

        private void ResetCurrentCollections()
        {
            _expiredByExpiryBlock = new ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>>();
            _invalidatedByBlock = new ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>>();
            _futureByBlock = new ConcurrentDictionary<long, ConcurrentDictionary<Hash, TransactionReceipt>>();
            _validated = new ConcurrentDictionary<Hash, TransactionReceipt>();
        }

        private void AddToRespectiveCurrentCollection(TransactionReceipt receipt)
        {
            switch (receipt.RefBlockStatus)
            {
                case RefBlockStatus.RefBlockExpired:
                    AddToCollection(_expiredByExpiryBlock, receipt);
                    break;
                case RefBlockStatus.FutureRefBlock:
                    AddToCollection(_futureByBlock, receipt);
                    break;
                case RefBlockStatus.RefBlockInvalid:
                    AddToCollection(_invalidatedByBlock, receipt);
                    break;
                case RefBlockStatus.RefBlockValid:
                    _validated.TryAdd(receipt.TransactionId, receipt);
                    break;
            }
        }

        #endregion

        #region Event Handler Methods

        public async Task HandleTransactionsReceivedAsync(TransactionsReceivedEvent eventData)
        {
            foreach (var transaction in eventData.Transactions)
            {
                if (!transaction.VerifySignature())
                    continue;

                var receipt = new TransactionReceipt(transaction);
                if (_allTransactions.ContainsKey(receipt.TransactionId))
                {
                    //Logger.LogWarning($"Transaction already exists in TxStore");
                    continue;
                }

                if (_allTransactions.Count > _transactionOptions.PoolLimit)
                {
                    Logger.LogWarning($"TxStore is full, ignore tx {receipt.TransactionId}");
                    break;
                }

                if (transaction.CalculateSize() > TransactionPoolConsts.TransactionSizeLimit)
                {
                    Logger.LogWarning($"Transaction {receipt.TransactionId} oversize {transaction.CalculateSize()}");
                    continue;
                }

                var txn = await _transactionManager.GetTransaction(receipt.TransactionId);
                if (txn != null)
                {
                    //Logger.LogWarning($"Transaction already exists in TxStore");
                    continue;
                }

                var success = _allTransactions.TryAdd(receipt.TransactionId, receipt);
                if (!success)
                {
                    continue;
                }

                await _transactionManager.AddTransactionAsync(transaction);

                if (_bestChainHash == Hash.Empty)
                {
                    continue;
                }

                var prefix = await GetPrefixByHeightAsync(receipt.Transaction.RefBlockNumber, _bestChainHash);
                CheckPrefixForOne(receipt, prefix, _bestChainHeight);
                AddToRespectiveCurrentCollection(receipt);
                if (receipt.RefBlockStatus == RefBlockStatus.RefBlockValid)
                {
                    await LocalEventBus.PublishAsync(new TransactionAcceptedEvent()
                    {
                        Transaction = transaction
                    });
                }
            }
        }

        public async Task HandleBlockAcceptedAsync(BlockAcceptedEvent eventData)
        {
            var block = await _blockchainService.GetBlockByHashAsync(eventData.BlockHeader.GetHash());
            foreach (var txId in block.Body.Transactions)
            {
                _allTransactions.TryRemove(txId, out _);
            }
        }

        public async Task HandleBestChainFoundAsync(BestChainFoundEventData eventData)
        {
            Logger.LogDebug($"Handle best chain found: BlockHeight: {eventData.BlockHeight}, BlockHash: {eventData.BlockHash}");
            
            var heights = _allTransactions.Select(kv => kv.Value.Transaction.RefBlockNumber).Distinct();
            var prefixes = await GetPrefixesByHeightAsync(heights, eventData.BlockHash);
            ResetCurrentCollections();
            foreach (var kv in _allTransactions)
            {
                var prefix = prefixes[kv.Value.Transaction.RefBlockNumber];
                CheckPrefixForOne(kv.Value, prefix, _bestChainHeight);
                AddToRespectiveCurrentCollection(kv.Value);
            }

            _bestChainHash = eventData.BlockHash;
            _bestChainHeight = eventData.BlockHeight;
            
            Logger.LogDebug($"Finish handle best chain found: BlockHeight: {eventData.BlockHeight}, BlockHash: {eventData.BlockHash}");
        }

        public async Task HandleNewIrreversibleBlockFoundAsync(NewIrreversibleBlockFoundEvent eventData)
        {
            foreach (var txIds in _expiredByExpiryBlock.Where(kv => kv.Key <= eventData.BlockHeight))
            {
                foreach (var txId in txIds.Value.Keys)
                {
                    _allTransactions.TryRemove(txId, out _);
                }
            }

            await Task.CompletedTask;
        }

        public async Task HandleUnexecutableTransactionsFoundAsync(UnexecutableTransactionsFoundEvent eventData)
        {
            foreach (var txId in eventData.Transactions)
            {
                _allTransactions.TryRemove(txId, out _);
            }
            await Task.CompletedTask;
        }
        #endregion

        public async Task<int> GetTransactionPoolSizeAsync()
        {
            return await Task.FromResult(_allTransactions.Count);
        }
    }
}