using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

/// <summary>
/// What the pool does with a per-test coverage capture so the mutation results that follow can be trusted.
/// </summary>
[TestClass]
public class MicrosoftTestPlatformRunnerPoolCoverageTrustTests : TestBase
{
    private const string Assembly = "assembly.dll";

    private readonly List<TestableRunner> _runners = [];

    [TestMethod]
    public void CaptureCoverage_CreditsMutantsCoveredOncePerProcess_ToEveryTestOfTheAssembly()
    {
        // test-1 is the first on its host: it starts what the host caches (mutant 7) and runs its own code (mutant 1)
        using var pool = CreatePool(["test-1", "test-2"], (runner, testId) =>
        {
            if (testId == "test-1")
            {
                var coverage = runner.OncePerProcessCoverageOf(Assembly);
                coverage.Repeated(coverage.FirstSeenOn(runner, [1, 7]), [1]);
                return [1, 7];
            }

            return [2];
        });

        var coverage = pool.CaptureCoverage(new Mock<IProjectAndTests>().Object).ToList();

        // test-2 relies on what mutant 7's code set up without running it, so it has to assess that mutant too
        coverage.Single(c => c.TestId == "test-2").MutationsCovered.ShouldBe([2, 7], ignoreOrder: true);
        coverage.Single(c => c.TestId == "test-1").MutationsCovered.ShouldBe([1, 7], ignoreOrder: true);
    }

    [TestMethod]
    public void CaptureCoverage_KeepsTestsThatFailOnAFreshHostWithNoMutationActive_FromKillingMutants()
    {
        using var pool = CreatePool(["test-1", "test-2", "test-3"], FailsDuringCapture("test-2"),
            failingOnFreshHost: tests => tests.ToList());

        var coverage = pool.CaptureCoverage(new Mock<IProjectAndTests>().Object).ToList();

        _runners.ShouldAllBe(runner => runner.TestsFailingWithoutMutation.SetEquals(new[] { "test-2" }));
        coverage.ShouldAllBe(c => c.Confidence == CoverageConfidence.Normal);
    }

    [TestMethod]
    public void CaptureCoverage_RunsATestThatOnlyFailsOnAWarmHost_AgainstEveryMutant()
    {
        // test-2 passes when its host is fresh, as it is when a mutant is tested, so it can still kill mutants.
        // Its run on the warm host ended early, so what it covered there is not all it covers.
        using var pool = CreatePool(["test-1", "test-2", "test-3"], FailsDuringCapture("test-2"),
            failingOnFreshHost: _ => []);

        var coverage = pool.CaptureCoverage(new Mock<IProjectAndTests>().Object).ToList();

        var unreliable = coverage.Single(c => c.TestId == "test-2");
        unreliable.Confidence.ShouldBe(CoverageConfidence.Dubious);
        unreliable.MutationsCovered.ShouldBe([1]);
        coverage.Where(c => c.TestId != "test-2").ShouldAllBe(c => c.Confidence == CoverageConfidence.Normal);
        _runners.ShouldAllBe(runner => runner.TestsFailingWithoutMutation.Count == 0);
    }

    [TestMethod]
    public void CaptureCoverage_Stops_WhenMostTestsFailOnAFreshHostWithNoMutationActive()
    {
        using var pool = CreatePool(["test-1", "test-2", "test-3"], FailsDuringCapture("test-1", "test-2"),
            failingOnFreshHost: tests => tests.ToList());

        var failure = Should.Throw<GeneralStrykerException>(() => pool.CaptureCoverage(new Mock<IProjectAndTests>().Object));

        failure.Message.ShouldContain("2 of 3 tests pass on the original assembly and fail on the mutated one");
    }

    private static Func<TestableRunner, string, int[]> FailsDuringCapture(params string[] failingTests) => (runner, testId) =>
    {
        if (failingTests.Contains(testId))
        {
            runner.RecordTestFailedDuringCoverage(testId);
        }

        return [1];
    };

    /// <summary>
    /// A pool of one runner over the given tests of one assembly.
    /// </summary>
    /// <param name="testIds">the tests of the assembly</param>
    /// <param name="capture">gives the mutants a test covers when its coverage is captured on a reused host</param>
    /// <param name="failingOnFreshHost">gives the tests, among the ones asked about, that fail on a fresh host with no mutation active</param>
    private MicrosoftTestPlatformRunnerPool CreatePool(
        IReadOnlyList<string> testIds,
        Func<TestableRunner, string, int[]> capture,
        Func<IReadOnlySet<string>, IReadOnlyCollection<string>>? failingOnFreshHost = null)
    {
        var options = new Mock<IStrykerOptions>();
        options.Setup(x => x.Concurrency).Returns(1);
        options.Setup(x => x.OptimizationMode).Returns(OptimizationModes.CoverageBasedTest);

        var runnerFactory = new Mock<ISingleRunnerFactory>();
        runnerFactory.Setup(x => x.CreateRunner(
                It.IsAny<int>(),
                It.IsAny<Dictionary<string, List<TestNode>>>(),
                It.IsAny<Dictionary<string, MtpTestDescription>>(),
                It.IsAny<TestSet>(),
                It.IsAny<object>(),
                It.IsAny<ILogger>(),
                It.IsAny<IStrykerOptions>()))
            .Returns<int, Dictionary<string, List<TestNode>>, Dictionary<string, MtpTestDescription>, TestSet, object, ILogger, IStrykerOptions>(
                (id, testsByAssembly, testDescriptions, testSet, discoveryLock, _, _) =>
                {
                    var tests = testIds.Select(testId => new TestNode(testId, testId, "test", "discovered")).ToList();
                    testsByAssembly[Assembly] = tests;
                    foreach (var test in tests)
                    {
                        var description = new MtpTestDescription(test);
                        testDescriptions[test.Uid] = description;
                        testSet.RegisterTest(description.Description);
                    }

                    TestableRunner runner = null!;
                    runner = new TestableRunner(id, testsByAssembly, testDescriptions, testSet, discoveryLock, () => { },
                        coverageHandler: (_, _, testId) => Task.FromResult<ICoverageRunResult>(CoverageRunResult.Create(
                            testId, CoverageConfidence.Normal, capture(runner, testId), Array.Empty<int>(), Array.Empty<int>())),
                        failingOnFreshHost: failingOnFreshHost);
                    _runners.Add(runner);
                    return runner;
                });

        return new MicrosoftTestPlatformRunnerPool(options.Object, NullLogger.Instance, runnerFactory.Object);
    }
}
