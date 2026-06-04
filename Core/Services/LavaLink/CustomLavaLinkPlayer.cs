using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using PlexBot.Core.Discord.Embeds;
using PlexBot.Core.Events;
using PlexBot.Core.Models.Media;
using PlexBot.Utils;

namespace PlexBot.Core.Services.LavaLink;

/// <summary>Local ffmpeg-backed player that preserves the old player surface used by the bot UI.</summary>
public sealed class CustomLavaLinkPlayer : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private readonly object _stateLock = new();
    private IAudioClient? _audioClient;
    private CancellationTokenSource? _playbackCts;
    private CancellationTokenSource? _idleDisconnectCts;
    private Process? _ffmpegProcess;
    private DateTimeOffset? _trackStartedAt;
    private TimeSpan _positionOffset = TimeSpan.Zero;
    private bool _skipRequested;
    private bool _stopRequested;
    private bool _disposed;

    public CustomLavaLinkPlayer(ulong guildId, ulong voiceChannelId, IVoiceChannel voiceChannel, ITextChannel? textChannel,
        IServiceProvider serviceProvider, float initialVolume = 0.2f)
    {
        GuildId = guildId;
        VoiceChannelId = voiceChannelId;
        VoiceChannel = voiceChannel;
        TextChannel = textChannel;
        _serviceProvider = serviceProvider;
        Volume = initialVolume;
    }

    public ulong GuildId { get; }
    public ulong VoiceChannelId { get; private set; }
    public IVoiceChannel VoiceChannel { get; private set; }
    public ITextChannel? TextChannel { get; }
    public FfmpegTrackQueue Queue { get; } = new();
    public CustomTrackQueueItem? CurrentItem { get; private set; }
    public LocalAudioTrack? CurrentTrack => CurrentItem is null ? null : new LocalAudioTrack(GetDuration(CurrentItem));
    public LocalPlayerPosition? Position => CurrentItem is null ? null : new LocalPlayerPosition(GetCurrentPosition());
    public TrackRepeatMode RepeatMode { get; set; } = TrackRepeatMode.None;
    public PlayerState State { get; private set; } = PlayerState.NotPlaying;
    public float Volume { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_audioClient is not null)
            return;

        _audioClient = await VoiceChannel.ConnectAsync(selfDeaf: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task PlayAsync(CustomTrackQueueItem item, CancellationToken cancellationToken = default)
    {
        await _playbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelIdleDisconnect();
            await StopCurrentProcessAsync().ConfigureAwait(false);
            CurrentItem = item;
            _positionOffset = TimeSpan.Zero;
            _trackStartedAt = DateTimeOffset.UtcNow;
            _skipRequested = false;
            _stopRequested = false;
            State = PlayerState.Playing;
            _playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Task.Run(() => RunTrackAsync(item, _playbackCts.Token), CancellationToken.None);
            await NotifyTrackStartedAsync(item, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != PlayerState.Playing)
            return Task.CompletedTask;

        lock (_stateLock)
        {
            _positionOffset = GetCurrentPosition();
            _trackStartedAt = null;
            State = PlayerState.Paused;
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State != PlayerState.Paused)
            return Task.CompletedTask;

        lock (_stateLock)
        {
            _trackStartedAt = DateTimeOffset.UtcNow;
            State = PlayerState.Playing;
        }
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volume = Math.Clamp(volume, 0f, 1f);
        return Task.CompletedTask;
    }

    public async Task SkipAsync(int count = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stopRequested = false;
        _skipRequested = true;
        await StopCurrentProcessAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stopRequested = true;
        _skipRequested = false;
        await StopCurrentProcessAsync().ConfigureAwait(false);
        CurrentItem = null;
        _trackStartedAt = null;
        _positionOffset = TimeSpan.Zero;
        State = PlayerState.NotPlaying;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        Queue.Clear();
        if (_audioClient is not null)
        {
            await _audioClient.StopAsync().ConfigureAwait(false);
            _audioClient.Dispose();
            _audioClient = null;
        }
    }

    internal async Task UpdateVoiceChannelAsync(IVoiceChannel voiceChannel, CancellationToken cancellationToken = default)
    {
        if (voiceChannel.Id == VoiceChannelId)
            return;

        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        VoiceChannel = voiceChannel;
        VoiceChannelId = voiceChannel.Id;
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunTrackAsync(CustomTrackQueueItem item, CancellationToken cancellationToken)
    {
        try
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (_audioClient is null)
                throw new InvalidOperationException("Discord audio client is not connected.");

            using Process ffmpeg = StartFfmpeg(item.SourceTrack);
            _ffmpegProcess = ffmpeg;
            _ = Task.Run(() => DrainErrorsAsync(ffmpeg, cancellationToken), CancellationToken.None);

            await using AudioOutStream discordStream = _audioClient.CreatePCMStream(AudioApplication.Music);
            byte[] buffer = new byte[3840];
            Stream output = ffmpeg.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (State == PlayerState.Paused)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int bytesRead = await output.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                    break;

                ApplyVolume(buffer, bytesRead, Volume);
                await discordStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }

            await discordStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (!ffmpeg.HasExited)
                ffmpeg.Kill(entireProcessTree: true);
        }
        catch (OperationCanceledException)
        {
            // Expected on skip/stop.
        }
        catch (Exception ex)
        {
            Logs.Error($"ffmpeg playback failed: {ex.Message}");
        }
        finally
        {
            _ffmpegProcess = null;
            if (_stopRequested && CurrentItem == item)
            {
                // Stop leaves the queue cleared/stopped; do not advance to another track.
            }
            else if (!_skipRequested && CurrentItem == item)
                await NotifyTrackEndedAndContinueAsync(item).ConfigureAwait(false);
            else if (_skipRequested && CurrentItem == item)
                await PlayNextAsync().ConfigureAwait(false);
        }
    }

    private async Task PlayNextAsync()
    {
        CustomTrackQueueItem? next = null;
        if (RepeatMode == TrackRepeatMode.Track && CurrentItem is not null)
            next = CurrentItem;
        else
        {
            if (RepeatMode == TrackRepeatMode.Queue && CurrentItem is not null)
                Queue.Add(CurrentItem);
            next = Queue.Dequeue();
        }

        if (next is null)
        {
            CurrentItem = null;
            State = PlayerState.NotPlaying;
            _trackStartedAt = null;
            _positionOffset = TimeSpan.Zero;
            ScheduleIdleDisconnect();
            return;
        }

        await PlayAsync(next).ConfigureAwait(false);
    }

    private async Task NotifyTrackStartedAsync(CustomTrackQueueItem track, CancellationToken cancellationToken)
    {
        try
        {
            VisualPlayer visualPlayer = _serviceProvider.GetRequiredService<VisualPlayer>();
            DiscordButtonBuilder buttonBuilder = _serviceProvider.GetRequiredService<DiscordButtonBuilder>();
            ButtonContext context = new() { Player = this };
            ComponentBuilder components = buttonBuilder.BuildButtons(ButtonFlag.VisualPlayer, context);
            await visualPlayer.AddOrUpdateVisualPlayerAsync(components, recreateImage: true).ConfigureAwait(false);

            ITrackPrefetchService prefetch = _serviceProvider.GetRequiredService<ITrackPrefetchService>();
            _ = prefetch.PrefetchNextAsync(this, cancellationToken);

            BotEventBus eventBus = _serviceProvider.GetRequiredService<BotEventBus>();
            _ = eventBus.PublishAsync(new BotEvent
            {
                EventType = BotEvents.TrackStarted,
                Data = new Dictionary<string, object>
                {
                    ["title"] = track.Title ?? "Unknown",
                    ["artist"] = track.Artist ?? "Unknown",
                    ["guildId"] = GuildId
                }
            });
        }
        catch (Exception ex)
        {
            Logs.Error($"Error in track started notification: {ex.Message}");
        }
    }

    private async Task NotifyTrackEndedAndContinueAsync(CustomTrackQueueItem track)
    {
        try
        {
            BotEventBus eventBus = _serviceProvider.GetRequiredService<BotEventBus>();
            _ = eventBus.PublishAsync(new BotEvent
            {
                EventType = BotEvents.TrackEnded,
                Data = new Dictionary<string, object>
                {
                    ["title"] = track.Title ?? "Unknown",
                    ["guildId"] = GuildId,
                    ["endReason"] = "Finished"
                }
            });
        }
        catch (Exception ex)
        {
            Logs.Error($"Error publishing track ended event: {ex.Message}");
        }

        await PlayNextAsync().ConfigureAwait(false);
    }

    private async Task StopCurrentProcessAsync()
    {
        try
        {
            _playbackCts?.Cancel();
            if (_ffmpegProcess is { HasExited: false })
                _ffmpegProcess.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Logs.Debug($"Error stopping ffmpeg process: {ex.Message}");
        }
        finally
        {
            _playbackCts?.Dispose();
            _playbackCts = null;
            await Task.CompletedTask;
        }
    }

    private void ScheduleIdleDisconnect()
    {
        CancelIdleDisconnect();
        double timeoutMinutes = BotConfig.GetDouble("visualPlayer.inactivityTimeout", 2.0);
        if (timeoutMinutes <= 0)
            return;

        _idleDisconnectCts = new CancellationTokenSource();
        CancellationToken token = _idleDisconnectCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(timeoutMinutes), token).ConfigureAwait(false);
                if (!token.IsCancellationRequested && State == PlayerState.NotPlaying && Queue.Count == 0)
                {
                    Logs.Info($"Idle timeout reached for guild {GuildId}, disconnecting...");
                    await DisconnectAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when playback resumes.
            }
            catch (Exception ex)
            {
                Logs.Error($"Idle disconnect failed: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void CancelIdleDisconnect()
    {
        _idleDisconnectCts?.Cancel();
        _idleDisconnectCts?.Dispose();
        _idleDisconnectCts = null;
    }

    private static Process StartFfmpeg(Track track)
    {
        if (string.IsNullOrWhiteSpace(track.PlaybackUrl))
            throw new InvalidOperationException($"Track has no playback URL: {track.Title}");

        ProcessStartInfo startInfo = new()
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-reconnect");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-reconnect_streamed");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-reconnect_delay_max");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(track.PlaybackUrl);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("48000");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("s16le");
        startInfo.ArgumentList.Add("pipe:1");

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
    }

    private static async Task DrainErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(error))
                Logs.Debug($"ffmpeg: {error.Trim()}");
        }
        catch
        {
            // Non-critical diagnostics only.
        }
    }

    private static void ApplyVolume(byte[] buffer, int bytesRead, float volume)
    {
        if (Math.Abs(volume - 1f) < 0.001f)
            return;

        for (int i = 0; i + 1 < bytesRead; i += 2)
        {
            short sample = BitConverter.ToInt16(buffer, i);
            int scaled = (int)(sample * volume);
            scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            buffer[i] = (byte)(scaled & 0xff);
            buffer[i + 1] = (byte)((scaled >> 8) & 0xff);
        }
    }

    private TimeSpan GetCurrentPosition()
    {
        lock (_stateLock)
        {
            if (_trackStartedAt is null)
                return _positionOffset;

            return _positionOffset + (DateTimeOffset.UtcNow - _trackStartedAt.Value);
        }
    }

    private static TimeSpan GetDuration(CustomTrackQueueItem item)
        => item.SourceTrack.DurationMs > 0 ? TimeSpan.FromMilliseconds(item.SourceTrack.DurationMs) : TimeSpan.Zero;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelIdleDisconnect();
        _playbackCts?.Cancel();
        _ffmpegProcess?.Dispose();
        _audioClient?.Dispose();
        _playbackLock.Dispose();
        _playbackCts?.Dispose();
    }
}

