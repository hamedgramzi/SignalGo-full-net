﻿using SignalGo.Shared.Log;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SignalGo.Shared
{
    /// <summary>
    /// ConcurrentDictionary extension helper
    /// </summary>
    public static class ConcurrentDictionaryEx
    {
        /// <summary>
        /// remove a dictionary key
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="self"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        public static bool Remove<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> self, TKey key)
        {
            return ((IDictionary<TKey, TValue>)self).Remove(key);
        }
    }

    /// <summary>
    /// run Action on same thread
    /// </summary>
    public static class AsyncActions
    {
        public static AutoLogger AutoLogger { get; set; } = new AutoLogger() { FileName = "AsyncActions Logs.log" };
#if (!PORTABLE)
        private static SynchronizationContext UIThread { get; set; }
        /// <summary>
        /// initialize ui thread
        /// </summary>
        public static void InitializeUIThread()
        {
            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            UIThread = SynchronizationContext.Current;
        }

        /// <summary>
        /// run your code on ui thread
        /// </summary>
        /// <param name="action"></param>
        public static void RunOnUI(Action action)
        {
            if (UIThread == null)
                throw new Exception("UI thread not initialized please call AsyncActions.InitializeUIThread in your ui thread to initialize");
            UIThread.Post((state) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AutoLogger.LogError(ex, "AsyncActions RunOnUI");
                }
            }, null);
        }
#endif
        /// <summary>
        /// if actions return exceptions
        /// </summary>
        public static Action<Exception> OnActionException { get; set; }
        /// <summary>
        /// Run action on thread
        /// </summary>
        /// <param name="action">your action</param>
        /// <param name="onException"></param>
        public static void Run(Action action, Action<Exception> onException = null)
        {
#if (NET35 || NET40)
            ThreadPool.QueueUserWorkItem(RunAction, null);
            void RunAction(object state)
#else
            System.Threading.Tasks.Task.Run(new Action(RunAction));
            void RunAction()
#endif
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    try
                    {
                        onException?.Invoke(ex);
                        AutoLogger.LogError(ex, "AsyncActions Run");
                        OnActionException?.Invoke(ex);
                    }
                    catch (Exception ex2)
                    {
                        AutoLogger.LogError(ex2, "AsyncActions Run 2");

                    }
                }
            }
        }

        public static Thread StartNew(Action action, Action<Exception> onException = null)
        {
            Thread thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    try
                    {
                        onException?.Invoke(ex);
                        AutoLogger.LogError(ex, "AsyncActions Run");
                        OnActionException?.Invoke(ex);
                    }
                    catch (Exception ex2)
                    {
                        AutoLogger.LogError(ex2, "AsyncActions Run 2");

                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }
    }
}
