using System;
using System.IO;

namespace DOPipeline.Logging
{
    public class FileConsoleLogger : IPipelineLogger
    {
        private readonly string _logFilePath;
        private readonly StreamWriter _streamWriter;
        private static readonly object _lock = new object(); // For thread safety on file write

        public FileConsoleLogger(string logFilePath)
        {
            _logFilePath = logFilePath;

            try
            {
                // Ensure the directory exists
                string? logDirectory = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                // Open the file for appending, create if it doesn't exist
                _streamWriter = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing logger to file '{_logFilePath}': {ex.Message}");
                // Fallback to prevent crash, logging will only go to console if file init fails
                _streamWriter = StreamWriter.Null;
            }
            Log("--- Logger Initialized ---");
        }

        public void Log(string message)
        {
            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

            // Log to console
            Console.WriteLine(logEntry);

            // Log to file (thread-safe)
            lock (_lock)
            {
                try
                {
                    if (_streamWriter != StreamWriter.Null)
                    {
                        _streamWriter.WriteLine(logEntry);
                    }
                }
                catch (Exception ex)
                {
                    // Avoid crashing the application if logging fails
                    Console.WriteLine($"Error writing to log file: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Log("--- Logger Shutting Down ---");
            _streamWriter.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}