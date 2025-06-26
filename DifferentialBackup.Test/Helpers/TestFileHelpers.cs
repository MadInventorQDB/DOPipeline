using System.IO;

namespace DifferentialBackup.Test.Helpers
{
    public static class TestFileHelpers
    {
        public static string ReadAllTextShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }
    }
}
