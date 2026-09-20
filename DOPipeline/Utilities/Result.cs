namespace DOPipeline.Utilities
{
    public class Result
    {
        public bool IsSuccess { get; }
        public string? ErrorMessage { get; }

        public Exception? Exception { get; }

        public bool IsCancellation { get; }

        protected Result(
            bool isSuccess,
            string? errorMessage = "",
            Exception? exception = null,
            bool isCancellation = false)
        {
            IsSuccess = isSuccess;
            ErrorMessage = errorMessage;
            Exception = exception;
            IsCancellation = isCancellation;
        }

        public static Result Success() => new Result(true, null);

        public static Result Fail(string? errorMessage) => new Result(false, errorMessage);

        public static Result Fail(string? errorMessage, Exception exception)
        {
            return new Result(false, errorMessage ?? exception.Message, exception);
        }

        public static Result Cancelled(string? errorMessage = "Operation cancelled.")
        {
            return new Result(false, errorMessage, isCancellation: true);
        }
    }
}
