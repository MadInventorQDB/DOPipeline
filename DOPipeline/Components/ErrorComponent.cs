namespace DOPipeline.Components
{
    public class ErrorComponent : IComponent
    {
        // Kept as execution-scope scratch data. It is never serialized as
        // durable workflow state, but it lets pipeline boundaries preserve the
        // initiating exception instead of manufacturing a replacement.
        public Exception? Exception { get; set; }

        public string? ErrorMessage { get; set; }

        public string? Stage { get; set; }

        public string? ExceptionType { get; set; }

        public bool IsCancellation { get; set; }

        public bool IsFatal { get; set; }
    }
}
