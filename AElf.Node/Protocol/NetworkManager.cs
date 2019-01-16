using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AElf.ChainController.EventMessages;
using AElf.Common;
using AElf.Common.Attributes;
using AElf.Common.Collections;
using AElf.Kernel;
using AElf.Miner.EventMessages;
using AElf.Network;
using AElf.Network.Connection;
using AElf.Network.Data;
using AElf.Network.Eventing;
using AElf.Network.Peers;
using AElf.Node.AElfChain;
using AElf.Node.EventMessages;
using AElf.Sdk.CSharp;
using AElf.Synchronization.BlockSynchronization;
using AElf.Synchronization.EventMessages;
using Easy.MessageHub;
using Google.Protobuf;
using NLog;

[assembly:InternalsVisibleTo("AElf.Network.Tests")]
namespace AElf.Node.Protocol
{
    [LoggerName(nameof(NetworkManager))]
    public class NetworkManager : INetworkManager
    {
        #region Settings

        public const int DefaultHeaderRequestCount = 3;
        public const int DefaultMaxBlockHistory = 100;
        public const int DefaultMaxTransactionHistory = 20;

        public const int DefaultRequestTimeout = 2000;
        public const int DefaultRequestMaxRetry = TimeoutRequest.DefaultMaxRetry;

        public int MaxBlockHistory { get; set; } = DefaultMaxBlockHistory;
        public int MaxTransactionHistory { get; set; } = DefaultMaxTransactionHistory;

        public int RequestTimeout { get; set; } = DefaultRequestTimeout;
        public int RequestMaxRetry { get; set; } = DefaultRequestMaxRetry;

        #endregion
        
        private readonly IPeerManager _peerManager;
        private readonly ILogger _logger;
        private readonly IBlockSynchronizer _blockSynchronizer;
        private readonly INodeService _nodeService;

        private readonly List<IPeer> _peers;

        private BoundedByteArrayQueue _seenBlocks;
        private BoundedByteArrayQueue _lastTxReceived;
        private BoundedByteArrayQueue _temp = new BoundedByteArrayQueue(10);

        private List<byte[]> _lastRequestedAnnouncements;
        private readonly object _aLock = new object();

        private readonly BlockingPriorityQueue<PeerMessageReceivedArgs> _incomingJobs;

        private ulong _currentLibNum;

        internal IPeer HistorySyncer { get; set; }
        internal int LocalHeight;

        internal int UnlinkableHeaderIndex;
        
        private readonly object _syncLock = new object();

