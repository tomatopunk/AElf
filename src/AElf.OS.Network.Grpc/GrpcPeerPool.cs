using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Account.Application;
using AElf.Kernel.Blockchain.Application;
using AElf.OS.Network.Events;
using AElf.OS.Network.Infrastructure;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.EventBus.Local;
using Volo.Abp.Threading;

namespace AElf.OS.Network.Grpc
{
    public class GrpcPeerPool : IPeerPool
    {
        private readonly NetworkOptions _networkOptions;

        private readonly IAccountService _accountService;
        private readonly IBlockchainService _blockchainService;

        private readonly ConcurrentDictionary<string, GrpcPeer> _authenticatedPeers;

        public ILocalEventBus EventBus { get; set; }
        public IReadOnlyDictionary<long, Hash> RecentBlockHeightAndHashMappings { get; }

        private readonly ConcurrentDictionary<long, Hash> _recentBlockHeightAndHashMappings;
        
        public IReadOnlyDictionary<long, Hash> PreLibBlockHeightAndHashMappings { get; }
        
        private readonly ConcurrentDictionary<long, Hash> _preLibBlockHeightAndHashMappings;

        public ILogger<GrpcPeerPool> Logger { get; set; }

        public GrpcPeerPool(IOptionsSnapshot<NetworkOptions> networkOptions, IAccountService accountService, 
            IBlockchainService blockChainService)
        {
            _networkOptions = networkOptions.Value;
            _accountService = accountService;
            _blockchainService = blockChainService;

            _authenticatedPeers = new ConcurrentDictionary<string, GrpcPeer>();
            _recentBlockHeightAndHashMappings = new ConcurrentDictionary<long, Hash>();
            RecentBlockHeightAndHashMappings = new ReadOnlyDictionary<long, Hash>(_recentBlockHeightAndHashMappings);
            _preLibBlockHeightAndHashMappings = new ConcurrentDictionary<long, Hash>();
            PreLibBlockHeightAndHashMappings = new ReadOnlyDictionary<long, Hash>(_preLibBlockHeightAndHashMappings);

            Logger = NullLogger<GrpcPeerPool>.Instance;
        }

        public async Task<bool> AddPeerAsync(string address)
        {
            if (FindPeerByAddress(address) != null)
                return false;

            return await DialAsync(address);
        }
        
        private async Task<bool> DialAsync(string ipAddress)
        {
            Logger.LogTrace($"Attempting to reach {ipAddress}.");

            Channel channel = new Channel(ipAddress, ChannelCredentials.Insecure, new List<ChannelOption>
            {
                new ChannelOption(ChannelOptions.MaxSendMessageLength, GrpcConstants.DefaultMaxSendMessageLength),
                new ChannelOption(ChannelOptions.MaxReceiveMessageLength, GrpcConstants.DefaultMaxReceiveMessageLength)
            });
            
            var client = new PeerService.PeerServiceClient(channel
                .Intercept(metadata =>
                    {
                        metadata.Add(GrpcConstants.PubkeyMetadataKey, AsyncHelper.RunSync(() => _accountService.GetPublicKeyAsync()).ToHex());
                        return metadata;
                    })
                .Intercept(new RetryInterceptor()));
            
            var hsk = await BuildHandshakeAsync();

            if (channel.State == ChannelState.TransientFailure)
            {
                // if failing give it some time to recover
                await channel.TryWaitForStateChangedAsync(channel.State,
                    DateTime.UtcNow.AddSeconds(_networkOptions.PeerDialTimeoutInMilliSeconds));
            }

            ConnectReply connectReply;
            
            try
            {
                Metadata data = new Metadata
                {
                    {GrpcConstants.TimeoutMetadataKey, _networkOptions.PeerDialTimeoutInMilliSeconds.ToString()}
                };
                connectReply = await client.ConnectAsync(hsk, data);
            }
            catch (AggregateException e)
            {
                await channel.ShutdownAsync();
                Logger.LogError(e, $"Could not connect to {ipAddress}.");
                return false;
            }

            // todo refactor so that connect returns the handshake and we'll check here 
            // todo if not correct we kill the channel.
            if (connectReply?.Handshake?.HskData == null || connectReply.Err != AuthError.None)
            {
                Logger.LogWarning($"Incorrect handshake for {ipAddress}, {connectReply?.Err}.");
                await channel.ShutdownAsync();
                return false;
            }

            var pubKey = connectReply.Handshake.HskData.PublicKey.ToHex();
            
            var connectionInfo = new GrpcPeerInfo
            {
                PublicKey = pubKey,
                PeerIpAddress = ipAddress,
                ProtocolVersion = connectReply.Handshake.HskData.Version,
                ConnectionTime = TimestampHelper.GetUtcNow().Seconds,
                StartHeight = connectReply.Handshake.Header.Height,
                IsInbound = false
            };

            var peer = new GrpcPeer(channel, client, connectionInfo);

            if (!_authenticatedPeers.TryAdd(pubKey, peer))
            {
                Logger.LogWarning($"Peer {pubKey} is already in list.");
                await channel.ShutdownAsync();
                return false;
            }

            peer.DisconnectionEvent += PeerOnDisconnectionEvent;
            
            Logger.LogTrace($"Connected to {peer} -- height {peer.StartHeight}.");

            _ = EventBus.PublishAsync(new AnnouncementReceivedEventData(new PeerNewBlockAnnouncement
            {
                BlockHash = connectReply.Handshake.Header.GetHash(),
                BlockHeight = connectReply.Handshake.Header.Height
            }, pubKey));

            return true;
        }

