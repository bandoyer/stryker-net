using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using Stryker.Utilities.Logging;
using static Stryker.Abstractions.Testing.ITestRunner;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Manages a pool of MicrosoftTestPlatformRunner instances to enable parallel mutation testing
/// with isolated environment variables per runner.
/// </summary>
public sealed class MicrosoftTestPlatformRunnerPool : ITestRunner
{
    public Abstractions.Options.TestRunner RunnerType =>
        Abstractions.Options.TestRunner.MicrosoftTestPlatform;

    private readonly AutoResetEvent _runnerAvailableHandler = new(false);
    private readonly ConcurrentBag<MicrosoftTestingPlatformRunner> _availableRunners = new();
    private readonly ConcurrentBag<MicrosoftTestingPlatformRunner> _allRunners = new();
    private bool _disposed;
    private readonly ILogger _logger;
    private readonly int _countOfRunners;
    private readonly TestSet _testSet = new();
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly = new();
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions = new();
    private readonly object _discoveryLock = new();
    private readonly ISingleRunnerFactory _runnerFactory;
    private readonly IStrykerOptions _options;
    private IReadOnlySet<string> _testsFailingInitially = new HashSet<string>();

    public IEnumerable<MicrosoftTestingPlatformRunner> Runners => _availableRunners;

    public MicrosoftTestPlatformRunnerPool(IStrykerOptions options, ILogger? logger = null, ISingleRunnerFactory? runnerFactory = null)
    {
        _logger = logger ?? ApplicationLogging.LoggerFactory.CreateLogger<MicrosoftTestPlatformRunnerPool>();
        _options = options;
        _countOfRunners = Math.Max(1, options.Concurrency);
        _runnerFactory = runnerFactory ?? new DefaultRunnerFactory();
        _logger.LogWarning("The Microsoft Test Platform testrunner is currently in preview. Results should be verified since this feature is still being tested.");

        Initialize();
    }

    public void ResetTestProcesses()
    {
        _logger.LogDebug("Resetting all test server processes in the pool");
        var tasks = _availableRunners.Select(runner => runner.ResetServerAsync());
        Task.WhenAll(tasks).Wait();
        _logger.LogDebug("All test server processes have been reset");
    }

    private void Initialize()
    {
        // Create and initialize all runners in parallel to speed up startup time
        Parallel.For(0, _countOfRunners, (int i, ParallelLoopState _) =>
        {
            var runner = _runnerFactory.CreateRunner(
                i,
                _testsByAssembly,
                _testDescriptions,
                _testSet,
                _discoveryLock,
                _logger,
                _options);
            _availableRunners.Add(runner);
            _allRunners.Add(runner);
            _runnerAvailableHandler.Set();
        });
    }

    public async Task<bool> DiscoverTestsAsync(string assembly)
    {
        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            throw new InputException($"The test project binaries could not be found at '{assembly}'.");
        }

