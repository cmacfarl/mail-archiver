namespace MailArchiver.Models
{
    public class BatchOperationOptions
    {
        public const string BatchOperation = "BatchOperation";
        
        public int BatchSize { get; set; } = 20;
        public int PauseBetweenEmailsMs { get; set; } = 100;
        public int PauseBetweenBatchesMs { get; set; } = 500;
        // Number of messages.get requests per Gmail batch HTTP call.
        // Gmail enforces a per-user concurrency limit within a batch; 10 is a safe default.
        public int GmailBatchSize { get; set; } = 10;
        // Pause between metadata (format=METADATA) batch calls in ms.
        // Keeps the lightweight pre-check phase from exhausting the per-minute quota
        // before the heavier RAW fetches start. 200ms = ~5 batches/sec = safe headroom.
        public int GmailMetadataBatchPauseMs { get; set; } = 200;
    }
}