        public List<IPeer> GetPeers(bool includeFailing = false)
        {
            var peers = _authenticatedPeers.Select(p => p.Value);

            if (!includeFailing)
                peers = peers.Where(p => p.IsReady);

            return peers.Select(p => p as IPeer).ToList();
        }

        public IPeer FindPeerByAddress(string peerAddress)
        {
            return _authenticatedPeers
                .Where(p => p.Value.PeerIpAddress == peerAddress)
                .Select(p => p.Value)
                .FirstOrDefault();
        }

        public IPeer FindPeerByPublicKey(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
                return null;
            
            _authenticatedPeers.TryGetValue(publicKey, out GrpcPeer p);
            
            return p;
        }

        public IPeer GetBestPeer()
        {
            return GetPeers().FirstOrDefault(p => p.IsBest);
        }

        public bool AddPeer(IPeer peer)
        {
            if (!(peer is GrpcPeer p))
                return false;
            
            string localPubKey = AsyncHelper.RunSync(_accountService.GetPublicKeyAsync).ToHex();

            if (peer.PubKey == localPubKey)
                throw new InvalidOperationException($"Connection to self detected {peer.PubKey} ({peer.PeerIpAddress})");

            if (!_authenticatedPeers.TryAdd(p.PubKey, p))
            {
                Logger.LogWarning($"Could not add peer {peer.PubKey} ({peer.PeerIpAddress})");
                return false;
            }

            p.DisconnectionEvent += PeerOnDisconnectionEvent;

            return true;
        }

        public async Task<Handshake> GetHandshakeAsync()
        {
            return await BuildHandshakeAsync();
        }

        private async Task<Handshake> BuildHandshakeAsync()
        {
            var nd = new HandshakeData
            {
                ListeningPort = _networkOptions.ListeningPort,
                PublicKey = ByteString.CopyFrom(await _accountService.GetPublicKeyAsync()),
                Version = KernelConstants.ProtocolVersion,
                ChainId = _blockchainService.GetChainId()
            };

            byte[] sig = await _accountService.SignAsync(Hash.FromMessage(nd).ToByteArray());

            var hsk = new Handshake
            {
                HskData = nd,
                Signature = ByteString.CopyFrom(sig),
                Header = await _blockchainService.GetBestChainLastBlockHeaderAsync()
            };

            return hsk;
        }

        public async Task<bool> RemovePeerByAddressAsync(string address)
        {
            var peer = _authenticatedPeers.FirstOrDefault(p => p.Value.PeerIpAddress == address).Value;

            if (peer != null) 
                return await RemovePeerAsync(peer.PubKey, true) != null;
            
            Logger.LogWarning($"Could not find peer {address}.");
            
            return false;
        }
        
        private async void PeerOnDisconnectionEvent(object sender, EventArgs e)
        {
            if (sender is GrpcPeer p)
                await RemovePeerAsync(p.PubKey, false);
        }

        public async Task<IPeer> RemovePeerAsync(string publicKey, bool sendDisconnect)
        {
            if (_authenticatedPeers.TryRemove(publicKey, out GrpcPeer removed))
            {
                removed.DisconnectionEvent -= PeerOnDisconnectionEvent;
                
                if (sendDisconnect)
                {
                    try
                    {
                        await removed.SendDisconnectAsync();
                    }
                    catch (RpcException e)
                    {
                        Logger.LogError(e, $"Error sending disconnect to peer {removed}.");
                    }
                }
                
                await removed.StopAsync();
                
                Logger.LogDebug($"Removed peer {removed}");
            }
            else
            {
                Logger.LogDebug($"Could not find {publicKey}");
            }

            return removed;
        }
        
        public void AddRecentBlockHeightAndHash(long blockHeight,Hash blockHash, bool hasFork)
        {
            if (hasFork)
            {
                _recentBlockHeightAndHashMappings.Clear();
                return;
            }
            _recentBlockHeightAndHashMappings[blockHeight] = blockHash;
            while (_recentBlockHeightAndHashMappings.Count > 10)
            {
                _recentBlockHeightAndHashMappings.TryRemove(_recentBlockHeightAndHashMappings.Keys.Min(), out _);
            }
        }
        
        public void AddPreLibBlockHeightAndHash(long blockHeight,Hash blockHash)
        {
            _preLibBlockHeightAndHashMappings[blockHeight] = blockHash;
            while (_preLibBlockHeightAndHashMappings.Count > 10)
            {
                _preLibBlockHeightAndHashMappings.TryRemove(_preLibBlockHeightAndHashMappings.Keys.Min(), out _);
            }
        }
    }
}