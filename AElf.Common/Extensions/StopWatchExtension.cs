using System;
using System.Diagnostics;

namespace AElf.Common
{
    public static class StopwatchExtension
    {
        public static T Measure<T>(this Stopwatch stopwatch, Func<T> action, Action<TimeSpan> elapsed)
        {
            try
            {
                return action();
            }
            finally
            {
                elapsed(stopwatch.Elapsed);
            }
        }

        public static void Measure(this Stopwatch stopwatch, Action action, Action<TimeSpan> elapsed)
        {
            stopwatch.Measure<object>(() =>
            {
                action();
                return default;
            }, elapsed);
        }
    }
}