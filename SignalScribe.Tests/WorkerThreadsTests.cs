using SignalScribe.Analysis;
using Xunit;

namespace SignalScribe.Tests;

public class WorkerThreadsTests
{
    [Fact]
    public void AutomaticLeavesOneCoreForCapture()
    {
        // Capture consumes a 6.4 MSPS stream in realtime; it cannot be starved by inference.
        Assert.Equal(3, WorkerThreads.Resolve(WorkerThreads.Automatic, processorCount: 4));
        Assert.Equal(15, WorkerThreads.Resolve(WorkerThreads.Automatic, processorCount: 16));
    }

    [Fact]
    public void AutomaticStillYieldsAUsableThreadOnASingleCore()
    {
        Assert.Equal(1, WorkerThreads.Resolve(WorkerThreads.Automatic, processorCount: 1));
        Assert.Equal(1, WorkerThreads.Resolve(WorkerThreads.Automatic, processorCount: 0));
    }

    [Fact]
    public void AnExplicitChoiceIsHonoured()
    {
        Assert.Equal(2, WorkerThreads.Resolve(2, processorCount: 4));
        Assert.Equal(4, WorkerThreads.Resolve(4, processorCount: 4)); // deliberately taking every core
    }

    [Fact]
    public void ClampsToWhatTheMachineActuallyHas()
    {
        Assert.Equal(4, WorkerThreads.Resolve(32, processorCount: 4));
    }
}
