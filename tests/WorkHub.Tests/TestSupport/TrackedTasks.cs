namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Observation of view-model tracked operations (Phase 11 B3, §10.1 correction).
/// <para>
/// <b>Quiescence contract.</b> A set of tracked operations is <i>quiescent</i> only when two consecutive reads of every
/// set return the same task instances and all of them are complete. A fault never ends observation: faulted tasks are
/// collected and the sets are read again, so replacement work published by (or after) a failing operation is awaited too.
/// Every set is observed even when another set faults. Quiescence is NOT established when a set cannot be read, when
/// the pass limit is reached while the sets keep changing, or (the caller's bound) when a task never completes.
/// Cancellation is the view models' designed way of superseding a load and counts as completion, not as a fault.
/// Only tracked operations are observable: work a view model starts without publishing it is outside this contract.
/// </para>
/// </summary>
internal static class TrackedTasks
{
    internal sealed record Observation(bool Quiescent, IReadOnlyList<Exception> Faults, IReadOnlyList<string> FaultedNames, string Detail, IReadOnlyList<(string Name, Task Task)> Final);

    public static Task<Observation> ObserveAsync(Func<IReadOnlyList<(string Name, Task Task)>> snapshot, int maxPasses = 10) =>
        ObserveAsync([snapshot], maxPasses);

    public static async Task<Observation> ObserveAsync(IReadOnlyList<Func<IReadOnlyList<(string Name, Task Task)>>> sets, int maxPasses = 10)
    {
        var faults = new List<Exception>();
        var faultedNames = new List<string>();
        var counted = new HashSet<Task>();
        var unreadable = new HashSet<int>();

        IReadOnlyList<(string Name, Task Task)> Read()
        {
            var all = new List<(string Name, Task Task)>();
            for (var i = 0; i < sets.Count; i++)
            {
                try
                {
                    all.AddRange(sets[i]());
                }
                catch (Exception ex)
                {
                    if (unreadable.Add(i))
                        faults.Add(ex); // reported once; the other sets are still observed
                }
            }

            return all;
        }

        void Collect(IEnumerable<(string Name, Task Task)> tasks)
        {
            foreach (var (name, task) in tasks)
            {
                if (!task.IsFaulted || !counted.Add(task))
                    continue;
                var real = task.Exception!.InnerExceptions.Where(e => e is not OperationCanceledException).ToList();
                if (real.Count == 0)
                    continue;
                faults.AddRange(real);
                faultedNames.Add(name);
            }
        }

        var tasks = Read();
        for (var pass = 1; pass <= maxPasses; pass++)
        {
            try
            {
                await Task.WhenAll(tasks.Select(t => t.Task));
            }
            catch
            {
                // faults are collected per task below; observation continues
            }

            Collect(tasks);
            var again = Read();
            Collect(again);
            if (SameAndCompleted(again, tasks))
            {
                return unreadable.Count == 0
                    ? new Observation(true, faults, faultedNames, "settled", again)
                    : new Observation(false, faults, faultedNames, $"{unreadable.Count} tracked set(s) could not be read", again);
            }

            tasks = again;
        }

        var pending = tasks.Where(t => !t.Task.IsCompleted).Select(t => t.Name).DefaultIfEmpty("(new instances each pass)");
        return new Observation(false, faults, faultedNames, $"did not settle within {maxPasses} passes; still pending or changing: {string.Join(", ", pending)}", tasks);
    }

    /// <summary>The same task instances, in order, all complete.</summary>
    public static bool SameAndCompleted(IReadOnlyList<(string Name, Task Task)> current, IReadOnlyList<(string Name, Task Task)> previous) =>
        current.Count == previous.Count && current.Zip(previous).All(p => ReferenceEquals(p.First.Task, p.Second.Task) && p.First.Task.IsCompleted);

    /// <summary>For test bodies: awaits quiescence and throws when it is not established or when an operation faulted.</summary>
    public static async Task SettleAsync(Func<IReadOnlyList<(string Name, Task Task)>> snapshot, int maxPasses = 10)
    {
        var observation = await ObserveAsync(snapshot, maxPasses);
        if (!observation.Quiescent)
            throw new InvalidOperationException($"Tracked operations {observation.Detail}.", observation.Faults.Count == 0 ? null : new AggregateException(observation.Faults));
        if (observation.Faults.Count > 0)
            throw new AggregateException($"Tracked operation(s) failed: {string.Join(", ", observation.FaultedNames)}.", observation.Faults);
    }
}
