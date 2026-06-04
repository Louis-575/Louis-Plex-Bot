using PlexBot.Core.Discord.Embeds;
using PlexBot.Core.Discord.Events;
using PlexBot.Core.Extensions;
using PlexBot.Core.Models.Players;
using PlexBot.Core.Services;
using PlexBot.Core.Services.LavaLink;
using PlexBot.Core.Services.Music;
using PlexBot.Core.Events;
using PlexBot.Core.Services.PlexApi;
using PlexBot.Utils;

namespace PlexBot.Main
{
    /// <summary>Extension methods for registering services with the DI container for centralized service configuration</summary>
    public static class ServiceRegistration
    {
        /// <summary>Adds all required services to the service collection for the application's dependency injection</summary>
        /// <param name="services">The service collection to add services to</param>
        /// <returns>The modified service collection</returns>
        public static IServiceCollection AddServices(this IServiceCollection services)
        {
            Logs.Init("Registering services");

            // Add HTTP client factory
            services.AddHttpClient();

            // Add Discord services
            AddDiscordServices(services);

            // Add Plex services
            AddPlexServices(services);

            // Add player services
            AddPlayerServices(services);

            // Add music provider services
            AddMusicProviderServices(services);

            // Add event bus
            services.AddSingleton<BotEventBus>();

            // Add extension services (must be last — extensions may depend on all above)
            AddExtensionServices(services);

            Logs.Init("Services registered");
            return services;
        }

        /// <summary>Configures Discord client, interaction service, and event handlers for bot communication</summary>
        /// <param name="services">The service collection to add services to</param>
        private static void AddDiscordServices(IServiceCollection services)
        {
            // Configure Discord client
            // LogSeverity.Debug ensures all Discord/player messages reach our Logs class,
            // which handles console filtering (LOGGING_LEVEL_ROOT) and always saves everything to file
            services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds
                    | GatewayIntents.GuildMessages
                    | GatewayIntents.GuildVoiceStates
                    | GatewayIntents.GuildMessageReactions
                    | GatewayIntents.DirectMessages
                    | GatewayIntents.MessageContent,
                AlwaysDownloadUsers = false,
                MessageCacheSize = 100,
                LogLevel = LogSeverity.Debug,
                // Use Discord's server time instead of the system clock for rate limit
                // calculations. Prevents clock skew in Docker from causing mismatches.
                UseSystemClock = false,
                // Disable local 3-second interaction deadline check that uses snowflake
                // timestamps — Docker clock drift can cause false rejections.
                UseInteractionSnowflakeDate = false,
                // Discord voice now requires DAVE-capable clients. The Docker image
                // builds libdave and copies it into the app directory for this.
                EnableVoiceDaveEncryption = true
            }));

            // Configure interaction service
            services.AddSingleton(provider => new InteractionService(
                provider.GetRequiredService<DiscordSocketClient>(),
                new InteractionServiceConfig
                {
                    DefaultRunMode = RunMode.Async,
                    LogLevel = LogSeverity.Debug
                }));
            services.AddSingleton<DiscordEventHandler>();
            services.AddSingleton<VisualPlayer>();
            services.AddSingleton<DiscordButtonBuilder>();
        }

        /// <summary>Registers Plex API services for authentication, data retrieval, and music functionality</summary>
        /// <param name="services">The service collection to add services to</param>
        private static void AddPlexServices(IServiceCollection services)
        {
            // Add Plex HTTP client
            services.AddHttpClient("PlexApi", client =>
            {
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.Timeout = TimeSpan.FromSeconds(30);
            });

            // Add Plex services
            services.AddSingleton<IPlexAuthService, PlexAuthService>();
            services.AddSingleton<IPlexApiService, PlexApiService>();
            services.AddSingleton<IPlexMusicService, PlexMusicService>();
            services.AddSingleton<IPlexSonicService, PlexSonicService>();
        }

        /// <summary>Configures local ffmpeg audio streaming services and player management for music playback</summary>
        /// <param name="services">The service collection to add services to</param>
        private static void AddPlayerServices(IServiceCollection services)
        {
            // Register options
            services.Configure<PlayerOptions>(options => {
                options.DefaultVolume = 0.2f;
                options.DisconnectAfterPlayback = false;
                options.InactivityTimeout = TimeSpan.FromMinutes(20);
                options.AnnounceNowPlaying = true;
                options.ShowThumbnails = true;
                options.DeleteOutdatedMessages = true;
                options.MaxQueueItemsToShow = 10;
                options.UsePremiumFeatures = false;
                options.DefaultRepeatMode = TrackRepeatMode.None;
            });
            // Add player services
            services.AddSingleton<ITrackPrefetchService, TrackPrefetchService>();
            services.AddSingleton<FfmpegPlayerManager>();
            services.AddSingleton<IPlayerService, PlayerService>();
            // Register the state manager as a singleton
            services.AddSingleton<VisualPlayerStateManager>();
            // Add caching for player artwork
            services.AddMemoryCache();
        }

        /// <summary>Registers the music provider registry and built-in Plex provider.
        /// Additional providers (YouTube, SoundCloud, etc.) are loaded via extensions.</summary>
        private static void AddMusicProviderServices(IServiceCollection services)
        {
            services.AddSingleton<MusicProviderRegistry>();
            services.AddSingleton<IMusicProvider, PlexMusicProvider>();
            services.AddSingleton<RadioSessionManager>();
        }

        /// <summary>Sets up the extension system with two-phase startup: discover and register services
        /// before the container is built, then initialize after</summary>
        /// <param name="services">The service collection to add services to</param>
        private static void AddExtensionServices(IServiceCollection services)
        {
            // Source: extension .csproj folders. Configurable for Docker where source is mounted elsewhere.
            string extensionsSourceDir = System.IO.Path.GetFullPath(
                EnvConfig.Get("EXTENSIONS_SOURCE_DIR", "Extensions"));
            // Bin: compiled extension DLLs go next to the host output
            string extensionsBinDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Extensions");

            ExtensionManager manager = new(extensionsSourceDir, extensionsBinDir);
            services.AddSingleton(manager);

            // Build, discover, and let extensions register services into THIS collection
            IReadOnlyList<Extension> extensions = manager.DiscoverAndInstantiateAsync()
                .GetAwaiter().GetResult();
            foreach (Extension ext in extensions)
            {
                ext.RegisterServices(services);
            }
        }
    }
}
