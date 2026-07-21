using System.Diagnostics;

namespace CodeCargo.Nats.DistributedCache.TestUtils.Services.Diagnostics;

/// <summary>
/// An <see cref="ActivityListener" /> that captures completed activities from a single source in memory so
/// tests can assert on them. Mirrors <c>RecordingLogger{T}</c> for traces.
/// </summary>
/// <remarks>
/// Samples every activity as <see cref="ActivitySamplingResult.AllDataAndRecorded" />, so tags set behind
/// <c>IsAllDataRequested</c> are captured. Only activities that stop while the listener is alive are
/// recorded, which is what makes per-test instances work.
/// </remarks>
public sealed class RecordingActivityListener : IDisposable
{
    private readonly List<Activity> _activities = new();
    private readonly ActivityListener _listener;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingActivityListener" /> class
    /// </summary>
    /// <param name="sourceName">The <see cref="ActivitySource" /> name to listen to</param>
    public RecordingActivityListener(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = SampleAllData,
            SampleUsingParentId = SampleAllDataUsingParentId,
            ActivityStopped = activity =>
            {
                lock (_activities)
                {
                    _activities.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// Gets a snapshot of the activities captured so far
    /// </summary>
    public IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_activities)
            {
                return _activities.ToArray();
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    // Named static methods rather than lambdas: SampleActivity<T> takes a `ref` parameter, and lambda
    // inference for ref parameters differs across the two target frameworks.
    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> options) =>
        ActivitySamplingResult.AllDataAndRecorded;

    private static ActivitySamplingResult SampleAllDataUsingParentId(ref ActivityCreationOptions<string> options) =>
        ActivitySamplingResult.AllDataAndRecorded;
}