public sealed record LocalAudioTrack(TimeSpan Duration);
public sealed record LocalPlayerPosition(TimeSpan Position);

public sealed class FfmpegTrackQueue : IEnumerable<CustomTrackQueueItem>
{
    private readonly List<CustomTrackQueueItem> _items = [];
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _items.Count; } }

    public Task AddAsync(CustomTrackQueueItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Add(item);
        return Task.CompletedTask;
    }

    public void Add(CustomTrackQueueItem item)
    {
        lock (_lock) _items.Add(item);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Clear();
        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }

    public Task ShuffleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            Random random = new();
            for (int i = _items.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (_items[i], _items[j]) = (_items[j], _items[i]);
            }
        }
        return Task.CompletedTask;
    }

    public CustomTrackQueueItem? Dequeue()
    {
        lock (_lock)
        {
            if (_items.Count == 0)
                return null;

            CustomTrackQueueItem item = _items[0];
            _items.RemoveAt(0);
            return item;
        }
    }

    public IEnumerator<CustomTrackQueueItem> GetEnumerator()
    {
        lock (_lock) return _items.ToList().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class FfmpegPlayerManager
{
    private readonly ConcurrentDictionary<ulong, CustomLavaLinkPlayer> _players = new();

    public CustomLavaLinkPlayer? GetPlayer(ulong guildId)
        => _players.TryGetValue(guildId, out CustomLavaLinkPlayer? player) ? player : null;

    public CustomLavaLinkPlayer GetOrCreatePlayer(ulong guildId, ulong voiceChannelId, IVoiceChannel voiceChannel,
        ITextChannel? textChannel, IServiceProvider serviceProvider, float initialVolume)
        => _players.GetOrAdd(guildId, _ => new CustomLavaLinkPlayer(guildId, voiceChannelId, voiceChannel, textChannel, serviceProvider, initialVolume));

    public void RemovePlayer(ulong guildId)
    {
        if (_players.TryRemove(guildId, out CustomLavaLinkPlayer? player))
            player.Dispose();
    }
}
