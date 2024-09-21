
using Xunit;
using DOPipeline.Components;

namespace DOPipeline.Test.Components
{
    public class ErrorComponentTests
    {
        [Fact]
        public void CanSetAndGetErrorMessage()
        {
            // Arrange
            var errorComponent = new ErrorComponent();
            var errorMessage = "Test error message";

            // Act
            errorComponent.ErrorMessage = errorMessage;

            // Assert
            Assert.Equal(errorMessage, errorComponent.ErrorMessage);
        }
    }
}