        return await RunThisAsync(runner => runner.DiscoverTestsAsync(assembly)).ConfigureAwait(false);
    }

    public ITestSet GetTests(IProjectAndTests project) => _testSet;

    public async Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        var results = await RunThisAsync(runner => runner.InitialTestAsync(project)).ConfigureAwait(false);
        _testsFailingInitially = results.FailingTests.GetIdentifiers().ToHashSet();

        // reset all test processes after the initial test run
        ResetTestProcesses();

        return results;
    }

    public IEnumerable<ICoverageRunResult> CaptureCoverage(IProjectAndTests project)
    {
        // "perTestInIsolation" (CaptureCoveragePerTest) restarts the test process around every single
        // test so each result can be trusted with CoverageConfidence.Exact; "perTest" (CoverageBasedTest
        // only) reuses a warm process across tests, which is faster but can only ever earn
        // CoverageConfidence.Normal; "all" (SkipUncoveredMutants only) only needs to know which mutants
        // are covered by *some* test, so the cheaper aggregate capture is enough. Mirrors
        // VsTestRunnerPool's routing.
        if (_options.OptimizationMode.HasFlag(OptimizationModes.CaptureCoveragePerTest))
        {
            return CaptureCoveragePerIsolatedTests();
        }

        return _options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest)
            ? CaptureCoverageTestByTest()
            : CaptureCoverageInOneGo(project);
    }

    private IEnumerable<ICoverageRunResult> CaptureCoverageInOneGo(IProjectAndTests project)
    {
        _logger.LogInformation("Starting aggregate coverage capture for MTP runner");

        // Enable coverage mode on all runners
        foreach (var runner in _allRunners)
        {
            runner.SetCoverageMode(true);
        }

        try
        {
            // Run all tests with coverage tracking enabled
            var testResult = RunThisAsync(runner => runner.InitialTestAsync(project)).GetAwaiter().GetResult();

            if (testResult.FailingTests.IsEveryTest)
            {
                _logger.LogWarning("Coverage test run failed: {Message}", testResult.ResultMessage);
            }

            SetAsideTestsFailingWithoutMutation(testResult.FailingTests.GetIdentifiers());

            // Reset test processes to trigger coverage file flush (process exit writes coverage)
            ResetTestProcesses();

            // Aggregate coverage data from all runners
            var allCoveredMutants = new HashSet<int>();
            var allStaticMutants = new HashSet<int>();

            foreach (var runner in _availableRunners)
            {
                var (coveredMutants, staticMutants) = runner.ReadCoverageData();
                foreach (var mutantId in coveredMutants)
                {
                    allCoveredMutants.Add(mutantId);
                }
                foreach (var mutantId in staticMutants)
                {
                    allStaticMutants.Add(mutantId);
                }
            }

            _logger.LogInformation("Aggregate coverage capture complete: {CoveredCount} mutations covered, {StaticCount} static mutations",
                allCoveredMutants.Count, allStaticMutants.Count);

            // For cumulative coverage, we return a single coverage result that applies to all tests
            // Each test is assumed to cover all the mutations that were covered during the full test run
            // Static mutants are marked as such for proper handling during mutation testing
            return _testDescriptions.Values.Select(testDescription =>
                CoverageRunResult.Create(
                    testDescription.Id,
                    CoverageConfidence.Normal,
                    allCoveredMutants,
                    allStaticMutants,
                    []));
        }
        finally
        {
            // Disable coverage mode on all runners for subsequent mutation testing
            foreach (var runner in _availableRunners)
            {
                runner.SetCoverageMode(false);
            }
        }
    }

    /// <summary>
    /// Captures coverage one test at a time (each test's coverage is flushed and reset separately via
    /// the epoch relay in <see cref="MicrosoftTestingPlatformRunner.RunSingleTestForCoverageInReusedProcessAsync"/>),
    /// so each mutant's covering tests can be narrowed down instead of assuming every test covers it.
    /// Tests are distributed across the whole pool for parallelism; a runner keeps its per-assembly
    /// server warm across the tests it is handed rather than restarting it for every test.
    /// </summary>
    private IEnumerable<ICoverageRunResult> CaptureCoverageTestByTest()
    {
        _logger.LogInformation("Starting per-test coverage capture for MTP runner");

        foreach (var runner in _allRunners)
        {
            runner.SetPerTestCoverageMode(true);
        }

        List<(string Assembly, ICoverageRunResult Coverage)> captured;

        try
        {
            var allTests = new List<(string Assembly, TestNode Test, string TestId)>();
            lock (_discoveryLock)
            {
                foreach (var (assembly, tests) in _testsByAssembly)
                {
                    foreach (var test in tests)
                    {
                        if (_testDescriptions.TryGetValue(test.Uid, out var description))
                        {
                            allTests.Add((assembly, test, description.Id));
                        }
                    }
                }
            }

            _logger.LogInformation("Capturing per-test coverage for {TestCount} tests across {AssemblyCount} assemblies",
                allTests.Count, _testsByAssembly.Count);

            var results = new ConcurrentBag<(string Assembly, ICoverageRunResult Coverage)>();

            Parallel.ForEach(allTests, new ParallelOptions { MaxDegreeOfParallelism = _countOfRunners }, testInfo =>
            {
                var result = RunThisAsync(runner =>
                        runner.RunSingleTestForCoverageInReusedProcessAsync(testInfo.Assembly, testInfo.Test, testInfo.TestId))
                    .GetAwaiter().GetResult();
                results.Add((testInfo.Assembly, result));
            });

            _logger.LogInformation("Per-test coverage capture complete: {TestCount} tests captured", results.Count);

            captured = results.ToList();
        }
        finally
        {
            foreach (var runner in _allRunners)
            {
                runner.SetPerTestCoverageMode(false);
            }
        }

        CreditCoverageOncePerProcessToEveryTest(captured);
        return SortOutTestsThatFailedOnAWarmHost(captured);
    }

    /// <summary>
    /// Captures coverage one test at a time, restarting the test host process around each test (see
    /// <see cref="MicrosoftTestingPlatformRunner.RunSingleTestForCoverageInIsolatedProcessAsync"/>) so
    /// no state (static or otherwise) can leak between tests. This lets each result be trusted with
    /// <see cref="CoverageConfidence.Exact"/>, at the cost of a much slower init phase than the
    /// reused-process "perTest" capture in <see cref="CaptureCoverageTestByTest"/>.
    /// </summary>
    private IEnumerable<ICoverageRunResult> CaptureCoveragePerIsolatedTests()
    {
        _logger.LogInformation("Starting per-test-in-isolation coverage capture for MTP runner");

        foreach (var runner in _allRunners)
        {
            runner.SetCoverageMode(true);
        }

        try
        {
            var allTests = new List<(string Assembly, TestNode Test, string TestId)>();
            lock (_discoveryLock)
            {
                foreach (var (assembly, tests) in _testsByAssembly)
                {
                    foreach (var test in tests)
                    {
                        if (_testDescriptions.TryGetValue(test.Uid, out var description))
                        {
                            allTests.Add((assembly, test, description.Id));
                        }
                    }
                }
            }

            _logger.LogInformation("Capturing per-test-in-isolation coverage for {TestCount} tests across {AssemblyCount} assemblies",
                allTests.Count, _testsByAssembly.Count);

            var results = new ConcurrentBag<ICoverageRunResult>();

            Parallel.ForEach(allTests, new ParallelOptions { MaxDegreeOfParallelism = _countOfRunners }, testInfo =>
            {
                var result = RunThisAsync(runner =>
                        runner.RunSingleTestForCoverageInIsolatedProcessAsync(testInfo.Assembly, testInfo.Test, testInfo.TestId))
                    .GetAwaiter().GetResult();
                results.Add(result);
            });

            _logger.LogInformation("Per-test-in-isolation coverage capture complete: {TestCount} tests captured", results.Count);

            SetAsideTestsFailingWithoutMutation(_allRunners.SelectMany(runner => runner.TestsFailedDuringCoverage));

            return results;
        }
        finally
        {
            foreach (var runner in _availableRunners)
            {
                runner.SetCoverageMode(false);
            }
        }
    }

    /// <summary>
    /// Credits the mutants that tests cover once per test host to every test of the assembly, see
    /// <see cref="OncePerProcessCoverage"/>: any of them may rely on what that code set up.
    /// </summary>
    private void CreditCoverageOncePerProcessToEveryTest(IEnumerable<(string Assembly, ICoverageRunResult Coverage)> results)
    {
        foreach (var testsOfAssembly in results.GroupBy(result => result.Assembly))
        {
            var mutants = _allRunners.SelectMany(runner => runner.MutantsCoveredOncePerProcess(testsOfAssembly.Key)).ToHashSet();
            if (mutants.Count == 0)
            {
                continue;
            }

            _logger.LogInformation(
                "{MutantCount} mutants are covered once per test host of {Assembly}. Each will be tested against every test of that assembly.",
                mutants.Count, Path.GetFileName(testsOfAssembly.Key));
            foreach (var (_, coverage) in testsOfAssembly)
            {
                coverage.Merge(CoverageRunResult.Create(coverage.TestId, coverage.Confidence, mutants, [], []));
            }
        }
    }

    /// <summary>
    /// Deals with the tests that passed in the initial run and failed while coverage was captured on a test host that
    /// earlier tests had used. No mutation is active during the capture, so such a test either fails on the mutated
    /// assembly as built, or passes only on a host in the state the whole test run leaves it in. Mutants are tested
    /// on a fresh host, so running the tests once on a fresh host with no mutation active tells which it is.
    /// </summary>
    /// <returns>The coverage to use, where a test that only fails on a warm host is no longer trusted for coverage.</returns>
    private List<ICoverageRunResult> SortOutTestsThatFailedOnAWarmHost(List<(string Assembly, ICoverageRunResult Coverage)> captured)
    {
        var failedOnWarmHost = _allRunners.SelectMany(runner => runner.TestsFailedDuringCoverage)
            .Where(test => !_testsFailingInitially.Contains(test)).ToHashSet();
        if (failedOnWarmHost.Count == 0)
        {
            return captured.Select(result => result.Coverage).ToList();
        }

        var failingOnFreshHost = new HashSet<string>();
        foreach (var testsOfAssembly in captured.Where(result => failedOnWarmHost.Contains(result.Coverage.TestId)).GroupBy(result => result.Assembly))
        {
            var tests = testsOfAssembly.Select(result => result.Coverage.TestId).ToHashSet();
            failingOnFreshHost.UnionWith(
                RunThisAsync(runner => runner.FindTestsFailingOnFreshHostAsync(testsOfAssembly.Key, tests)).GetAwaiter().GetResult());
        }

        SetAsideTestsFailingWithoutMutation(failingOnFreshHost);

        var failedOnWarmHostOnly = failedOnWarmHost.Except(failingOnFreshHost).ToHashSet();
        if (failedOnWarmHostOnly.Count > 0)
        {
            // The run that gave their coverage ended early, so it can not show all they cover. A test with dubious
            // coverage is run against every mutant, which keeps it able to kill the ones it does cover.
            _logger.LogInformation(
                "{TestCount} tests failed while coverage was captured on a reused test host and pass on a fresh one: {Examples}. " +
                "Their coverage can not be trusted, so they will be run against every mutant.",
                failedOnWarmHostOnly.Count, string.Join(", ", NamesOf(failedOnWarmHostOnly)));
        }

        return captured.Select(result => failedOnWarmHostOnly.Contains(result.Coverage.TestId)
            ? WithDubiousConfidence(result.Coverage)
            : result.Coverage).ToList();
    }

    private static ICoverageRunResult WithDubiousConfidence(ICoverageRunResult coverage) =>
        CoverageRunResult.Create(
            coverage.TestId,
            CoverageConfidence.Dubious,
            coverage.MutationsCovered,
            coverage.MutationsCovered.Where(mutant => coverage[mutant].HasFlag(MutationTestingRequirements.Static)),
            []);

    private List<string> NamesOf(IEnumerable<string> tests)
    {
        lock (_discoveryLock)
        {
            return tests.Order().Take(5)
                .Select(test => _testDescriptions.TryGetValue(test, out var description) ? description.Description.Name : test)
                .ToList();
        }
    }

    /// <summary>
    /// Stops the tests that fail on a fresh test host with no mutation active from killing mutants. Their failure
    /// says nothing about a mutant.
    /// </summary>
    /// <exception cref="GeneralStrykerException">More than half of the tests fail that way. The mutated assembly
    /// does not behave like the original then, and every result would be wrong.</exception>
    private void SetAsideTestsFailingWithoutMutation(IEnumerable<string> failedWithoutMutation)
    {
        var failing = failedWithoutMutation.Where(test => !_testsFailingInitially.Contains(test)).ToHashSet();
        if (failing.Count == 0)
        {
            return;
        }

        var examples = NamesOf(failing);
        int testCount;
        lock (_discoveryLock)
        {
            testCount = _testDescriptions.Count;
        }

        if (failing.Count * 2 > testCount)
        {
            throw new GeneralStrykerException(
                $"{failing.Count} of {testCount} tests pass on the original assembly and fail on the mutated one with no mutation active, " +
                $"for example {string.Join(", ", examples)}. The mutated assembly does not behave like the original, so no mutant can be judged. " +
                "Run with --verbosity debug to see why the tests fail.");
        }

        _logger.LogWarning(
            "{FailingCount} tests passed in the initial test run and fail on the mutated assembly with no mutation active: {Examples}. " +
            "They can not kill a mutant, so a mutant only they cover will survive.",
            failing.Count, string.Join(", ", examples));
        foreach (var runner in _allRunners)
        {
            runner.IgnoreFailuresOf(failing);
        }
    }

    public async Task<ITestRunResult> TestMultipleMutantsAsync(
        IProjectAndTests project,
        ITimeoutValueCalculator? timeoutCalc,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update)
    {
        var assemblies = project.GetTestAssemblies();
        if (!assemblies.Any())
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        return await RunThisAsync(runner => runner.TestMultipleMutantsAsync(project, timeoutCalc, mutants, update)).ConfigureAwait(false);
    }

    private async Task<T> RunThisAsync<T>(Func<MicrosoftTestingPlatformRunner, Task<T>> task)
    {
        MicrosoftTestingPlatformRunner? runner;

        // Try to get a runner with a timeout to prevent indefinite blocking
        var attempts = 0;
        const int maxWaitTimeSeconds = 300; // 5 minutes max wait
        const int waitIntervalMs = 1000; // Check every second
        var maxAttempts = maxWaitTimeSeconds * 1000 / waitIntervalMs;

        while (!_availableRunners.TryTake(out runner))
        {
            if (!_runnerAvailableHandler.WaitOne(waitIntervalMs))
            {
                attempts++;
                if (attempts >= maxAttempts)
                {
                    throw new TimeoutException($"Timed out waiting for an available test runner after {maxWaitTimeSeconds} seconds. Available runners: {_availableRunners.Count}, Total runners: {_countOfRunners}");
                }

                if (attempts % 30 == 0) // Log every 30 seconds
                {
                    _logger.LogWarning("Waiting for available test runner... ({Attempts}s elapsed, {Available}/{Total} runners available)",
                        attempts, _availableRunners.Count, _countOfRunners);
                }
            }
        }

        try
        {
            return await task(runner).ConfigureAwait(false);
        }
        finally
        {
            _availableRunners.Add(runner);
            _runnerAvailableHandler.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var runner in _allRunners)
        {
            runner.Dispose();
        }
        _runnerAvailableHandler.Dispose();
    }
}
