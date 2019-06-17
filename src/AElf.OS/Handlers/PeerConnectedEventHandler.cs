using System.Threading.Tasks;
using AElf.OS.BlockSync.Application;
using AElf.OS.Network;
using AElf.OS.Network.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.EventBus;

namespace AElf.OS.Handlers
{
    public class PeerConnectedEventHandler : ILocalEventHandler<AnnouncementReceivedEventData>
    {
        public ILogger<PeerConnectedEventHandler> Logger { get; set; }

        private readonly IBlockSyncService _blockSyncService;
        private readonly NetworkOptions _networkOptions;

        public PeerConnectedEventHandler(IBlockSyncService blockSyncService,
            IOptionsSnapshot<NetworkOptions> networkOptions)
        {
            _blockSyncService = blockSyncService;
            _networkOptions = networkOptions.Value;
            Logger = NullLogger<PeerConnectedEventHandler>.Instance;
        }
        
        public Task HandleEventAsync(AnnouncementReceivedEventData eventData)
        {
            var _ = _blockSyncService.SyncBlockAsync(eventData.Announce.BlockHash, eventData.Announce.BlockHeight,
                _networkOptions.BlockIdRequestCount, eventData.SenderPubKey);
            return Task.CompletedTask;
        }
    }
}