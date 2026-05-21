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
    /// driver registers itself on startup; Source, Helper, and Receiver
    /// drivers wait for registration via <see cref="Subscribe"/>.
    /// </summary>
    /// <remarks>
    /// All four drivers reference this assembly (Lyrion_Common.dll) via
    /// ProjectReference and share the same DependencyGroup ("LyrionLMS"),
    /// ensuring they are loaded into the same AppDomain. The CLR loads
    /// Lyrion_Common.dll once, so the static fields below are truly
    /// process-wide shared state.
    /// </remarks>
    public static class LyrionGatewayServiceRegistry
    {
        private static readonly object Gate = new object();
        private static ILyrionGatewayService _current;

        // Tracks every consumer that has subscribed. Subscribers remain in
        // this list across Register/Unregister cycles so that a Gateway reload
        // can re-notify all surviving consumers with the new service instance.
        // Consumers MUST call Unsubscribe from Dispose to avoid the list
        // holding references to disposed drivers.
        private static readonly List<Action<ILyrionGatewayService>> Subscribers =
            new List<Action<ILyrionGatewayService>>();

        /// <summary>Called by the Gateway driver during driver startup.</summary>
        /// <remarks>
        /// <para>Notifies every current subscriber, not just the ones that
        /// subscribed before this Register call. This means a Gateway reload
        /// (where the Gateway driver is disposed and re-created without the
        /// Source/Helper/Receiver drivers being reloaded) correctly hands the
        /// new service instance to all surviving consumers.</para>
        /// <para>Idempotent: calling Register twice with the same instance is
        /// a no-op and does not re-notify subscribers.</para>
        /// <para>SECURITY: this registry is process-wide static state. Any
        /// driver loaded into the same AppDomain can call <see cref="Register"/>
        /// and replace the active service, which would then receive every
        /// Subscribe callback. This is acceptable on Crestron Home because
        /// drivers are loaded from a signed/curated package store and the
        /// AppDomain is treated as a single trust zone; do NOT remove this
        /// note without also revisiting that platform assumption.</para>
        /// </remarks>
        public static void Register(ILyrionGatewayService service)
        {
            if (service == null) throw new ArgumentNullException(nameof(service));

            List<Action<ILyrionGatewayService>> toNotify;
            lock (Gate)
            {
                if (ReferenceEquals(_current, service)) return;
                _current = service;
                toNotify = new List<Action<ILyrionGatewayService>>(Subscribers);
            }

            foreach (var cb in toNotify)
            {
                try { cb(service); }
                catch { /* never propagate consumer faults */ }
            }
        }

        /// <summary>Called by the Gateway driver during Dispose.</summary>
        /// <remarks>
        /// Subscribers are NOT removed. Consumers retain a reference to the
        /// outgoing service (which has been disposed and will no longer fire
        /// events). When a new Gateway registers, <see cref="Register"/>
        /// re-notifies all subscribers so they can swap to the new instance.
        /// </remarks>
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
        /// Registers <paramref name="onAvailable"/> to be invoked whenever a
        /// Gateway service is registered. If one is already registered, the
        /// callback fires once before this method returns. The callback will
        /// also fire again each time a new Gateway service replaces the
        /// current one (e.g. after a Gateway driver reload), so handlers MUST
        /// be safe to invoke multiple times — typically by detaching from
        /// any prior service reference before attaching to the new one.
        /// </summary>
        /// <remarks>
        /// Duplicate subscriptions (the same delegate added twice) are
        /// silently ignored so a buggy reload cycle cannot cause double-fire.
        /// </remarks>
        public static void Subscribe(Action<ILyrionGatewayService> onAvailable)
        {
            if (onAvailable == null) throw new ArgumentNullException(nameof(onAvailable));

            ILyrionGatewayService now;
            lock (Gate)
            {
                if (!Subscribers.Contains(onAvailable))
                {
                    Subscribers.Add(onAvailable);
                }
                now = _current;
            }

            if (now != null)
            {
                try { onAvailable(now); }
                catch { /* never propagate consumer faults */ }
            }
        }

        /// <summary>
        /// Removes a <see cref="Subscribe"/> registration. Idempotent. Consumers
        /// MUST call this from Dispose() to prevent the subscriber list from
        /// holding references to disposed drivers.
        /// </summary>
        public static void Unsubscribe(Action<ILyrionGatewayService> onAvailable)
        {
            if (onAvailable == null) return;
            lock (Gate)
            {
                Subscribers.Remove(onAvailable);
            }
        }
    }
}