        public NetworkManager(IPeerManager peerManager, IBlockSynchronizer blockSynchronizer, INodeService nodeService, ILogger logger)
        {
            _incomingJobs = new BlockingPriorityQueue<PeerMessageReceivedArgs>();
            _peers = new List<IPeer>();

            _peerManager = peerManager;
            _logger = logger;
            _blockSynchronizer = blockSynchronizer;
            _nodeService = nodeService;

            _lastRequestedAnnouncements = new List<byte[]>();

            peerManager.PeerEvent += OnPeerAdded;

            MessageHub.Instance.Subscribe<TransactionAddedToPool>(inTx =>
            {
                if (inTx?.Transaction == null)
                {
                    _logger?.Warn("[event] Transaction null.");
                    return;
                }

                var txHash = inTx.Transaction.GetHashBytes();

                if (txHash != null)
                    _lastTxReceived.Enqueue(txHash);

                if (_peers == null || !_peers.Any())
                    return;

                BroadcastMessage(AElfProtocolMsgType.NewTransaction, inTx.Transaction.Serialize());
            });

            MessageHub.Instance.Subscribe<BlockMined>(inBlock =>
            {
                if (inBlock?.Block == null)
                {
                    _logger?.Warn("[event] Block null.");
                    return;
                }

                byte[] blockHash = inBlock.Block.GetHash().DumpByteArray();
                
                lock (_aLock)
                {
                    _logger?.Debug($"Added mined block {blockHash}.");
                    _lastRequestedAnnouncements.Add(blockHash);
                }

                if (blockHash != null)
                    _seenBlocks.Enqueue(blockHash);

                AnnounceBlock((Block) inBlock.Block);

                _logger?.Info($"Block produced, announcing {blockHash.ToHex()} to peers ({string.Join("|", _peers)}) with " +
                              $"{inBlock.Block.Body.TransactionsCount} txs, block height {inBlock.Block.Header.Index}.");

                LocalHeight = (int) inBlock.Block.Index;
            });

            MessageHub.Instance.Subscribe<BlockAccepted>(inBlock =>
            {
                if (inBlock?.Block == null)
                {
                    _logger?.Warn("[event] Block null.");
                    return;
                }

                // Note - This should not happen during header this
                if (UnlinkableHeaderIndex != 0)
                    return;

                IBlock acceptedBlock = inBlock.Block;
                
                var blockHash = acceptedBlock.GetHash().DumpByteArray();

                // todo TEMP 
                if (_temp.Contains(blockHash))
                    return;

                _temp.Enqueue(blockHash);

                _logger?.Trace($"Block accepted, announcing {blockHash.ToHex()} to peers ({string.Join("|", _peers)}), " +
                               $"block height {acceptedBlock.Header.Index}.");
                
                lock (_syncLock)
                {
                    if (HistorySyncer == null || !HistorySyncer.IsSyncingHistory)
                    {
                        AnnounceBlock(acceptedBlock);
                    }
                }
            });
            
            MessageHub.Instance.Subscribe<BlockExecuted>(inBlock =>
            {
                if (inBlock?.Block == null)
                {
                    _logger?.Warn("[event] Block null.");
                    return;
                }
//
//                // Note - This should not happen during header this
//                if (UnlinkableHeaderIndex != 0)
//                    return;
//            
                LocalHeight = (int) inBlock.Block.Index;
//            
//                SyncNext();
            });

            MessageHub.Instance.Subscribe<UnlinkableHeader>(unlinkableHeaderMsg =>
            {
                if (unlinkableHeaderMsg?.Header == null)
                {
                    _logger?.Warn("[event] message or header null.");
                    return;
                }
                
                // The reception of this event means that the chain has discovered 
                // that the current block it is trying to execute (height H) is 
                // not linkable to the block we have at H-1.
                
                // At this point we stop all current syncing activities and repetedly 
                // download previous headers to the block we couldn't link (in other 
                // word "his branch") until we find a linkable block (at wich point 
                // the HeaderAccepted event should be launched.
                
                // note that when this event is called, our knowledge of the local 
                // height doesn't mean much.
                
                lock (_syncLock)
                {
                    // If this is already != 0, it means that the previous batch of 
                    // headers was not linked and that more need to be requested. 
                    if (UnlinkableHeaderIndex != 0)
                    {
                        // Set state with the first occurence of the unlinkable block
                        UnlinkableHeaderIndex = (int) unlinkableHeaderMsg.Header.Index;
                    }
                    else
                    {
                        HistorySyncer = null;
                        
                        // Reset all syncing operations
                        foreach (var peer in _peers)
                        {
                            peer.ResetSync();
                        }

                        LocalHeight = 0;
                    }
                }

                _logger?.Trace($"Header unlinkable, height {unlinkableHeaderMsg.Header.Index}.");

                // Use the peer with the highest target to request headers.
                IPeer target = _peers
                    .Where(p => p.KnownHeight >= (int) unlinkableHeaderMsg.Header.Index)
                    .OrderByDescending(p => p.KnownHeight)
                    .FirstOrDefault();

                if (target == null)
                {
                    _logger?.Warn("[event] no peers to sync from.");
                    return;
                }

                target.RequestHeaders((int) unlinkableHeaderMsg.Header.Index, DefaultHeaderRequestCount);
            });

            MessageHub.Instance.Subscribe<HeaderAccepted>(header =>
            {
                if (header?.Header == null)
                {
                    _logger?.Warn("[event] message or header null.");
                    return;
                }

                if (UnlinkableHeaderIndex != 0)
                {
                    _logger?.Warn("[event] HeaderAccepted but network module not in recovery mode.");
                    return;
                }
                
                if (HistorySyncer != null)
                {
                    // todo possible sync reset
                    _logger?.Warn("[event] current sync source is not null");
                    return;
                }

                lock (_syncLock)
                {
                    // Local height reset 
                    LocalHeight = (int) header.Header.Index - 1;
                    
                    // Reset Unlinkable header state
                    UnlinkableHeaderIndex = 0;
                    
                    _logger?.Trace($"[event] header accepted, height {header.Header.Index}, local height reset to {header.Header.Index - 1}.");
                    
                    // Use the peer with the highest target that is higher than our height.
                    IPeer target = _peers
                        .Where(p => p.KnownHeight > LocalHeight)
                        .OrderByDescending(p => p.KnownHeight)
                        .FirstOrDefault();
    
                    if (target == null)
                    {
                        _logger?.Warn("[event] no peers to sync from.");
                        return;
                    }
                    
                    HistorySyncer = target;
                    HistorySyncer?.SyncToHeight(LocalHeight + 1, target.KnownHeight);
                    
                    FireSyncStateChanged(true);
                }
            });

            MessageHub.Instance.Subscribe<ChainInitialized>(inBlock =>
            {
                _peerManager.Start();
                Task.Run(StartProcessingIncoming).ConfigureAwait(false);
            });
            
            MessageHub.Instance.Subscribe<MinorityForkDetected>(inBlock => { OnMinorityForkDetected(); });

            MessageHub.Instance.Subscribe<NewLibFound>(msg =>
            {
                _currentLibNum = msg.Height;
                _logger?.Debug($"Network lib updated : {_currentLibNum}.");
            });

            MessageHub.Instance.Subscribe<BlockRejected>(msg =>
            {
                // the block that is currently been synced has failed 
                lock (_syncLock)
                {
                    if (msg?.Block == null)
                        _logger?.Warn("[event] Block rejected: block null.");
                    
                    if (HistorySyncer == null)
                        _logger?.Warn("Unexpected situation, rejected a block but no peer is currently syncing.");

                    if (HistorySyncer != null && HistorySyncer.IsSyncingHistory)
                    {
                        // If we're currently syncing history
                        var next = _peers.FirstOrDefault(p => p != HistorySyncer && p.KnownHeight > LocalHeight);

                        if (next == null)
                        {
                            _logger?.Warn("Rejected block but no other peer to sync from. ");
                            return;
                        }
                        
                        HistorySyncer = next;
                        HistorySyncer.SyncToHeight(LocalHeight + 1, next.KnownHeight);
                    }
                    else
                    {
                        HistorySyncer.ResetSync();
                        //SyncNext(); // get another peer to sync from
                    }
                }
            });
            
            MessageHub.Instance.Subscribe<BlockLinked>(msg =>
            {

            });
        }
        
