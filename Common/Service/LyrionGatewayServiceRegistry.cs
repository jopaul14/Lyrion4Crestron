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
    /// This is the platform-neutral surface for the "Crestron SDK service
    /// registration" requirement in CLAUDE.md. On a Crestron Home appliance
    /// where all driver assemblies are loaded into a single AppDomain, the
    /// static state in this class is the shared object. If a deployment ever
    /// requires cross-process service routing, this class is the single point
    /// where the underlying mechanism can be swapped without touching any
    /// driver.
    /// </remarks>
    public static class LyrionGatewayServiceRegistry
    {
        private static readonly object Gate = new object();
        private static ILyrionGatewayService _current;
        private static readonly List<Action<ILyrionGatewayService>> Pending =
            new List<Action<ILyrionGatewayService>>();

        /// <summary>Called by the Gateway driver during driver startup.</summary>
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
    }
}
