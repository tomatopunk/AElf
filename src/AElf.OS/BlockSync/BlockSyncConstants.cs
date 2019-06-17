namespace AElf.OS.BlockSync
{
    public class BlockSyncConstants
    {
        public const string BlockSyncQueueName = "BlockSyncQueue";
        public const string BlockSyncAttachQueueName = "BlockSyncAttachQueue";
        public const int BlockSyncAnnouncementAgeLimit = 4000;
        public const int BlockSyncAttachBlockAgeLimit = 2000;
        public const int BlockSyncJobAgeLimit = 500;
    }
}