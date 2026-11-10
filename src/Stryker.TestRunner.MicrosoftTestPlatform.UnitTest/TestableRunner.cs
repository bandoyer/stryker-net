using Microsoft.Extensions.Logging.Abstractions;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

internal class TestableRunner : MicrosoftTestingPlatformRunner
{
    private readonly Action _onDispose;
    private readonly Func<string, TestNode, string, Task<ICoverageRunResult>>? _coverageHandler;
    private readonly Func<string, TestNode, string, Task<ICoverageRunResult>>? _isolatedCoverageHandler;
    private readonly Func<IReadOnlySet<string>, IReadOnlyCollection<string>>? _failingOnFreshHost;

    public TestableRunner(int id, Action onDispose)
        : base(id, new Dictionary<string, List<TestNode>>(),
                new Dictionary<string, MtpTestDescription>(),
                new TestSet(),
                new object(),
                NullLogger.Instance)
    {
        _onDispose = onDispose;
    }

    public TestableRunner(
        int id,
        Dictionary<string, List<TestNode>> testsByAssembly,
        Dictionary<string, MtpTestDescription> testDescriptions,
        TestSet testSet,
        object discoveryLock,
        Action onDispose,
        Func<string, TestNode, string, Task<ICoverageRunResult>>? coverageHandler = null,
        Func<string, TestNode, string, Task<ICoverageRunResult>>? isolatedCoverageHandler = null,
        Func<IReadOnlySet<string>, IReadOnlyCollection<string>>? failingOnFreshHost = null)
        : base(id, testsByAssembly, testDescriptions, testSet, discoveryLock, NullLogger.Instance)
    {
        _onDispose = onDispose;
        _coverageHandler = coverageHandler;
        _isolatedCoverageHandler = isolatedCoverageHandler;
        _failingOnFreshHost = failingOnFreshHost;
    }

    /// <summary>
    /// Stands in for running the tests on a fresh host with no mutation active. Every test passes there by default.
    /// </summary>
    internal override Task<IReadOnlyCollection<string>> FindTestsFailingOnFreshHostAsync(string assembly, IReadOnlySet<string> tests) =>
        Task.FromResult<IReadOnlyCollection<string>>(_failingOnFreshHost?.Invoke(tests) ?? []);

    internal override async Task<ICoverageRunResult> RunSingleTestForCoverageInReusedProcessAsync(
        string assembly, TestNode test, string testId)
    {
        if (_coverageHandler is not null)
        {
            try
            {
                return await _coverageHandler(assembly, test, testId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return CoverageRunResult.Create(testId, CoverageConfidence.Dubious,
                    Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
            }
        }

        return CoverageRunResult.Create(testId, CoverageConfidence.Normal,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
    }

    internal override async Task<ICoverageRunResult> RunSingleTestForCoverageInIsolatedProcessAsync(
        string assembly, TestNode test, string testId)
    {
        if (_isolatedCoverageHandler is not null)
        {
            try
            {
                return await _isolatedCoverageHandler(assembly, test, testId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Mirrors the real runner: a failing isolated capture degrades to Dubious rather than
                // tearing down the whole coverage phase.
                return CoverageRunResult.Create(testId, CoverageConfidence.Dubious,
                    Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
            }
        }

        return CoverageRunResult.Create(testId, CoverageConfidence.Exact,
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>());
    }

    public override void Dispose(bool disposing)
    {
        _onDispose?.Invoke();
        base.Dispose(disposing);
    }
}