        private void OnMinorityForkDetected()
        {
            try
            {
                // reset everything inside the lock to keep a coherent state
                
                lock (_syncLock)
                {
                    HistorySyncer = null;
                    foreach (var peer in _peers)
                    {
                        peer.ResetSync();
                    }

                    _seenBlocks.Clear();
                    _lastTxReceived.Clear();
                    _temp.Clear();

                    LocalHeight = (int) _currentLibNum;
                    
                    // todo should be from a BP that is not part of the minority
                    IPeer syncPeer = _peers.FirstOrDefault(p => p.KnownHeight > LocalHeight);

                    if (syncPeer != null)
                    {
                        HistorySyncer = syncPeer;
                        HistorySyncer.SyncToHeight(LocalHeight + 1, syncPeer.KnownHeight);
                        
                        FireSyncStateChanged(true);
                    }
                    else
                    {
                        _logger?.Warn("Could not find any peer to sync to.");
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while reset for minority fork.");
            }
        }

        private void FireSyncStateChanged(bool newState)
        {
            Task.Run(() => MessageHub.Instance.Publish(new SyncStateChanged(newState)));
        }

        /// <summary>
        /// This method start the server that listens for incoming
        /// connections and sets up the manager.
        /// </summary>
        public async Task Start()
        {
            // init the queue
            try
            {
                _seenBlocks = new BoundedByteArrayQueue(MaxBlockHistory);
                _lastTxReceived = new BoundedByteArrayQueue(MaxTransactionHistory);

                LocalHeight = await _nodeService.GetCurrentBlockHeightAsync();

                _logger?.Info($"Network initialized at height {LocalHeight}.");
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while initializing the network.");
                throw;
            }
        }

        public async Task Stop()
        {
            await _peerManager.Stop();
        }

        private void AnnounceBlock(IBlock block)
        {
            if (block?.Header == null)
            {
                _logger?.Error("Block or block header is null.");
                return;
            }
            
            if (_peers == null || !_peers.Any())
                return;

            try
            {
                Announce anc = new Announce
                {
                    Height = (int) block.Header.Index,
                    Id = ByteString.CopyFrom(block.GetHashBytes())
                };

                BroadcastMessage(AElfProtocolMsgType.Announcement, anc.ToByteArray());
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while announcing block.");
            }
        }

        #region Eventing

        private void OnPeerAdded(object sender, EventArgs eventArgs)
        {
            try
            {
                if (eventArgs is PeerEventArgs peer && peer.Peer != null)
                {
                    if (peer.Actiontype == PeerEventType.Added)
                    {
                        int peerHeight = peer.Peer.KnownHeight;
                
                        _peers.Add(peer.Peer);

                        peer.Peer.MessageReceived += HandleNewMessage;
                        peer.Peer.PeerDisconnected += ProcessClientDisconnection;

                        // Sync if needed
                        lock (_syncLock)
                        {
                            // If not doing initial sync
                            if (LocalHeight < peerHeight)
                            {
                                peer.Peer.StartDownloadBranch();
                            }
                        }
                    }
                    else
                    {
                        _peers.Remove(peer.Peer);
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e);
            }
        }

        /// <summary>
        /// Callback for when a Peer fires a <see cref="IPeer.PeerDisconnected"/> event. It unsubscribes
        /// the manager from the events and removes it from the list.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ProcessClientDisconnection(object sender, EventArgs e)
        {
            if (sender != null && e is PeerDisconnectedArgs args && args.Peer != null)
            {
                IPeer peer = args.Peer;

                peer.MessageReceived -= HandleNewMessage;
                peer.PeerDisconnected -= ProcessClientDisconnection;

                _peers.Remove(args.Peer);

                lock (_syncLock)
                {
                    if (HistorySyncer != null && HistorySyncer.IsSyncingHistory)
                    {
                        IPeer nextHistSyncSource = _peers.FirstOrDefault(p => p.KnownHeight >= args.Peer.SyncTarget);

                        if (nextHistSyncSource == null)
                        {
                            _logger?.Warn("No other peer to sync from.");
                            return;
                        }

                        HistorySyncer = nextHistSyncSource;
                        nextHistSyncSource.SyncToHeight(LocalHeight+1, nextHistSyncSource.KnownHeight);
                    }
                    else
                    {
                        _logger?.Debug($"Peer {args.Peer} has been removed, trying to find another peer to sync.");
                        //SyncNext();
                    }
                }
            }
        }

        private void HandleNewMessage(object sender, EventArgs e)
        {
            // exec on the peers thread
            if (e is PeerMessageReceivedArgs args)
            {
                if (args.Peer.IsDownloadingBranch && args.Message.Type == (int)AElfProtocolMsgType.NewTransaction)
                    return; // if we receive a transaction during initial sync, we just ignore.
                
                if (args.Message.Type == (int)AElfProtocolMsgType.Block)
                {
                    // If we get a block deserialize it here to stop the request timer asap
                    Block block;
                    
                    try
                    {
                        block = Block.Parser.ParseFrom(args.Message.Payload);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, $"Could not deserialize block from {args.Peer}");
                        return; // todo handle
                    }

                    // Some basic checks about the block 
                    if (block?.Header == null)
                    {
                        _logger?.Warn($"Block {args.Peer} has a null header.");
                        return; // todo handle
                    }
                    
                    if (block.Header.Index < _currentLibNum)
                    {
                        _logger?.Warn($"Block {block} is lower than lib ({_currentLibNum}).");
                        
                        if (args.Peer.IsDownloadingBranch)
                        {
                            // todo maybe special handling gone past lib  
                        }
                        
                        // todo handle
                        return;
                    }

                    byte[] blockHash = block.GetHashBytes();
                    bool stoped = args.Peer.StopBlockTimer(block);

                    if (!stoped)
                    {
                        // He sent us a block by mistake or simply the request timed out
                        // when the block was in flight
                        _logger?.Warn($"Could not find timer for block {block} in {args.Peer}");
                        return;
                    }

                    // For now downloading a branch can download duplicates
                    // todo detect that we're downloading the same branch
                    if (!args.Peer.IsDownloadingBranch && _seenBlocks.Contains(blockHash)) // todo review lastseenblock thread safety
                    {
                        _logger.Warn($"Block {blockHash.ToHex()} already received by the network.");
                        //DoNext(block); // todo review
                        return;
                    }

                    // first level cache for all blocks received
                    _seenBlocks.Enqueue(blockHash);
                    
                    _logger?.Info($"Block received {block}, with {block.Body.Transactions.Count} txns. (TransactionListCount = {block.Body.TransactionList.Count})");
                    
                    // If this peer is syncing a branch we continue the download.
                    if (args.Peer.IsDownloadingBranch)
                    {
                        _logger?.Debug("Received a block from a branch that's downloading.");
                    
                        // Means that these peers are waiting for a block from there branch
                        // We get the previous to check linkability in the block set.
                        IBlock previousBlock = _blockSynchronizer.GetBlockByHash(block.Header.PreviousBlockHash, includeCache: false); // todo review this searches blockchain

                        if (previousBlock != null)
                        {
                            // linkable, this means that the branch they are downloading 
                            // has been completed.
                            
                            List<IBlock> branch = args.Peer.FinishBranchDownload(block);
                        
                            // todo send branch to block sync
                            args.Branch = branch;
                            
                            _incomingJobs.Enqueue(args, 0);
                            
                            Debug.Assert(!args.Peer.IsDownloadingBranch, "the peer did not finish downloading !");
                            
                            // Go get any announcements 
                            lock (_aLock)
                            {
                                Announce low = args.Peer.GetLowestAnnouncement();
                                
                                if (low != null && _lastRequestedAnnouncements.Any(an => an.BytesEqual(low.Id.ToByteArray())))
                                {
                                    _logger?.Debug($"{args.Peer} announcement already requested.");
                                    return;
                                }
                                
                                if (low != null && low.Height != 0)
                                {
                                    if (low.Height == (int)block.Header.Index + 1)
                                    {
                                        args.Peer.SyncNextAnnouncement();
                                    }
                                    else
                                    {
                                        // todo need to get more 
                                    }
                                }
                                else
                                {
                                    _logger?.Debug($"All synced up to {args.Peer} !");
                                }
                            }
                            
                            return;
                        }
                        else
                        {
                            args.Peer.AddBranchedBlock(block);
                            return;
                        }
                    }
                    else if (args.Peer.IsSyncingAnnounced)
                    {
                        lock (_aLock)
                        {
                            int removed = _lastRequestedAnnouncements.RemoveAll(a => a.BytesEqual(blockHash));
                            _logger?.Debug($"Removed {removed} announcements from the cache.");
                        }
                        
                        // Verify that it's the currently synced announcement
                        if (!args.Peer.SyncedAnnouncement.Id.ToByteArray().BytesEqual(blockHash))
                        {
                            _logger?.Warn($"Block {blockHash.ToHex()} accepted by the chain but not currently synced.");
                            return; // todo handle
                        }
                        
                        args.Block = block;
                        _incomingJobs.Enqueue(args, 0);

                        byte[] hasReqNext;
                        lock (_aLock)
                        {
                            // We've received this block, so every peer can move on now
                            foreach (var peer in _peers)
                                peer.CleanAnnouncement(blockHash);
                            
                            if (_lastRequestedAnnouncements.Any(an => an.BytesEqual(blockHash)))
                            {
                                _logger?.Debug($"{args.Peer} announcement already requested.");
                                return;
                            }
                            
                            _lastRequestedAnnouncements.Add(blockHash);
                            hasReqNext = args.Peer.SyncNextAnnouncement();
                        }

                        if (hasReqNext != null)
                            return;
                
                        _logger?.Trace($"Catched up to announcements with {args.Peer}.");
                        return;    
                    }
                    else
                    {
                        args.Block = block;
                    }
                }

                _incomingJobs.Enqueue(args, 0);
            }
        }

        private void TrySyncAnnouncement(Peer argsPeer)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Message processing

        private async Task StartProcessingIncoming()
        {
            while (true)
            {
                try
                {
                    PeerMessageReceivedArgs msg = _incomingJobs.Take();
                    await ProcessPeerMessage(msg);
                }
                catch (Exception e)
                {
                    _logger?.Error(e, "Error while processing incoming messages");
                }
            }
        }

        private async Task ProcessPeerMessage(PeerMessageReceivedArgs args)
        {
            if (args?.Peer == null)
            {
                _logger.Warn("Peer is invalid.");
                return;
            }
            
            if (args.Message?.Payload == null)
            {
                _logger?.Warn($"Message from {args.Peer}, message/payload is null.");
                return;
            }

            AElfProtocolMsgType msgType = (AElfProtocolMsgType) args.Message.Type;

            switch (msgType)
            {
                case AElfProtocolMsgType.Announcement:
                    HandleAnnouncement(args.Message, args.Peer);
                    break;
                case AElfProtocolMsgType.Block:
                {
                    if (args.Branch != null)
                    {
                        MessageHub.Instance.Publish(new BlockReceived(args.Branch));
                    }
                    else
                    {
                        MessageHub.Instance.Publish(new BlockReceived(args.Block));
                    }
                    
                    break;
                }
                case AElfProtocolMsgType.NewTransaction:
                    HandleNewTransaction(args.Message);
                    break;
                case AElfProtocolMsgType.Headers:
                    HandleHeaders(args.Message);
                    break;
                case AElfProtocolMsgType.RequestBlock:
                    await HandleBlockRequestJob(args);
                    break;
                case AElfProtocolMsgType.HeaderRequest:
                    await HandleHeaderRequest(args);
                    break;
            }
        }

        private async Task HandleHeaderRequest(PeerMessageReceivedArgs args)
        {
            try
            {
                var hashReq = BlockHeaderRequest.Parser.ParseFrom(args.Message.Payload);
                
                var blockHeaderList = await _nodeService.GetBlockHeaderList((ulong) hashReq.Height, hashReq.Count);
                
                var req = NetRequestFactory.CreateMessage(AElfProtocolMsgType.Headers, blockHeaderList.ToByteArray());
                
                if (args.Message.HasId)
                    req.Id = args.Message.Id;

                args.Peer.EnqueueOutgoing(req);

                _logger?.Debug($"Send {blockHeaderList.Headers.Count} block headers start " +
                               $"from {blockHeaderList.Headers.FirstOrDefault()?.GetHash().ToHex()}, to node {args.Peer}.");
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while during HandleBlockRequest.");
            }
        }

        private async Task HandleBlockRequestJob(PeerMessageReceivedArgs args)
        { 
            try
            {
                var breq = BlockRequest.Parser.ParseFrom(args.Message.Payload);
                
                Block b;
                
                if (breq.Id != null && breq.Id.Length > 0)
                {
                    b = await _nodeService.GetBlockFromHash(breq.Id.ToByteArray());
                }
                else
                {
                    b = await _nodeService.GetBlockAtHeight(breq.Height);
                }

                if (b == null)
                {
                    _logger?.Warn($"Block not found {breq.Id?.ToByteArray().ToHex()}");
                    return;
                }
                
                Message req = NetRequestFactory.CreateMessage(AElfProtocolMsgType.Block, b.ToByteArray());
                
                if (args.Message.HasId)
                    req.Id = args.Message.Id;

                // Send response
                args.Peer.EnqueueOutgoing(req, _ =>
                {
                    _logger?.Debug($"Block sent {{ hash: {b.BlockHashToHex}, to: {args.Peer} }}");
                });
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while during HandleBlockRequest.");
            }
        }

        private void HandleHeaders(Message msg)
        {
            try
            {
                BlockHeaderList blockHeaders = BlockHeaderList.Parser.ParseFrom(msg.Payload);
                MessageHub.Instance.Publish(new HeadersReceived(blockHeaders.Headers.ToList()));
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while handling header list.");
            }
        }

        private void HandleAnnouncement(Message msg, Peer peer)
        {
            try
            {
                Announce a = Announce.Parser.ParseFrom(msg.Payload);

                byte[] blockHash = a.Id.ToByteArray();
                
                peer.OnAnnouncementMessage(a); // todo - impr - move completely inside peer class.

                // If we already know about this block don't stash the announcement and return.
                if (_seenBlocks.Contains(blockHash) || _blockSynchronizer.GetBlockByHash(Hash.LoadByteArray(blockHash)) != null)
                {
                    _logger?.Debug($"{peer} announced an already known block : {{ id: {blockHash.ToHex()}, height: {a.Height} }}");
                    return;
                }

                // todo report in new logic
//                if (CurrentSyncSource != null && CurrentSyncSource.IsSyncingHistory && a.Height <= CurrentSyncSource.SyncTarget)
//                {
//                    _logger?.Trace($"{peer} : ignoring announce {a.Height} because history sync will fetch (sync target {CurrentSyncSource.SyncTarget}).");
//                    return;
//                }

                if (UnlinkableHeaderIndex != 0)
                {
                    _logger?.Trace($"{peer} : ignoring announce {a.Height} because we're syncing unlinkable.");
                    return;
                }
                
                lock (_syncLock)
                {
                    // stash inside the lock because BlockAccepted will clear 
                    // announcements after the chain accepts
                    peer.StashAnnouncement(a); // todo review thread safety
                    
                    if (!peer.IsSyncing)
                    {
                        lock (_aLock)
                        {
                            if (_lastRequestedAnnouncements.Any(an => an.BytesEqual(blockHash)))
                            {
                                _logger?.Debug($"{peer} announcement already requested.");
                                return;
                            }
                            
                            peer.SyncNextAnnouncement();
                            _lastRequestedAnnouncements.Add(blockHash);
                        }
                    }
                    else
                    {
                        _logger?.Debug($"{peer} is already syncing...");
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while handling annoucement.");
            }
        }

        private void HandleNewTransaction(Message msg)
        {
            try
            {
                Transaction tx = Transaction.Parser.ParseFrom(msg.Payload);

                byte[] txHash = tx.GetHashBytes();

                if (_lastTxReceived.Contains(txHash))
                    return;

                _lastTxReceived.Enqueue(txHash);

                MessageHub.Instance.Publish(new TxReceived(tx));
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while handling new transaction reception.");
            }
        }

        #endregion

        /// <summary>
        /// This message broadcasts data to all of its peers. This creates and
        /// sends an object with the provided pay-load and message type.
        /// </summary>
        /// <param name="messageMsgType"></param>
        /// <param name="payload"></param>
        /// <returns></returns>
        private void BroadcastMessage(AElfProtocolMsgType messageMsgType, byte[] payload)
        {
            if (_peers == null || !_peers.Any())
            {
                _logger?.Warn("Cannot broadcast - no peers.");
                return;
            }
            
            try
            {
                Message message = NetRequestFactory.CreateMessage(messageMsgType, payload);
                
                foreach (var peer in _peers)
                    peer.EnqueueOutgoing(message);
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Error while sending a message to the peers.");
            }
        }
    }
}