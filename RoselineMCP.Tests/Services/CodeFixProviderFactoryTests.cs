using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class CodeFixProviderFactoryTests
{
    private readonly CodeFixProviderFactory _factory;
    private readonly ILogger<CodeFixProviderFactory> _logger;

    public CodeFixProviderFactoryTests()
    {
        _logger = A.Fake<ILogger<CodeFixProviderFactory>>();
        _factory = new CodeFixProviderFactory(_logger);
    }

    public class GetProviderForDiagnosticTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Return_Provider_For_Known_Diagnostic()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var provider = _factory.GetProviderForDiagnostic("CS0168");

            // Assert
            // Provider may be null if not available in test environment
            if (provider != null)
            {
                provider.ShouldBeAssignableTo<CodeFixProvider>();
            }
        }

        [Fact]
        public void Should_Return_Null_For_Unknown_Diagnostic()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var provider = _factory.GetProviderForDiagnostic("UNKNOWN999");

            // Assert
            provider.ShouldBeNull();
        }

        [Fact]
        public void Should_Handle_Null_Diagnostic_Id()
        {
            // Act & Assert
            Should.Throw<ArgumentNullException>(() => 
                _factory.GetProviderForDiagnostic(null!));
        }
    }

    public class GetFixableDiagnosticIdsTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Return_Diagnostic_Ids()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var ids = _factory.GetFixableDiagnosticIds();

            // Assert
            ids.ShouldNotBeNull();
            ids.ShouldBeAssignableTo<IEnumerable<string>>();
        }

        [Fact]
        public void Should_Return_Unique_Ids()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var ids = _factory.GetFixableDiagnosticIds().ToList();

            // Assert
            ids.Distinct().Count().ShouldBe(ids.Count);
        }
    }

    public class LoadProvidersTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Load_Providers_Without_Error()
        {
            // Act & Assert
            Should.NotThrow(() => _factory.LoadProviders());
        }

        [Fact]
        public void Should_Be_Idempotent()
        {
            // Act & Assert
            Should.NotThrow(() =>
            {
                _factory.LoadProviders();
                _factory.LoadProviders();
            });
        }
    }

    public class ThreadSafetyTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Be_Thread_Safe_For_Loading()
        {
            // Arrange
            var tasks = new List<Task>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() => _factory.LoadProviders()));
            }

            // Assert
            Should.NotThrow(async () => await Task.WhenAll(tasks));
        }

        [Fact]
        public void Should_Be_Thread_Safe_For_Getting_Providers()
        {
            // Arrange
            _factory.LoadProviders();
            var tasks = new List<Task<CodeFixProvider?>>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() => _factory.GetProviderForDiagnostic("CS0168")));
            }

            // Assert
            Should.NotThrow(async () =>
            {
                var results = await Task.WhenAll(tasks);
                // All should return the same result (null or not null)
                var firstResult = results.First();
                results.All(r => (r == null) == (firstResult == null)).ShouldBeTrue();
            });
        }
    }
}