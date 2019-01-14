using System;
using System.Collections.Generic;
using AElf.Kernel;
using AElf.Network.Connection;
using AElf.Network.Data;

namespace AElf.Network.Peers
{
    public interface IPeer : IDisposable
    {
        event EventHandler MessageReceived;
        event EventHandler PeerDisconnected;
        event EventHandler AuthFinished;

        bool IsDisposed { get; }
        
        string IpAddress { get; }
        ushort Port { get; }

        bool IsAuthentified { get; }
        bool IsBp { get; }
        int KnownHeight { get; }
        
        bool IsSyncingHistory { get; }
        bool IsSyncingAnnounced { get; }
        int CurrentlyRequestedHeight { get; }
        Announce SyncedAnnouncement { get; }
        bool AnyStashed { get; }
        bool IsSyncing { get; }
        bool IsDownloadingBranch { get; }
        List<IBlock> FinishBranchDownload(IBlock block);
        void StartDownloadBranch();
        
        byte[] LastRequested { get; }

        bool Start();
        
        NodeData DistantNodeData { get; }
        string DistantNodeAddress { get; }
        void EnqueueOutgoing(Message msg, Action<Message> successCallback = null);

        void ResetSync();
        
        void StashAnnouncement(Announce announce);
        Announce GetLowestAnnouncement();

        int SyncTarget { get; }

        void SyncToHeight(int start, int target);
        bool SyncNextHistory();
        byte[] SyncNextAnnouncement();

        void AddBranchedBlock(IBlock block);

        void RequestHeaders(int headerIndex, int headerRequestCount);
        void CleanAnnouncements(int blockHeight);
        void CleanAnnouncement(byte[] blockId);
    }
}