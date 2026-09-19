namespace DifferentialBackup.Utilities
{
    public sealed class BackupBatchOptions
    {
        public const long DefaultMaxSourceBytesPerPart = 1024L * 1024L * 1024L;
        public const int DefaultMaxFilesPerPart = 5000;
        public const int DefaultTransferQueueCapacity = 2;

        public long MaxSourceBytesPerPart { get; init; } = DefaultMaxSourceBytesPerPart;

        public int MaxFilesPerPart { get; init; } = DefaultMaxFilesPerPart;

        public int TransferQueueCapacity { get; init; } = DefaultTransferQueueCapacity;

        public int CopyBufferSize { get; init; } = 4 * 1024 * 1024;

        public void Validate()
        {
            if (MaxSourceBytesPerPart <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxSourceBytesPerPart));
            }

            if (MaxFilesPerPart <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxFilesPerPart));
            }

            if (TransferQueueCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(TransferQueueCapacity));
            }

            if (CopyBufferSize < 64 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(CopyBufferSize));
            }
        }
    }
}
