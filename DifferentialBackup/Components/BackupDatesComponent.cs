using DOPipeline.Components;
using System;
using System.Collections.Generic;

namespace DifferentialBackup.Components
{
    public class BackupDatesComponent : IComponent
    {
        public List<DateTime> Dates { get; set; } = new List<DateTime>();
    }
}
