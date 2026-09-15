// ---------------------------------------------------------------------------
//  Lyrion4Crestron - Shared service contract
//  Licensed under the MIT License. See LICENSE at the repository root.
// ---------------------------------------------------------------------------

using System;

namespace LyrionCommunity.Crestron.Lyrion.Service
{
    /// <summary>
    /// One installer-configured preset: a named, icon-bearing shortcut that
    /// sends a fixed LMS CLI command fragment to this room's player. Presets
    /// are how a Lyrion playlist, favourite, or radio stream is started from
    /// Crestron Home without the driver having to enumerate the server's whole
    /// playlist library.
    /// </summary>
    /// <remarks>
    /// "Preset" is Crestron's vocabulary for a named, recallable device
    /// shortcut (cf. <c>IPresetController</c>, <c>ITuner.PresetRecall</c>); the
    /// underlying LMS objects are playlists and favourites.
    /// <para>
    /// Configured on the Helper as a single pipe-delimited string per preset:
    /// <code>KCRW|icBroadcastRegular|favorites playlist play item_id:2</code>
    /// </para>
    /// The MAC address and the trailing newline are supplied by the driver, so
    /// the command is only the fragment that follows the MAC.
    /// </remarks>
    public sealed class LyrionPresetConfig
    {
        /// <summary>Icon used when the installer leaves the icon field blank.</summary>
        public const string DefaultIcon = "icBroadcastRegular";

        private LyrionPresetConfig(string name, string icon, string command)
        {
            Name = name;
            Icon = icon;
            Command = command;
        }

        /// <summary>Button label, e.g. "KCRW". Never empty for a parsed preset.</summary>
        public string Name { get; }

        /// <summary>
        /// Crestron icon name without the UiDefinition '#' prefix, e.g.
        /// "icBroadcastRegular". Falls back to <see cref="DefaultIcon"/>.
        /// </summary>
        public string Icon { get; }

        /// <summary>
        /// CLI fragment following the MAC, e.g.
        /// <c>favorites playlist play item_id:2</c>. Never empty for a parsed
        /// preset.
        /// </summary>
        public string Command { get; }

        /// <summary>
        /// Parses one configured preset string. Returns <c>null</c> — meaning
        /// "this preset slot is unconfigured, hide it" — for null, blank, or
        /// unparseable input.
        /// </summary>
        /// <remarks>
        /// Accepted forms, both tolerant of surrounding whitespace:
        /// <list type="bullet">
        /// <item><c>Name|Icon|Command</c></item>
        /// <item><c>Name|Command</c> — icon defaults to
        /// <see cref="DefaultIcon"/>.</item>
        /// </list>
        /// The split is limited to three parts so a '|' inside the command
        /// itself survives. A leading '#' on the icon is tolerated: that is the
        /// UiDefinition's literal-icon syntax, and installers copying an icon
        /// name from driver XML would otherwise get a silently broken button.
        /// Only the two-field form is ambiguous, and it resolves in favour of
        /// the command, which is the field a preset cannot work without.
        /// </remarks>
        public static LyrionPresetConfig Parse(string configured)
        {
            if (string.IsNullOrEmpty(configured)) return null;

            var parts = configured.Split(new[] { '|' }, 3);

            string name, icon, command;
            if (parts.Length >= 3)
            {
                name = parts[0].Trim();
                icon = parts[1].Trim();
                command = parts[2].Trim();
            }
            else if (parts.Length == 2)
            {
                name = parts[0].Trim();
                icon = string.Empty;
                command = parts[1].Trim();
            }
            else
            {
                // A lone value has no name to label a button with and no way to
                // tell a name from a command; treat the slot as unconfigured
                // rather than guessing.
                return null;
            }

            if (name.Length == 0 || command.Length == 0) return null;

            if (icon.Length == 0) icon = DefaultIcon;
            else if (icon[0] == '#') icon = icon.Substring(1).Trim();
            if (icon.Length == 0) icon = DefaultIcon;

            return new LyrionPresetConfig(name, icon, command);
        }
    }
}
