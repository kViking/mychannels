namespace Jellyfin.Plugin.MyChannels.Models;

/// <summary>
/// How the channel inserts auto-generated Up Next cards between its scheduled programs.
/// </summary>
public enum FillerMode
{
    /// <summary>No cards. Programs air back-to-back at their real durations (upstream behaviour).</summary>
    Off = 0,

    /// <summary>Insert a fixed-length card (see <see cref="Channel.BumperSeconds"/>) before every program.</summary>
    FixedBumper = 1,

    /// <summary>Pad each program's slot to a grid boundary (see <see cref="Channel.SnapMinutes"/>) with a card. Programs start on tidy times like real broadcast TV.</summary>
    SnapToBoundary = 2,

    /// <summary>Play a user-uploaded MP4 bumper before every program (see <see cref="Channel.HasCustomBumper"/>). The bumper's own duration is the slot; auto-card generation is bypassed entirely for this channel.</summary>
    CustomBumper = 3
}
