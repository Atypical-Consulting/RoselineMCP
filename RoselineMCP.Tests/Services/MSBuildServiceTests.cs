using System.Reflection;
using FakeItEasy;
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


    /// <summary>
    /// Tests the instance-selection policy via reflection (the repo's established pattern for
    /// internal helpers): <c>VisualStudioInstance</c> has an internal constructor, so the policy
    /// is a generic internal helper exercised here with a stand-in type.
    /// </summary>
    public class SelectPreferredInstanceTests
    {
        private sealed record FakeInstance(string Name, Version Version);

        private static FakeInstance? Select(params FakeInstance[] instances)
        {
            var method = typeof(MSBuildService)
                .GetMethod("SelectPreferredInstance", BindingFlags.NonPublic | BindingFlags.Static)
                .ShouldNotBeNull()
                .MakeGenericMethod(typeof(FakeInstance));

            return (FakeInstance?)method.Invoke(null, [instances, (Func<FakeInstance, Version>)(i => i.Version)]);
        }

        [Fact]
        public void Should_Pick_Highest_Version_Not_First()
        {
            // Arrange — the newest SDK is deliberately not first in enumeration order
            var older = new FakeInstance(".NET SDK 8", new Version(8, 0, 100));
            var newest = new FakeInstance(".NET SDK 10", new Version(10, 0, 100));
            var middle = new FakeInstance(".NET SDK 9", new Version(9, 0, 200));

            // Act
            var selected = Select(older, newest, middle);

            // Assert
            selected.ShouldBe(newest);
        }

        [Fact]
        public void Should_Return_Null_When_No_Instances()
        {
            // Act & Assert
            Select().ShouldBeNull();
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
#pragma warning disable xUnit1051 // TestContext.Current not available in xUnit 3
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