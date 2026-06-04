using PlexBot.Core.Discord.Embeds;
using PlexBot.Core.Exceptions;
using PlexBot.Core.Models.Media;
using PlexBot.Core.Models.Players;
using PlexBot.Core.Services;
using PlexBot.Utils;

namespace PlexBot.Core.Services.LavaLink;

/// <summary>Manages local ffmpeg playback in Discord voice channels.</summary>
public class PlayerService(
    VisualPlayerStateManager stateManager,
    FfmpegPlayerManager playerManager,
    VisualPlayer visualPlayer,
    IServiceProvider serviceProvider,
    DiscordButtonBuilder buttonBuilder)
    : IPlayerService
{
    /// <inheritdoc />
    public async Task<CustomLavaLinkPlayer?> GetPlayerAsync(IDiscordInteraction interaction, bool connectToVoiceChannel = true,
        CancellationToken cancellationToken = default)
    {
        if (interaction.User is not IGuildUser user)
        {
            await interaction.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return null;
        }

        ulong guildId = user.Guild.Id;
        CustomLavaLinkPlayer? existing = playerManager.GetPlayer(guildId);

        if (!connectToVoiceChannel)
            return existing;

        if (user.VoiceChannel is null)
        {
            await interaction.FollowupAsync("You must be in a voice channel to use the music player.", ephemeral: true);
            return null;
        }

        try
        {
            ITextChannel? textChannel = interaction is SocketInteraction socketInteraction
                ? socketInteraction.Channel as ITextChannel
                : null;

            CustomLavaLinkPlayer player = playerManager.GetOrCreatePlayer(
                guildId,
                user.VoiceChannel.Id,
                user.VoiceChannel,
                textChannel,
                serviceProvider,
                0.2f);

            await player.UpdateVoiceChannelAsync(user.VoiceChannel, cancellationToken).ConfigureAwait(false);
            await player.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return player;
        }
        catch (Exception ex)
        {
            Logs.Error($"Error getting player: {ex.Message}");
            throw new PlayerException($"Failed to get player: {ex.Message}", "Connect", ex);
        }
    }

    /// <inheritdoc />
    public async Task PlayTrackAsync(IDiscordInteraction interaction, Track track, CancellationToken cancellationToken = default)
    {
        await AddToQueueAsync(interaction, [track], cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddToQueueAsync(IDiscordInteraction interaction, IEnumerable<Track> tracks,
        CancellationToken cancellationToken = default)
    {
        CustomLavaLinkPlayer? player = await GetPlayerAsync(interaction, true, cancellationToken);
        if (player == null)
        {
            Logs.Warning("Failed to get player for queueing");
            return;
        }

        try
        {
            IUserMessage response = await interaction.GetOriginalResponseAsync();
            ITextChannel? channel = response.Channel as ITextChannel;
            stateManager.CurrentPlayerChannel = channel ?? throw new InvalidOperationException("CurrentPlayerChannel is not set");

            List<Track> trackList = tracks.Where(t => !string.IsNullOrWhiteSpace(t.PlaybackUrl)).ToList();
            int requestedCount = tracks.Count();
            int totalCount = trackList.Count;
            Logs.Debug($"Adding {totalCount} tracks to ffmpeg queue");

            if (totalCount == 0)
            {
                await interaction.ModifyOriginalResponseAsync(msg =>
                {
                    msg.Components = ComponentV2Builder.Error("Load Failed", "No playable track URLs were found.");
                    msg.Embed = null;
                    msg.Flags = MessageFlags.ComponentsV2;
                });
                return;
            }

            List<CustomTrackQueueItem> items = trackList.Select(track => new CustomTrackQueueItem
            {
                SourceTrack = track,
                RequestedBy = interaction.User.Username
            }).ToList();

            bool shouldPlay = player.State != PlayerState.Playing && player.State != PlayerState.Paused;
            if (shouldPlay)
            {
                Logs.Debug($"Playing first track: {items[0].Title} by {items[0].Artist}");
                await player.PlayAsync(items[0], cancellationToken);
                foreach (CustomTrackQueueItem item in items.Skip(1))
                    await player.Queue.AddAsync(item, cancellationToken);
            }
            else
            {
                foreach (CustomTrackQueueItem item in items)
                    await player.Queue.AddAsync(item, cancellationToken);
            }

            if (items.Count > 1)
            {
                ButtonContext ctx = new() { Player = player, Interaction = interaction };
                ComponentBuilder refreshComponents = buttonBuilder.BuildButtons(ButtonFlag.VisualPlayer, ctx);
                await visualPlayer.AddOrUpdateVisualPlayerAsync(refreshComponents, recreateImage: true);
            }

            string message = items.Count == 1
                ? shouldPlay
                    ? $"Playing: {items[0].Title} by {items[0].Artist}"
                    : $"Added to queue: {items[0].Title} by {items[0].Artist}"
                : requestedCount == totalCount
                    ? $"Added {items.Count} tracks to the queue"
                    : $"Added {items.Count} of {requestedCount} tracks to the queue";

            await interaction.ModifyOriginalResponseAsync(msg =>
            {
                msg.Components = ComponentV2Builder.Success(items.Count == 1 ? "Track Added" : "Tracks Added", message);
                msg.Embed = null;
                msg.Flags = MessageFlags.ComponentsV2;
            });
        }
        catch (Exception ex)
        {
            Logs.Error($"Error adding tracks to queue: {ex.Message}");
            throw new PlayerException($"Failed to add tracks to queue: {ex.Message}", "Queue", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> TogglePauseResumeAsync(IDiscordInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        CustomLavaLinkPlayer? player = await GetPlayerAsync(interaction, false, cancellationToken);
        if (player == null)
            throw new PlayerException("No active player found", "Pause");

        try
        {
            string result;
            if (player.State == PlayerState.Paused)
            {
                await player.ResumeAsync(cancellationToken);
                result = "Resumed";
            }
            else if (player.State == PlayerState.Playing)
            {
                await player.PauseAsync(cancellationToken);
                result = "Paused";
            }
            else
            {
                throw new PlayerException("No track is currently playing", "Pause");
            }

            ButtonContext context = new() { Player = player, Interaction = interaction };
            ComponentBuilder components = buttonBuilder.BuildButtons(ButtonFlag.VisualPlayer, context);
            await visualPlayer.AddOrUpdateVisualPlayerAsync(components);
            return result;
        }
        catch (Exception ex) when (ex is not PlayerException)
        {
            Logs.Error($"Error toggling pause/resume: {ex.Message}");
            throw new PlayerException($"Failed to toggle pause/resume: {ex.Message}", "Pause", ex);
        }
    }

    /// <inheritdoc />
    public async Task SkipTrackAsync(IDiscordInteraction interaction, CancellationToken cancellationToken = default)
    {
        CustomLavaLinkPlayer? player = await GetPlayerAsync(interaction, false, cancellationToken);
        if (player == null)
            throw new PlayerException("No active player found", "Skip");

        try
        {
            if (player.State != PlayerState.Playing && player.State != PlayerState.Paused)
                throw new PlayerException("No track is currently playing", "Skip");

            await player.SkipAsync(1, cancellationToken);
            Logs.Debug($"Track skipped by {interaction.User.Username}");
        }
        catch (Exception ex) when (ex is not PlayerException)
        {
            Logs.Error($"Error skipping track: {ex.Message}");
            throw new PlayerException($"Failed to skip track: {ex.Message}", "Skip", ex);
        }
    }

    /// <inheritdoc />
    public async Task SetRepeatModeAsync(IDiscordInteraction interaction, TrackRepeatMode repeatMode,
        CancellationToken cancellationToken = default)
    {
        CustomLavaLinkPlayer? player = await GetPlayerAsync(interaction, false, cancellationToken);
        if (player == null)
            throw new PlayerException("No active player found", "Repeat");

        try
        {
            player.RepeatMode = repeatMode;
            ButtonContext context = new() { Player = player, Interaction = interaction };
            ComponentBuilder components = buttonBuilder.BuildButtons(ButtonFlag.VisualPlayer, context);
            await visualPlayer.AddOrUpdateVisualPlayerAsync(components, true);
            Logs.Debug($"Repeat mode set to {repeatMode} by {interaction.User.Username}");
        }
        catch (Exception ex)
        {
            Logs.Error($"Error setting repeat mode: {ex.Message}");
            throw new PlayerException($"Failed to set repeat mode: {ex.Message}", "Repeat", ex);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(IDiscordInteraction interaction, bool disconnect = false,
        CancellationToken cancellationToken = default)
    {
        CustomLavaLinkPlayer? player = await GetPlayerAsync(interaction, false, cancellationToken);
        if (player == null)
            throw new PlayerException("No active player found", "Stop");

        try
        {
            await player.StopAsync(cancellationToken);
            await player.Queue.ClearAsync(cancellationToken);
            if (disconnect)
            {
                await player.DisconnectAsync(cancellationToken);
                playerManager.RemovePlayer(player.GuildId);
            }
            Logs.Debug(disconnect
                ? $"Player stopped and disconnected by {interaction.User.Username}"
                : $"Player stopped by {interaction.User.Username}");
        }
        catch (Exception ex)
        {
            Logs.Error($"Error stopping player: {ex.Message}");
            throw new PlayerException($"Failed to stop player: {ex.Message}", "Stop", ex);
        }
    }
}
