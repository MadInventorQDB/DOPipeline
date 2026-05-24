using System;
using System.IO;

namespace DOPipeline.Logging
{
    public class FileConsoleLogger : IPipelineLogger, IProgressLogger
    {
        private readonly string _logFilePath;
        private readonly StreamWriter _streamWriter;
        private static readonly object _lock = new object(); // For thread safety on file write
        private bool _progressActive;

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
                var fileStream = new FileStream(
                    _logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                _streamWriter = new StreamWriter(fileStream) { AutoFlush = true };
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

            lock (_lock)
            {
                ClearProgressLine();
                Console.WriteLine(logEntry);

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

        public void ReportProgress(string message)
        {
            lock (_lock)
            {
                if (Console.IsOutputRedirected)
                {
                    return;
                }

                var width = Math.Max(20, Console.WindowWidth - 1);
                var trimmed = message.Length > width ? message.Substring(0, width) : message;
                Console.Write("\r" + trimmed.PadRight(width));
                _progressActive = true;
            }
        }

        public void Dispose()
        {
            Log("--- Logger Shutting Down ---");
            _streamWriter.Dispose();
            GC.SuppressFinalize(this);
        }

        private void ClearProgressLine()
        {
            if (!_progressActive || Console.IsOutputRedirected)
            {
                return;
            }

            var width = Math.Max(20, Console.WindowWidth - 1);
            Console.Write("\r" + new string(' ', width) + "\r");
            _progressActive = false;
        }
    }
}
