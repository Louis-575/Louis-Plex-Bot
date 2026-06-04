using PlexBot.Core.Models.Media;

namespace PlexBot.Core.Services.LavaLink;

/// <summary>Enhanced track queue item that holds a reference to the source Track model and exposes metadata through convenience properties for UI compatibility</summary>
public class CustomTrackQueueItem
{
    /// <summary>The source track metadata from Plex/YouTube/etc.</summary>
    public Track SourceTrack { get; init; } = new();

    /// <summary>The Discord user who requested this track</summary>
    public string? RequestedBy { get; init; }

    // Convenience accessors for backward compatibility with UI code (ImageBuilder, VisualPlayer, DiscordEmbedBuilder)
    public string? Title => SourceTrack.Title;
    public string? Artist => SourceTrack.Artist;
    public string? Album => SourceTrack.Album;
    public string? ReleaseDate => SourceTrack.ReleaseDate;
    public string? Artwork => SourceTrack.ArtworkUrl;
    public string? Url => SourceTrack.PlaybackUrl;
    public string? ArtistUrl => SourceTrack.ArtistUrl;
    public string? Duration => SourceTrack.DurationDisplay;
    public string? Studio => SourceTrack.Studio;

    /// <summary>Generates a user-friendly string representation of this track for logging and debugging</summary>
    public override string ToString() => $"{Title} by {Artist} ({Duration})";
}
