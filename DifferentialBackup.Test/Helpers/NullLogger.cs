using DOPipeline.Logging; // Make sure this using is present
using System;

// Adjust namespace to match the new location
namespace DifferentialBackup.Test.Helpers
{
    /// <summary>
    /// A dummy logger implementation that performs no actions.
    /// Useful for satisfying IPipelineLogger dependencies in unit tests
    /// where logging side effects are not desired or tested.
    /// </summary>
    public class NullLogger : IPipelineLogger
    {
        // Get a shared instance to avoid repeated allocations in tests
        public static readonly NullLogger Instance = new NullLogger();

        /// <summary>
        /// Does nothing.
        /// </summary>
        /// <param name="message">The message (ignored).</param>
        public void Log(string message)
        {
            // Intentionally empty
        }

        /// <summary>
        /// Does nothing.
        /// </summary>
        public void Dispose()
        {
            // No resources to dispose
            GC.SuppressFinalize(this);
        }
    }
}