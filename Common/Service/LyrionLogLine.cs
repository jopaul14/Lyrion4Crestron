// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>Where a driver log line goes besides Trace.</summary>
    public enum LyrionLogLevel
    {
        /// <summary>Trace only (a Toolbox Text Console on the processor).</summary>
        TraceOnly,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Decides which driver log lines ALSO go to Crestron Home's own log — the
    /// one the Setup app shows under Diagnostics → Logs (#49).
    /// </summary>
    /// <remarks>
    /// Every line goes to Trace, as it always has; Trace reaches only a Toolbox
    /// console, so on the 1.0.17 bench pass an installer looking in the Setup
    /// app saw none of the warnings written for them — H3's mistyped-MAC
    /// warning, which exists precisely so a first-setup typo is not silent,
    /// was still silent there.
    ///
    /// The rule reads the severity word the log surface already carries
    /// ("Source WARNING: …", "Lyrion Server ERROR auth: …"), so no call site
    /// changes and a new WARNING line is forwarded without anyone remembering
    /// to. The one addition is the Server's smoothed connectivity transition:
    /// "LMS DISCONNECTED" as a warning, so an outage like #46 shows, and
    /// "LMS CONNECTED" as information. All of these fire once per
    /// misconfiguration or per smoothed transition, never during playback, so
    /// forwarding them stays within the flash-safe logging surface.
    /// </remarks>
    public static class LyrionLogLine
    {
        public static LyrionLogLevel Classify(string message)
        {
            if (string.IsNullOrEmpty(message)) return LyrionLogLevel.TraceOnly;
            if (message.IndexOf(" ERROR", StringComparison.Ordinal) >= 0) return LyrionLogLevel.Error;
            if (message.IndexOf(" WARNING", StringComparison.Ordinal) >= 0) return LyrionLogLevel.Warning;
            if (message.EndsWith("LMS DISCONNECTED", StringComparison.Ordinal)) return LyrionLogLevel.Warning;
            if (message.EndsWith("LMS CONNECTED", StringComparison.Ordinal)) return LyrionLogLevel.Info;
            return LyrionLogLevel.TraceOnly;
        }
    }
}
