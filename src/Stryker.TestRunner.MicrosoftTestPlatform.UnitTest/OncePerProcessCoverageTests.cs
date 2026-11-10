using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class OncePerProcessCoverageTests
{
    private readonly object _host = new();

    [TestMethod]
    public void FirstSeenOn_GivesOnlyTheMutantsNoEarlierTestCoveredOnTheHost()
    {
        var coverage = new OncePerProcessCoverage();
        coverage.Repeated(coverage.FirstSeenOn(_host, [1, 2]), [1, 2]);

        coverage.FirstSeenOn(_host, [2, 3]).ShouldBe([3]);
    }

    [TestMethod]
    public void Repeated_KeepsTheMutantsTheSecondRunDidNotCover()
    {
        var coverage = new OncePerProcessCoverage();

        // the first test starts what the host caches (mutant 7) and runs its own code (mutant 1) both times
        coverage.Repeated(coverage.FirstSeenOn(_host, [1, 7]), [1]);

        coverage.Mutants.ShouldBe([7]);
    }

    [TestMethod]
    public void Repeated_CountsEveryMutant_WhenTheSecondRunGaveNoCoverage()
    {
        var coverage = new OncePerProcessCoverage();

        coverage.Repeated(coverage.FirstSeenOn(_host, [1, 7]), coveredAgain: null);

        coverage.Mutants.ShouldBe([1, 7], ignoreOrder: true);
    }

    [TestMethod]
    public void FirstSeenOn_StartsOver_OnANewHost()
    {
        var coverage = new OncePerProcessCoverage();
        coverage.Repeated(coverage.FirstSeenOn(_host, [7]), []);

        // a new host starts what it caches again, so the first test on it covers mutant 7 again
        coverage.FirstSeenOn(new object(), [7]).ShouldBe([7]);
    }

    [TestMethod]
    public void FirstSeenOn_AsksForASecondRunAgain_WhenTheFirstOneWasNeverRecorded()
    {
        var coverage = new OncePerProcessCoverage();

        // the host went away before the second run, so nothing is known about mutant 7 yet
        coverage.FirstSeenOn(_host, [7]);

        coverage.FirstSeenOn(_host, [7]).ShouldBe([7]);
        coverage.Mutants.ShouldBeEmpty();
    }
}
