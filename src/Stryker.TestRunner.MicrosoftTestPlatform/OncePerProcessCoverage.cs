namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Finds the mutants that tests cover only once in the life of a test host process, however many tests need the
/// code they sit in.
/// </summary>
/// <remarks>
/// Per-test coverage on a reused host credits a mutant to the tests that run its code. Code that runs once per
/// process breaks that: the start of an application that a test library caches for the life of the process, or a
/// lazily built singleton, is run by whichever test reaches it first. Every later test relies on what that code set
/// up without running it, so none of them would be asked to assess its mutants.
/// Running a test a second time on the same host tells the two kinds of coverage apart. What the test covers both
/// times belongs to the test. What it covers the first time only depended on the state of the host.
/// </remarks>
internal sealed class OncePerProcessCoverage
{
    private readonly HashSet<int> _seenOnHost = [];
    private readonly HashSet<int> _coveredOncePerProcess = [];
    private object? _host;

    /// <summary>
    /// The mutants that no test covers a second time on the same host.
    /// </summary>
    public IReadOnlyCollection<int> Mutants => _coveredOncePerProcess;

    /// <summary>
    /// Gives the mutants among <paramref name="covered"/> that no earlier test covered on this host. A test that
    /// covers any of them has to run again, and its second coverage goes to <see cref="Repeated"/>.
    /// </summary>
    /// <param name="host">The test host process the test ran on. A new host starts with nothing seen.</param>
    /// <param name="covered">The mutants the test covered.</param>
    public IReadOnlyCollection<int> FirstSeenOn(object host, IEnumerable<int> covered)
    {
        if (!ReferenceEquals(host, _host))
        {
            _host = host;
            _seenOnHost.Clear();
        }

        return covered.Where(mutant => !_seenOnHost.Contains(mutant)).ToList();
    }

    /// <summary>
    /// Records the second run of a test that covered <paramref name="firstSeen"/> on its first run.
    /// </summary>
    /// <param name="firstSeen">What <see cref="FirstSeenOn"/> returned for the first run.</param>
    /// <param name="coveredAgain">The mutants the second run covered, or null when it gave no coverage. With no
    /// second reading there is no telling the two kinds apart, so every mutant counts as covered once per process:
    /// that costs test time, where the other guess could let a mutant survive untested.</param>
    public void Repeated(IReadOnlyCollection<int> firstSeen, IEnumerable<int>? coveredAgain)
    {
        _seenOnHost.UnionWith(firstSeen);
        _coveredOncePerProcess.UnionWith(coveredAgain is null ? firstSeen : firstSeen.Except(coveredAgain));
    }
}
