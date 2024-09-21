
using Xunit;
using DifferentialBackup.Utilities;
using System.IO;

namespace DifferentialBackup.Test.Utilities
{
    public class HashUtilityTests
    {
        [Fact]
        public void ComputeSHA256_ValidFile_ReturnsCorrectHash()
        {
            // Arrange
            var testFilePath = Path.Combine(Path.GetTempPath(), "testfile.txt");
            var testContent = "Hello, World!";
            File.WriteAllText(testFilePath, testContent);

            // Compute expected hash
            string expectedHash;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                using (var stream = File.OpenRead(testFilePath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    expectedHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }

            // Act
            var actualHash = HashUtility.ComputeSHA256(testFilePath);

            // Assert
            Assert.Equal(expectedHash, actualHash);

            // Clean up
            File.Delete(testFilePath);
        }

        [Fact]
        public void ComputeSHA256_NonExistentFile_ReturnsNull()
        {
            var testFilePath = Path.Combine(Path.GetTempPath(), "nonexistentfile.txt");

            var result = HashUtility.ComputeSHA256(testFilePath);

            Assert.Null(result);
        }

        [Fact]
        public void ComputeSHA256_NullFilePath_ReturnsNull()
        {
            var result = HashUtility.ComputeSHA256(null);

            Assert.Null(result);
        }
    }
}
