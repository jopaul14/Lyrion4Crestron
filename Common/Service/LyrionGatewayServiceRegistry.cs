// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// Process-wide rendezvous for the Lyrion gateway service. The Gateway
    /// driver registers itself on startup; Media and Volume drivers wait for
    /// registration via <see cref="Subscribe"/>.
    /// </summary>
    /// <remarks>
    /// All three drivers reference this assembly (Lyrion_Common.dll) via
    /// ProjectReference and share the same DependencyGroup ("LyrionLMS"),
    /// ensuring they are loaded into the same AppDomain. The CLR loads
    /// Lyrion_Common.dll once, so the static fields below are truly
    /// process-wide shared state.
    /// </remarks>
    public static class LyrionGatewayServiceRegistry
    {
        private static readonly object Gate = new object();
        private static ILyrionGatewayService _current;
        private static readonly List<Action<ILyrionGatewayService>> Pending =
            new List<Action<ILyrionGatewayService>>();

        /// <summary>Called by the Gateway driver during driver startup.</summary>
        /// <remarks>
        /// SECURITY: this registry is process-wide static state. Any driver
        /// loaded into the same AppDomain can call <see cref="Register"/> and
        /// replace the active service, which would then receive every
        /// Subscribe callback. This is acceptable on Crestron Home because
        /// drivers are loaded from a signed/curated package store and the
        /// AppDomain is treated as a single trust zone; do NOT remove this
        /// note without also revisiting that platform assumption.
        /// </remarks>
        public static void Register(ILyrionGatewayService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            List<Action<ILyrionGatewayService>> toNotify;
            lock (Gate)
            {
                _current = service;
                toNotify = new List<Action<ILyrionGatewayService>>(Pending);
                Pending.Clear();
            }

            foreach (var cb in toNotify)
            {
                try { cb(service); }
                catch { /* never propagate consumer faults */ }
            }
        }

        /// <summary>Called by the Gateway driver during Dispose.</summary>
        public static void Unregister(ILyrionGatewayService service)
        {
            lock (Gate)
            {
                if (ReferenceEquals(_current, service))
                {
                    _current = null;
                }
            }
        }

        public static bool TryGet(out ILyrionGatewayService service)
        {
            lock (Gate)
            {
                service = _current;
                return service != null;
            }
        }

        /// <summary>
        /// Invoke <paramref name="onAvailable"/> as soon as the gateway is
        /// registered. If it is already registered the callback fires before
        /// this method returns. The callback fires at most once.
        /// </summary>
        public static void Subscribe(Action<ILyrionGatewayService> onAvailable)
        {
            if (onAvailable == null) throw new ArgumentNullException(nameof(onAvailable));

            ILyrionGatewayService now;
            lock (Gate)
            {
                now = _current;
                if (now == null)
                {
                    Pending.Add(onAvailable);
                    return;
                }
            }

            try { onAvailable(now); }
            catch { /* never propagate consumer faults */ }
        }

        /// <summary>
        /// Removes a pending <see cref="Subscribe"/> callback if it has not yet
        /// fired. Safe to call after the callback has fired (no-op). Consumers
        /// must call this from Dispose() to prevent the registry from holding
        /// references to disposed drivers if the gateway has not yet registered.
        /// </summary>
        public static void Unsubscribe(Action<ILyrionGatewayService> onAvailable)
        {
            if (onAvailable == null) return;
            lock (Gate)
            {
                Pending.Remove(onAvailable);
            }
        }
    }
}
