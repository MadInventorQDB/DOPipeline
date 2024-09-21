
using Xunit;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Utilities
{
    public class ResultTests
    {
        [Fact]
        public void Success_ReturnsSuccessResult()
        {
            // Act
            var result = Result.Success();

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Null(result.ErrorMessage);
        }

        [Fact]
        public void Fail_ReturnsFailureResultWithErrorMessage()
        {
            // Arrange
            var errorMessage = "An error occurred";

            // Act
            var result = Result.Fail(errorMessage);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(errorMessage, result.ErrorMessage);
        }
    }
}
