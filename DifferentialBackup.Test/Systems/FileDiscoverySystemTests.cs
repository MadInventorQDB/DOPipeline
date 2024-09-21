
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.IO;
using DifferentialBackup.Components;
using System.Linq;

namespace DifferentialBackup.Test.Systems
{
    public class FileDiscoverySystemTests
    {
        [Fact]
        public void Execute_FindsAllFilesInDirectory()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FileDiscoveryTest");
            Directory.CreateDirectory(tempDir);

            var file1 = Path.Combine(tempDir, "file1.txt");
            var file2 = Path.Combine(tempDir, "file2.txt");
            File.WriteAllText(file1, "Content1");
            File.WriteAllText(file2, "Content2");

            var system = new FileDiscoverySystem(tempDir);
            var storage = new ComponentStorage();
            var entity = new Entity();

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);

            var entities = storage.GetAllEntities();
            var filePaths = entities.Select(e => storage.GetComponent<FilePathComponent>(e)?.FilePath)
                                    .Where(fp => fp != null)
                                    .ToList();

            Assert.Contains(file1, filePaths);
            Assert.Contains(file2, filePaths);

            File.Delete(file1);
            File.Delete(file2);
            Directory.Delete(tempDir);
        }
    }
}
