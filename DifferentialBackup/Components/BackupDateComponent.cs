using DOPipeline.Components;
using System;

namespace DifferentialBackup.Components
{
    public class BackupDateComponent : IComponent
    {
        public DateTime BackupDate { get; set; }
    }
}
