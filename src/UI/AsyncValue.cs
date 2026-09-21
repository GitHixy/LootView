using System;
using System.Threading.Tasks;

namespace LootView.UI;

/// <summary>
/// Holds a value that is expensive to compute and keeps that work off the draw thread.
///
/// Call <see cref="Ensure"/> every frame with a key describing what the value should be
/// derived from. When the key changes, the computation is started on the thread pool and
/// the previous value stays on screen until the new one is ready, so the UI never blocks
/// and never flashes empty.
///
/// All members are called from the draw thread only; the factory is the sole thing that
/// runs elsewhere.
/// </summary>
public sealed class AsyncValue<T> where T : class
{
    private readonly string name;
    private Task<T> running;
    private object runningKey;
    private object currentKey;
    private bool hasResult;

    public AsyncValue(string name) => this.name = name;

    /// <summary>The most recently computed value, or null until the first one arrives.</summary>
    public T Value { get; private set; }

    /// <summary>True once a value has been produced at least once.</summary>
    public bool HasValue => Value is not null;

    /// <summary>True while a computation is in flight.</summary>
    public bool IsLoading => running is not null;

    /// <summary>True on the very first load, when there is nothing to show yet.</summary>
    public bool IsFirstLoad => running is not null && Value is null;

    /// <summary>True when the last computation threw. Cleared by the next successful run.</summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// Makes sure <see cref="Value"/> reflects <paramref name="key"/>, starting
    /// <paramref name="factory"/> in the background if it does not yet.
    /// <paramref name="factory"/> runs off the game thread: it must not touch ImGui or
    /// any Dalamud game state.
    /// </summary>
    public void Ensure(object key, Func<T> factory)
    {
        Poll();

        if (running is not null)
        {
            // Already computing. A key that moved on again is picked up next frame, once
            // the in-flight task has been collected - which also debounces slider drags.
            return;
        }

        if (hasResult && Equals(currentKey, key))
        {
            return;
        }

        runningKey = key;
        running = Task.Run(factory);
    }

    /// <summary>
    /// Forces the next <see cref="Ensure"/> to recompute. The current value is kept so it
    /// stays on screen while the refresh runs.
    /// </summary>
    public void Invalidate()
    {
        hasResult = false;
        currentKey = null;
    }

    private void Poll()
    {
        if (running is null || !running.IsCompleted)
        {
            return;
        }

        var finished = running;
        running = null;

        if (finished.IsFaulted)
        {
            Plugin.Log.Error(finished.Exception, "Background computation for {Name} failed", name);
            Failed = true;
            // Adopt the key anyway, so a reproducible failure does not spin forever.
            currentKey = runningKey;
            hasResult = true;
            return;
        }

        if (finished.IsCanceled)
        {
            return;
        }

        Value = finished.Result;
        currentKey = runningKey;
        hasResult = true;
        Failed = false;
    }
}
