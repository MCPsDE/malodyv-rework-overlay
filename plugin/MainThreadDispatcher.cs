using System.Collections.Concurrent;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace MalodyV.ReworkOverlay.Plugin;

internal sealed class MainThreadDispatcher : MonoBehaviour
{
    private sealed record Scheduled(int DueFrame, Action Callback);

    private static readonly ConcurrentQueue<Scheduled> Pending = new();
    private static bool _registered;

    public MainThreadDispatcher(IntPtr pointer) : base(pointer)
    {
    }

    public static void Initialize()
    {
        if (_registered)
            return;
        ClassInjector.RegisterTypeInIl2Cpp<MainThreadDispatcher>();
        var host = new GameObject("MalodyV.ReworkOverlay.Dispatcher");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<MainThreadDispatcher>();
        _registered = true;
    }

    public static void PostDelayed(Action callback, int delayFrames = 8)
    {
        Pending.Enqueue(new Scheduled(Math.Max(0, Time.frameCount + delayFrames), callback));
    }

    private void Update()
    {
        var deferred = new List<Scheduled>();
        while (Pending.TryDequeue(out var scheduled))
        {
            if (scheduled.DueFrame <= Time.frameCount)
                scheduled.Callback();
            else
                deferred.Add(scheduled);
        }

        foreach (var scheduled in deferred)
            Pending.Enqueue(scheduled);
    }
}
