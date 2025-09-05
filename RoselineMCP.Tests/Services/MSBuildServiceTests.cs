using FakeItEasy;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class MSBuildServiceTests
{
    private readonly MSBuildService _service;
    private readonly ILogger<MSBuildService> _logger;

    public MSBuildServiceTests()
    {
        _logger = A.Fake<ILogger<MSBuildService>>();
        _service = new MSBuildService(_logger);
    }

    public class EnsureMSBuildRegisteredTests : MSBuildServiceTests
    {
        [Fact]
        public void Should_Not_Throw_When_Registering()
        {
            // Act & Assert
            Should.NotThrow(() =>
            {
                _service.EnsureMSBuildRegistered();
            });
        }

        [Fact]
        public void Should_Handle_Multiple_Calls()
        {
            // Act & Assert
            Should.NotThrow(() =>
            {
                _service.EnsureMSBuildRegistered();
                _service.EnsureMSBuildRegistered(); // Second call should not throw
            });
        }
    }

    public class CreateWorkspaceTests : MSBuildServiceTests
    {
        [Fact]
        public void Should_Create_Workspace()
        {
            // Arrange
            _service.EnsureMSBuildRegistered();

            // Act
            var workspace = _service.CreateWorkspace();

            // Assert
            workspace.ShouldNotBeNull();
            workspace.ShouldBeOfType<MSBuildWorkspace>();
        }

        [Fact]
        public void Should_Create_Multiple_Workspaces()
        {
            // Arrange
            _service.EnsureMSBuildRegistered();

            // Act
            var workspace1 = _service.CreateWorkspace();
            var workspace2 = _service.CreateWorkspace();

            // Assert
            workspace1.ShouldNotBeNull();
            workspace2.ShouldNotBeNull();
            workspace1.ShouldNotBe(workspace2);
        }
    }


    public class ThreadSafetyTests : MSBuildServiceTests
    {
        [Fact]
        public void Should_Handle_Multiple_Service_Instances()
        {
            // Arrange
            var logger1 = A.Fake<ILogger<MSBuildService>>();
            var logger2 = A.Fake<ILogger<MSBuildService>>();
            var service1 = new MSBuildService(logger1);
            var service2 = new MSBuildService(logger2);

            // Act & Assert
            Should.NotThrow(() =>
            {
                service1.EnsureMSBuildRegistered();
                service2.EnsureMSBuildRegistered();
            });
        }

        [Fact]
        public void Should_Be_Thread_Safe()
        {
            // Arrange
            var tasks = new List<Task>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    var logger = A.Fake<ILogger<MSBuildService>>();
                    var service = new MSBuildService(logger);
                    service.EnsureMSBuildRegistered();
                    service.CreateWorkspace();
                }));
            }

            // Assert
            Should.NotThrow(async () => await Task.WhenAll(tasks));
        }
    }
}