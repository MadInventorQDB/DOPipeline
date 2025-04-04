using System;

namespace DOPipeline.Logging
{
    public interface IPipelineLogger : IDisposable
    {
        void Log(string message);
    }
}