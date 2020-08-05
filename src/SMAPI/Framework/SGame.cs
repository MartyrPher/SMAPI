using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AppCenter.Crashes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Enums;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Events;
using StardewModdingAPI.Framework.Input;
using StardewModdingAPI.Framework.Networking;
using StardewModdingAPI.Framework.PerformanceMonitoring;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Rendering;
using StardewModdingAPI.Framework.StateTracking.Comparers;
using StardewModdingAPI.Framework.StateTracking.Snapshots;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Toolkit.Serialization;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Minigames;
using StardewValley.Mobile;
using StardewValley.Tools;
using xTile.Dimensions;
using xTile.Layers;
using xTile.Tiles;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using SObject = StardewValley.Object;

namespace StardewModdingAPI.Framework
{
    /// <summary>SMAPI's extension of the game's core <see cref="Game1"/>, used to inject events.</summary>
    internal class SGame : Game1
    {
        /*********
        ** Fields
        *********/
        /****
        ** SMAPI state
        ****/
        /// <summary>Encapsulates monitoring and logging for SMAPI.</summary>
        private readonly Monitor Monitor;

        /// <summary>Encapsulates monitoring and logging on the game's behalf.</summary>
        private readonly IMonitor MonitorForGame;

        /// <summary>Manages SMAPI events for mods.</summary>
        private readonly EventManager Events;

        /// <summary>Tracks the installed mods.</summary>
        private readonly ModRegistry ModRegistry;

        /// <summary>Manages deprecation warnings.</summary>
        private readonly DeprecationManager DeprecationManager;

        /// <summary>Tracks performance metrics.</summary>
        private readonly PerformanceMonitor PerformanceMonitor;

        /// <summary>The maximum number of consecutive attempts SMAPI should make to recover from a draw error.</summary>
        private readonly Countdown DrawCrashTimer = new Countdown(60); // 60 ticks = roughly one second

        /// <summary>The maximum number of consecutive attempts SMAPI should make to recover from an update error.</summary>
        private readonly Countdown UpdateCrashTimer = new Countdown(60); // 60 ticks = roughly one second

        /// <summary>The number of ticks until SMAPI should notify mods that the game has loaded.</summary>
        /// <remarks>Skipping a few frames ensures the game finishes initializing the world before mods try to change it.</remarks>
        private readonly Countdown AfterLoadTimer = new Countdown(5);

        /// <summary>Whether custom content was removed from the save data to avoid a crash.</summary>
        private bool IsSaveContentRemoved;

        /// <summary>Whether the game is saving and SMAPI has already raised <see cref="IGameLoopEvents.Saving"/>.</summary>
        private bool IsBetweenSaveEvents;

        /// <summary>Whether the game is creating the save file and SMAPI has already raised <see cref="IGameLoopEvents.SaveCreating"/>.</summary>
        private bool IsBetweenCreateEvents;

        /// <summary>A callback to invoke the first time *any* game content manager loads an asset.</summary>
        private readonly Action OnLoadingFirstAsset;

        /// <summary>A callback to invoke after the game finishes initializing.</summary>
        private readonly Action OnGameInitialized;

        /// <summary>A callback to invoke when the game exits.</summary>
        private readonly Action OnGameExiting;

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection;

        /// <summary>Encapsulates access to SMAPI core translations.</summary>
        private readonly Translator Translator;

        /// <summary>Propagates notification that SMAPI should exit.</summary>
        private readonly CancellationTokenSource CancellationToken;


        /****
        ** Game state
        ****/
        /// <summary>Monitors the entire game state for changes.</summary>
        private WatcherCore Watchers;

        /// <summary>A snapshot of the current <see cref="Watchers"/> state.</summary>
        private readonly WatcherSnapshot WatcherSnapshot = new WatcherSnapshot();

        /// <summary>Whether post-game-startup initialization has been performed.</summary>
        private bool IsInitialized;

        /// <summary>Whether the next content manager requested by the game will be for <see cref="Game1.content"/>.</summary>
        private bool NextContentManagerIsMain;

        private readonly IReflectedField<bool> DrawActiveClickableMenuField;
        private readonly IReflectedField<string> SpriteBatchBeginNextIDField;
        private readonly IReflectedField<bool> DrawHudField;
        private readonly IReflectedField<List<Farmer>> FarmerShadowsField;
        private readonly IReflectedField<StringBuilder> DebugStringBuilderField;
        private readonly IReflectedField<BlendState> LightingBlendField;

        private readonly IReflectedMethod SpriteBatchBeginMethod;
        private readonly IReflectedMethod _spriteBatchBeginMethod;
        private readonly IReflectedMethod _spriteBatchEndMethod;
        private readonly IReflectedMethod DrawLoadingDotDotDotMethod;
        private readonly IReflectedMethod CheckToReloadGameLocationAfterDrawFailMethod;
        private readonly IReflectedMethod DrawTapToMoveTargetMethod;
        private readonly IReflectedMethod DrawDayTimeMoneyBoxMethod;
        private readonly IReflectedMethod DrawAfterMapMethod;
        private readonly IReflectedMethod DrawToolbarMethod;
        private readonly IReflectedMethod DrawVirtualJoypadMethod;
        private readonly IReflectedMethod DrawMenuMouseCursorMethod;
        private readonly IReflectedMethod DrawFadeToBlackFullScreenRectMethod;
        private readonly IReflectedMethod DrawChatBoxMethod;
        private readonly IReflectedMethod DrawDialogueBoxForPinchZoomMethod;
        private readonly IReflectedMethod DrawUnscaledActiveClickableMenuForPinchZoomMethod;
        private readonly IReflectedMethod DrawNativeScaledActiveClickableMenuForPinchZoomMethod;

        // ReSharper disable once InconsistentNaming
        private readonly IReflectedMethod DrawHUDMessagesMethod;

        // ReSharper disable once InconsistentNaming
        private readonly IReflectedMethod DrawTutorialUIMethod;
        private readonly IReflectedMethod DrawGreenPlacementBoundsMethod;

        /*********
        ** Accessors
        *********/
        /// <summary>Static state to use while <see cref="Game1"/> is initializing, which happens before the <see cref="SGame"/> constructor runs.</summary>
        internal static SGameConstructorHack ConstructorHack { get; set; }

        /// <summary>The number of update ticks which have already executed. This is similar to <see cref="Game1.ticks"/>, but incremented more consistently for every tick.</summary>
        internal static uint TicksElapsed { get; private set; }

        /// <summary>SMAPI's content manager.</summary>
        public ContentCoordinator ContentCore { get; private set; }

        /// <summary>Manages console commands.</summary>
        public CommandManager CommandManager { get; } = new CommandManager();

        /// <summary>Manages input visible to the game.</summary>
        public SInputState Input => (SInputState) Game1.input;

        /// <summary>The game's core multiplayer utility.</summary>
        public SMultiplayer Multiplayer => (SMultiplayer) Game1.multiplayer;

        /// <summary>A list of queued commands to execute.</summary>
        /// <remarks>This property must be threadsafe, since it's accessed from a separate console input thread.</remarks>
        public ConcurrentQueue<string> CommandQueue { get; } = new ConcurrentQueue<string>();

        public static SGame instance;

        /// <summary>Asset interceptors added or removed since the last tick.</summary>
        private readonly List<AssetInterceptorChange> ReloadAssetInterceptorsQueue = new List<AssetInterceptorChange>();

        public bool IsGameSuspended;

        public bool IsAfterInitialize = false;
        /*********
        ** Protected methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Encapsulates monitoring and logging for SMAPI.</param>
        /// <param name="monitorForGame">Encapsulates monitoring and logging on the game's behalf.</param>
        /// <param name="reflection">Simplifies access to private game code.</param>
        /// <param name="translator">Encapsulates access to arbitrary translations.</param>
        /// <param name="eventManager">Manages SMAPI events for mods.</param>
        /// <param name="jsonHelper">Encapsulates SMAPI's JSON file parsing.</param>
        /// <param name="modRegistry">Tracks the installed mods.</param>
        /// <param name="deprecationManager">Manages deprecation warnings.</param>
        /// <param name="performanceMonitor">Tracks performance metrics.</param>
        /// <param name="onGameInitialized">A callback to invoke after the game finishes initializing.</param>
        /// <param name="onGameExiting">A callback to invoke when the game exits.</param>
        /// <param name="cancellationToken">Propagates notification that SMAPI should exit.</param>
        /// <param name="logNetworkTraffic">Whether to log network traffic.</param>
        internal SGame(Monitor monitor, IMonitor monitorForGame, Reflector reflection, Translator translator, EventManager eventManager, JsonHelper jsonHelper, ModRegistry modRegistry, DeprecationManager deprecationManager, PerformanceMonitor performanceMonitor, Action onGameInitialized, Action onGameExiting, CancellationTokenSource cancellationToken, bool logNetworkTraffic)
        {
            this.OnLoadingFirstAsset = SGame.ConstructorHack.OnLoadingFirstAsset;
            SGame.ConstructorHack = null;

            // check expectations
            if (this.ContentCore == null)
                throw new InvalidOperationException($"The game didn't initialize its first content manager before SMAPI's {nameof(SGame)} constructor. This indicates an incompatible lifecycle change.");

            // init XNA
            Game1.graphics.GraphicsProfile = GraphicsProfile.HiDef;

            // init SMAPI
            this.Monitor = monitor;
            this.MonitorForGame = monitorForGame;
            this.Events = eventManager;
            this.ModRegistry = modRegistry;
            this.Reflection = reflection;
            this.Translator = translator;
            this.DeprecationManager = deprecationManager;
            this.PerformanceMonitor = performanceMonitor;
            this.OnGameInitialized = onGameInitialized;
            this.OnGameExiting = onGameExiting;
            Game1.input = new SInputState();
            Game1.multiplayer = new SMultiplayer(monitor, eventManager, jsonHelper, modRegistry, reflection, this.OnModMessageReceived, logNetworkTraffic);
            Game1.hooks = new SModHooks(this.OnNewDayAfterFade);
            this.CancellationToken = cancellationToken;

            // init observables
            Game1.locations = new ObservableCollection<GameLocation>();
            SGame.instance = this;
            this.DrawActiveClickableMenuField = this.Reflection.GetField<bool>(this, "_drawActiveClickableMenu");
            this.SpriteBatchBeginNextIDField = this.Reflection.GetField<string>(typeof(Game1), "_spriteBatchBeginNextID");
            this.DrawHudField = this.Reflection.GetField<bool>(this, "_drawHUD");
            this.FarmerShadowsField = this.Reflection.GetField<List<Farmer>>(this, "_farmerShadows");
            this.DebugStringBuilderField = this.Reflection.GetField<StringBuilder>(typeof(Game1), "_debugStringBuilder");
            this.LightingBlendField = this.Reflection.GetField<BlendState>(this, "lightingBlend");
            this.SpriteBatchBeginMethod = this.Reflection.GetMethod(this, "SpriteBatchBegin", new[] {typeof(float)});
            this._spriteBatchBeginMethod = this.Reflection.GetMethod(this, "_spriteBatchBegin", new[] {typeof(SpriteSortMode), typeof(BlendState), typeof(SamplerState), typeof(DepthStencilState), typeof(RasterizerState), typeof(Effect), typeof(Matrix)});
            this._spriteBatchEndMethod = this.Reflection.GetMethod(this, "_spriteBatchEnd", new Type[] { });
            this.DrawLoadingDotDotDotMethod = this.Reflection.GetMethod(this, "DrawLoadingDotDotDot", new[] {typeof(GameTime)});
            this.CheckToReloadGameLocationAfterDrawFailMethod = this.Reflection.GetMethod(this, "CheckToReloadGameLocationAfterDrawFail", new[] {typeof(string), typeof(Exception)});
            this.DrawTapToMoveTargetMethod = this.Reflection.GetMethod(this, "DrawTapToMoveTarget", new Type[] { });
            this.DrawDayTimeMoneyBoxMethod = this.Reflection.GetMethod(this, "DrawDayTimeMoneyBox", new Type[] { });
            this.DrawAfterMapMethod = this.Reflection.GetMethod(this, "DrawAfterMap", new Type[] { });
            this.DrawToolbarMethod = this.Reflection.GetMethod(this, "DrawToolbar", new Type[] { });
            this.DrawVirtualJoypadMethod = this.Reflection.GetMethod(this, "DrawVirtualJoypad", new Type[] { });
            this.DrawMenuMouseCursorMethod = this.Reflection.GetMethod(this, "DrawMenuMouseCursor", new Type[] { });
            this.DrawFadeToBlackFullScreenRectMethod = this.Reflection.GetMethod(this, "DrawFadeToBlackFullScreenRect", new Type[] { });
            this.DrawChatBoxMethod = this.Reflection.GetMethod(this, "DrawChatBox", new Type[] { });
            this.DrawDialogueBoxForPinchZoomMethod = this.Reflection.GetMethod(this, "DrawDialogueBoxForPinchZoom", new Type[] { });
            this.DrawUnscaledActiveClickableMenuForPinchZoomMethod = this.Reflection.GetMethod(this, "DrawUnscaledActiveClickableMenuForPinchZoom", new Type[] { });
            this.DrawNativeScaledActiveClickableMenuForPinchZoomMethod = this.Reflection.GetMethod(this, "DrawNativeScaledActiveClickableMenuForPinchZoom", new Type[] { });
            this.DrawHUDMessagesMethod = this.Reflection.GetMethod(this, "DrawHUDMessages", new Type[] { });
            this.DrawTutorialUIMethod = this.Reflection.GetMethod(this, "DrawTutorialUI", new Type[] { });
            this.DrawGreenPlacementBoundsMethod = this.Reflection.GetMethod(this, "DrawGreenPlacementBounds", new Type[] { });
        }

        /// <summary>Load content when the game is launched.</summary>
        protected override void LoadContent()
        {
            // load content
            base.LoadContent();
            Game1.mapDisplayDevice = new SDisplayDevice(Game1.content, this.GraphicsDevice);

            // log GPU info
#if SMAPI_FOR_WINDOWS
            this.Monitor.Log($"Running on GPU: {this.GraphicsDevice?.Adapter?.Description ?? "<unknown>"}");
#endif
        }

        /// <summary>Initialize just before the game's first update tick.</summary>
        private void InitializeAfterGameStarted()
        {
            // set initial state
            this.Input.TrueUpdate();

            // init watchers
            this.Watchers = new WatcherCore(this.Input);

            // raise callback
            this.OnGameInitialized();
        }

        /// <summary>Perform cleanup logic when the game exits.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="args">The event args.</param>
        /// <remarks>This overrides the logic in <see cref="Game1.exitEvent"/> to let SMAPI clean up before exit.</remarks>
        protected override void OnExiting(object sender, EventArgs args)
        {
            Game1.multiplayer.Disconnect(StardewValley.Multiplayer.DisconnectType.ClosedGame);
            this.OnGameExiting?.Invoke();
        }

        /// <summary>A callback invoked before <see cref="Game1.newDayAfterFade"/> runs.</summary>
        protected void OnNewDayAfterFade()
        {
            this.Events.DayEnding.RaiseEmpty();
        }

        /// <summary>A callback invoked when a mod message is received.</summary>
        /// <param name="message">The message to deliver to applicable mods.</param>
        private void OnModMessageReceived(ModMessageModel message)
        {
            // get mod IDs to notify
            HashSet<string> modIDs = new HashSet<string>(message.ToModIDs ?? this.ModRegistry.GetAll().Select(p => p.Manifest.UniqueID), StringComparer.OrdinalIgnoreCase);
            if (message.FromPlayerID == Game1.player?.UniqueMultiplayerID)
                modIDs.Remove(message.FromModID); // don't send a broadcast back to the sender

            // raise events
            this.Events.ModMessageReceived.Raise(new ModMessageReceivedEventArgs(message), mod => mod != null && modIDs.Contains(mod.Manifest.UniqueID));
        }

        /// <summary>A callback invoked when custom content is removed from the save data to avoid a crash.</summary>
        internal void OnSaveContentRemoved()
        {
            this.IsSaveContentRemoved = true;
        }

        /// <summary>A callback invoked when the game's low-level load stage changes.</summary>
        /// <param name="newStage">The new load stage.</param>
        internal void OnLoadStageChanged(LoadStage newStage)
        {
            // nothing to do
            if (newStage == Context.LoadStage)
                return;

            // update data
            LoadStage oldStage = Context.LoadStage;
            Context.LoadStage = newStage;
            this.Monitor.VerboseLog($"Context: load stage changed to {newStage}");
            if (newStage == LoadStage.None)
            {
                this.Monitor.Log("Context: returned to title", LogLevel.Trace);
                this.OnReturnedToTitle();
            }

            // raise events
            this.Events.LoadStageChanged.Raise(new LoadStageChangedEventArgs(oldStage, newStage));
            if (newStage == LoadStage.None)
                this.Events.ReturnedToTitle.RaiseEmpty();
        }

        /// <summary>A callback invoked when a mod adds or removes an asset interceptor.</summary>
        /// <param name="mod">The mod which added or removed interceptors.</param>
        /// <param name="added">The added interceptors.</param>
        /// <param name="removed">The removed interceptors.</param>
        internal void OnAssetInterceptorsChanged(IModMetadata mod, IEnumerable added, IEnumerable removed)
        {
            if (added != null)
            {
                foreach (object instance in added)
                    this.ReloadAssetInterceptorsQueue.Add(new AssetInterceptorChange(mod, instance, wasAdded: true));
            }

            if (removed != null)
            {
                foreach (object instance in removed)
                    this.ReloadAssetInterceptorsQueue.Add(new AssetInterceptorChange(mod, instance, wasAdded: false));
            }
        }

        /// <summary>Perform cleanup when the game returns to the title screen.</summary>
        private void OnReturnedToTitle()
        {
            this.Multiplayer.CleanupOnMultiplayerExit();

            if (!(Game1.mapDisplayDevice is SDisplayDevice))
                Game1.mapDisplayDevice = new SDisplayDevice(Game1.content, this.GraphicsDevice);
        }

        /// <summary>Constructor a content manager to read XNB files.</summary>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        protected override LocalizedContentManager CreateContentManager(IServiceProvider serviceProvider, string rootDirectory)
        {
            // Game1._temporaryContent initializing from SGame constructor
            // NOTE: this method is called before the SGame constructor runs. Don't depend on anything being initialized at this point.
            if (this.ContentCore == null)
            {
                this.ContentCore = new ContentCoordinator(serviceProvider, rootDirectory, Thread.CurrentThread.CurrentUICulture, SGame.ConstructorHack.Monitor, SGame.ConstructorHack.Reflection, SGame.ConstructorHack.JsonHelper, this.OnLoadingFirstAsset ?? SGame.ConstructorHack?.OnLoadingFirstAsset);
                this.NextContentManagerIsMain = true;
                return this.ContentCore.CreateGameContentManager("Game1._temporaryContent");
            }

            // Game1.content initializing from LoadContent
            if (this.NextContentManagerIsMain)
            {
                this.NextContentManagerIsMain = false;
                SGameConsole.Instance.InitializeContent(this.ContentCore.MainContentManager);
                return this.ContentCore.MainContentManager;
            }

            // any other content manager
            return this.ContentCore.CreateGameContentManager("(generated)");
        }

        /// <summary>The method called when the game is updating its state. This happens roughly 60 times per second.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        protected override void Update(GameTime gameTime)
        {
            if (this.IsGameSuspended)
            {
                if (!this.IsAfterInitialize)
                    this.IsAfterInitialize = true;

                return;
            }

            var events = this.Events;

            try
            {
                this.DeprecationManager.PrintQueued();
                this.PerformanceMonitor.PrintQueuedAlerts();

                /*********
                ** First-tick initialization
                *********/
                if (!this.IsInitialized)
                {
                    this.IsInitialized = true;
                    this.InitializeAfterGameStarted();
                }

                /*********
                ** Update input
                *********/
                // This should *always* run, even when suppressing mod events, since the game uses
                // this too. For example, doing this after mod event suppression would prevent the
                // user from doing anything on the overnight shipping screen.
                SInputState inputState = this.Input;
                if (this.IsActive)
                    inputState.TrueUpdate();

                /*********
                ** Special cases
                *********/
                // Abort if SMAPI is exiting.
                if (this.CancellationToken.IsCancellationRequested)
                {
                    this.Monitor.Log("SMAPI shutting down: aborting update.", LogLevel.Trace);
                    return;
                }

                // Run async tasks synchronously to avoid issues due to mod events triggering
                // concurrently with game code.
                bool saveParsed = false;
                if (Game1.currentLoader != null)
                {
                    //this.Monitor.Log("Game loader synchronizing...", LogLevel.Trace);
                    while (Game1.currentLoader?.MoveNext() == true)
                    {
                        // raise load stage changed
                        switch (Game1.currentLoader.Current)
                        {
                            case 1:
                            case 24:
                                return;

                            case 20:
                                if (!saveParsed && SaveGame.loaded != null)
                                {
                                    this.Monitor.Log("SaveParsed", LogLevel.Debug);
                                    saveParsed = true;
                                    this.OnLoadStageChanged(LoadStage.SaveParsed);
                                }

                                return;

                            case 36:
                                this.Monitor.Log("SaveLoadedBasicInfo", LogLevel.Debug);
                                this.OnLoadStageChanged(LoadStage.SaveLoadedBasicInfo);
                                break;

                            case 50:
                                this.Monitor.Log("SaveLoadedLocations", LogLevel.Debug);
                                this.OnLoadStageChanged(LoadStage.SaveLoadedLocations);
                                break;

                            default:
                                if (Game1.gameMode == Game1.playingGameMode)
                                {
                                    this.Monitor.Log("Preloaded", LogLevel.Debug);
                                    this.OnLoadStageChanged(LoadStage.Preloaded);
                                }

                                break;
                        }
                    }

                    Game1.currentLoader = null;
                    this.Monitor.Log("Game loader done.");
                }

                if (Game1._newDayTask?.Status == TaskStatus.Created)
                {
                    this.Monitor.Log("New day task synchronizing...", LogLevel.Trace);
                    Game1._newDayTask.RunSynchronously();
                    this.Monitor.Log("New day task done.", LogLevel.Trace);
                }

                // While a background task is in progress, the game may make changes to the game
                // state while mods are running their code. This is risky, because data changes can
                // conflict (e.g. collection changed during enumeration errors) and data may change
                // unexpectedly from one mod instruction to the next.
                //
                // Therefore we can just run Game1.Update here without raising any SMAPI events. There's
                // a small chance that the task will finish after we defer but before the game checks,
                // which means technically events should be raised, but the effects of missing one
                // update tick are negligible and not worth the complications of bypassing Game1.Update.
                if (Game1._newDayTask != null || Game1.gameMode == Game1.loadingMode)
                {
                    events.UnvalidatedUpdateTicking.RaiseEmpty();
                    SGame.TicksElapsed++;
                    base.Update(gameTime);
                    events.UnvalidatedUpdateTicked.RaiseEmpty();
                    return;
                }

                // Raise minimal events while saving.
                // While the game is writing to the save file in the background, mods can unexpectedly
                // fail since they don't have exclusive access to resources (e.g. collection changed
                // during enumeration errors). To avoid problems, events are not invoked while a save
                // is in progress. It's safe to raise SaveEvents.BeforeSave as soon as the menu is
                // opened (since the save hasn't started yet), but all other events should be suppressed.
                if (Context.IsSaving)
                {
                    // raise before-create
                    if (!Context.IsWorldReady && !this.IsBetweenCreateEvents)
                    {
                        this.IsBetweenCreateEvents = true;
                        this.Monitor.Log("Context: before save creation.", LogLevel.Trace);
                        events.SaveCreating.RaiseEmpty();
                    }

                    // raise before-save
                    if (Context.IsWorldReady && !this.IsBetweenSaveEvents)
                    {
                        this.IsBetweenSaveEvents = true;
                        this.Monitor.Log("Context: before save.", LogLevel.Trace);
                        events.Saving.RaiseEmpty();
                    }

                    // suppress non-save events
                    events.UnvalidatedUpdateTicking.RaiseEmpty();
                    SGame.TicksElapsed++;
                    base.Update(gameTime);
                    events.UnvalidatedUpdateTicked.RaiseEmpty();
                    return;
                }

                /*********
                ** Reload assets when interceptors are added/removed
                *********/
                if (this.ReloadAssetInterceptorsQueue.Any())
                {
                    // get unique interceptors
                    AssetInterceptorChange[] interceptors = this.ReloadAssetInterceptorsQueue
                        .GroupBy(p => p.Instance, new ObjectReferenceComparer<object>())
                        .Select(p => p.First())
                        .ToArray();
                    this.ReloadAssetInterceptorsQueue.Clear();

                    // log summary
                    this.Monitor.Log("Invalidating cached assets for new editors & loaders...");
                    this.Monitor.Log(
                        "   changed: "
                        + string.Join(", ",
                            interceptors
                                .GroupBy(p => p.Mod)
                                .OrderBy(p => p.Key.DisplayName)
                                .Select(modGroup =>
                                    $"{modGroup.Key.DisplayName} ("
                                    + string.Join(", ", modGroup.GroupBy(p => p.WasAdded).ToDictionary(p => p.Key, p => p.Count()).Select(p => $"{(p.Key ? "added" : "removed")} {p.Value}"))
                                    + ")"
                                )
                        )
                    );

                    // reload affected assets
                    this.ContentCore.InvalidateCache(asset => interceptors.Any(p => p.CanIntercept(asset)));
                }

                /*********
                ** Execute commands
                *********/
                while (this.CommandQueue.TryDequeue(out string rawInput))
                {
                    // parse command
                    string name;
                    string[] args;
                    Command command;
                    try
                    {
                        if (!this.CommandManager.TryParse(rawInput, out name, out args, out command))
                        {
                            this.Monitor.Log("Unknown command; type 'help' for a list of available commands.", LogLevel.Error);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Failed parsing that command:\n{ex.GetLogSummary()}", LogLevel.Error);
                        continue;
                    }

                    // execute command
                    try
                    {
                        command.Callback.Invoke(name, args);
                    }
                    catch (Exception ex)
                    {
                        if (command.Mod != null)
                            command.Mod.LogAsMod($"Mod failed handling that command:\n{ex.GetLogSummary()}", LogLevel.Error);
                        else
                            this.Monitor.Log($"Failed handling that command:\n{ex.GetLogSummary()}", LogLevel.Error);
                    }
                }

                /*********
                ** Update context
                *********/
                bool wasWorldReady = Context.IsWorldReady;
                if (Context.IsWorldReady && !Context.IsSaveLoaded || Game1.exitToTitle)
                {
                    Context.IsWorldReady = false;
                    this.AfterLoadTimer.Reset();
                }
                else if (Context.IsSaveLoaded && this.AfterLoadTimer.Current > 0 && Game1.currentLocation != null)
                {
                    if (Game1.dayOfMonth != 0) // wait until new-game intro finishes (world not fully initialized yet)
                        this.AfterLoadTimer.Decrement();
                    Context.IsWorldReady = this.AfterLoadTimer.Current == 0;
                }

                /*********
                ** Update watchers
                **   (Watchers need to be updated, checked, and reset in one go so we can detect any changes mods make in event handlers.)
                *********/
                this.Watchers.Update();
                this.WatcherSnapshot.Update(this.Watchers);
                this.Watchers.Reset();
                WatcherSnapshot state = this.WatcherSnapshot;

                /*********
                ** Display in-game warnings
                *********/
                // save content removed
                if (this.IsSaveContentRemoved && Context.IsWorldReady)
                {
                    this.IsSaveContentRemoved = false;
                    Game1.addHUDMessage(new HUDMessage(this.Translator.Get("warn.invalid-content-removed"), HUDMessage.error_type));
                }

                /*********
                ** Pre-update events
                *********/
                {
                    /*********
                    ** Save created/loaded events
                    *********/
                    if (this.IsBetweenCreateEvents)
                    {
                        // raise after-create
                        this.IsBetweenCreateEvents = false;
                        this.Monitor.Log($"Context: after save creation, starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                        this.OnLoadStageChanged(LoadStage.CreatedSaveFile);
                        events.SaveCreated.RaiseEmpty();
                    }

                    if (this.IsBetweenSaveEvents)
                    {
                        // raise after-save
                        this.IsBetweenSaveEvents = false;
                        this.Monitor.Log($"Context: after save, starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}.", LogLevel.Trace);
                        events.Saved.RaiseEmpty();
                        events.DayStarted.RaiseEmpty();
                    }

                    /*********
                    ** Locale changed events
                    *********/
                    if (state.Locale.IsChanged)
                        this.Monitor.Log($"Context: locale set to {state.Locale.New}.", LogLevel.Trace);

                    /*********
                    ** Load / return-to-title events
                    *********/
                    if (wasWorldReady && !Context.IsWorldReady)
                        this.OnLoadStageChanged(LoadStage.None);
                    else if (Context.IsWorldReady && Context.LoadStage != LoadStage.Ready)
                    {
                        // print context
                        string context = $"Context: loaded save '{Constants.SaveFolderName}', starting {Game1.currentSeason} {Game1.dayOfMonth} Y{Game1.year}, locale set to {this.ContentCore.Language}.";
                        if (Context.IsMultiplayer)
                        {
                            int onlineCount = Game1.getOnlineFarmers().Count();
                            context += $" {(Context.IsMainPlayer ? "Main player" : "Farmhand")} with {onlineCount} {(onlineCount == 1 ? "player" : "players")} online.";
                        }
                        else
                            context += " Single-player.";

                        this.Monitor.Log(context, LogLevel.Trace);

                        // raise events
                        this.OnLoadStageChanged(LoadStage.Ready);
                        events.SaveLoaded.RaiseEmpty();
                        events.DayStarted.RaiseEmpty();
                    }

                    /*********
                    ** Window events
                    *********/
                    // Here we depend on the game's viewport instead of listening to the Window.Resize
                    // event because we need to notify mods after the game handles the resize, so the
                    // game's metadata (like Game1.viewport) are updated. That's a bit complicated
                    // since the game adds & removes its own handler on the fly.
                    if (state.WindowSize.IsChanged)
                    {
                        if (this.Monitor.IsVerbose)
                            this.Monitor.Log($"Events: window size changed to {state.WindowSize.New}.", LogLevel.Trace);

                        events.WindowResized.Raise(new WindowResizedEventArgs(state.WindowSize.Old, state.WindowSize.New));
                    }

                    /*********
                    ** Input events (if window has focus)
                    *********/
                    if (this.IsActive)
                    {
                        // raise events
                        bool isChatInput = Game1.IsChatting || Context.IsMultiplayer && Context.IsWorldReady && Game1.activeClickableMenu == null && Game1.currentMinigame == null && inputState.IsAnyDown(Game1.options.chatButton);
                        if (!isChatInput)
                        {
                            ICursorPosition cursor = this.Input.CursorPosition;

                            // raise cursor moved event
                            if (state.Cursor.IsChanged)
                                events.CursorMoved.Raise(new CursorMovedEventArgs(state.Cursor.Old, state.Cursor.New));

                            // raise mouse wheel scrolled
                            if (state.MouseWheelScroll.IsChanged)
                            {
                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log($"Events: mouse wheel scrolled to {state.MouseWheelScroll.New}.", LogLevel.Trace);
                                events.MouseWheelScrolled.Raise(new MouseWheelScrolledEventArgs(cursor, state.MouseWheelScroll.Old, state.MouseWheelScroll.New));
                            }

                            // raise input button events
                            foreach (var pair in inputState.LastButtonStates)
                            {
                                SButton button = pair.Key;
                                SButtonState status = pair.Value;

                                if (status == SButtonState.Pressed)
                                {
                                    if (this.Monitor.IsVerbose)
                                        this.Monitor.Log($"Events: button {button} pressed.", LogLevel.Trace);

                                    events.ButtonPressed.Raise(new ButtonPressedEventArgs(button, cursor, inputState));
                                }
                                else if (status == SButtonState.Released)
                                {
                                    if (this.Monitor.IsVerbose)
                                        this.Monitor.Log($"Events: button {button} released.", LogLevel.Trace);

                                    events.ButtonReleased.Raise(new ButtonReleasedEventArgs(button, cursor, inputState));
                                }
                            }
                        }
                    }

                    /*********
                    ** Menu events
                    *********/
                    if (state.ActiveMenu.IsChanged)
                    {
                        IClickableMenu was = state.ActiveMenu.Old;
                        IClickableMenu now = state.ActiveMenu.New;

                        if (this.Monitor.IsVerbose)
                            this.Monitor.Log($"Context: menu changed from {state.ActiveMenu.Old?.GetType().FullName ?? "none"} to {state.ActiveMenu.New?.GetType().FullName ?? "none"}.", LogLevel.Trace);

                        // raise menu events
                        events.MenuChanged.Raise(new MenuChangedEventArgs(was, now));

                        if (now is GameMenu gameMenu)
                        {
                            foreach (IClickableMenu menu in gameMenu.pages)
                            {
                                OptionsPage optionsPage = menu as OptionsPage;
                                if (optionsPage != null)
                                {
                                    List<OptionsElement> options = this.Reflection.GetField<List<OptionsElement>>(optionsPage, "options").GetValue();
                                    options.Insert(0, new OptionsButton("Console", () => SGameConsole.Instance.Show()));
                                    this.Reflection.GetMethod(optionsPage, "updateContentPositions").Invoke();
                                }
                            }
                        }
                        else if (now is ShopMenu shopMenu)
                        {
                            Dictionary<ISalable, int[]> itemPriceAndStock = this.Reflection.GetField<Dictionary<ISalable, int[]>>(shopMenu, "itemPriceAndStock").GetValue();
                            if (shopMenu.forSaleButtons.Count < itemPriceAndStock.Keys.Select(item => item.Name).Distinct().Count())
                            {
                                this.Monitor.Log("Shop Menu Pop");
                                Game1.activeClickableMenu = new ShopMenu(itemPriceAndStock,
                                    this.Reflection.GetField<int>(shopMenu, "currency").GetValue(),
                                    this.Reflection.GetField<string>(shopMenu, "personName").GetValue(),
                                    shopMenu.onPurchase, shopMenu.onSell, shopMenu.storeContext);
                            }
                        }
                    }

                    /*********
                    ** World & player events
                    *********/
                    if (Context.IsWorldReady)
                    {
                        bool raiseWorldEvents = !state.SaveID.IsChanged; // don't report changes from unloaded => loaded

                        // location list changes
                        if (state.Locations.LocationList.IsChanged && (events.LocationListChanged.HasListeners() || this.Monitor.IsVerbose))
                        {
                            var added = state.Locations.LocationList.Added.ToArray();
                            var removed = state.Locations.LocationList.Removed.ToArray();
                            if (this.Monitor.IsVerbose)
                            {
                                string addedText = added.Any() ? string.Join(", ", added.Select(p => p.Name)) : "none";
                                string removedText = removed.Any() ? string.Join(", ", removed.Select(p => p.Name)) : "none";
                                this.Monitor.Log($"Context: location list changed (added {addedText}; removed {removedText}).", LogLevel.Trace);
                            }


                            events.LocationListChanged.Raise(new LocationListChangedEventArgs(added, removed));
                        }

                        // raise location contents changed
                        if (raiseWorldEvents)
                        {
                            foreach (LocationSnapshot locState in state.Locations.Locations)
                            {
                                var location = locState.Location;

                                // buildings changed
                                if (locState.Buildings.IsChanged)
                                    events.BuildingListChanged.Raise(new BuildingListChangedEventArgs(location, locState.Buildings.Added, locState.Buildings.Removed));

                                // debris changed
                                if (locState.Debris.IsChanged)
                                    events.DebrisListChanged.Raise(new DebrisListChangedEventArgs(location, locState.Debris.Added, locState.Debris.Removed));

                                // large terrain features changed
                                if (locState.LargeTerrainFeatures.IsChanged)
                                    events.LargeTerrainFeatureListChanged.Raise(new LargeTerrainFeatureListChangedEventArgs(location, locState.LargeTerrainFeatures.Added, locState.LargeTerrainFeatures.Removed));

                                // NPCs changed
                                if (locState.Npcs.IsChanged)
                                    events.NpcListChanged.Raise(new NpcListChangedEventArgs(location, locState.Npcs.Added, locState.Npcs.Removed));

                                // objects changed
                                if (locState.Objects.IsChanged)
                                    events.ObjectListChanged.Raise(new ObjectListChangedEventArgs(location, locState.Objects.Added, locState.Objects.Removed));

                                // chest items changed
                                if (events.ChestInventoryChanged.HasListeners())
                                {
                                    foreach (var pair in locState.ChestItems)
                                    {
                                        SnapshotItemListDiff diff = pair.Value;
                                        events.ChestInventoryChanged.Raise(new ChestInventoryChangedEventArgs(pair.Key, location, added: diff.Added, removed: diff.Removed, quantityChanged: diff.QuantityChanged));
                                    }
                                }

                                // terrain features changed
                                if (locState.TerrainFeatures.IsChanged)
                                    events.TerrainFeatureListChanged.Raise(new TerrainFeatureListChangedEventArgs(location, locState.TerrainFeatures.Added, locState.TerrainFeatures.Removed));
                            }
                        }

                        // raise time changed
                        if (raiseWorldEvents && state.Time.IsChanged)
                            events.TimeChanged.Raise(new TimeChangedEventArgs(state.Time.Old, state.Time.New));

                        // raise player events
                        if (raiseWorldEvents)
                        {
                            PlayerSnapshot playerState = state.CurrentPlayer;
                            Farmer player = playerState.Player;

                            // raise current location changed
                            if (playerState.Location.IsChanged)
                            {
                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log($"Context: set location to {playerState.Location.New}.", LogLevel.Trace);
                                // Fix tapToMove null pointer error
                                if (playerState.Location.New.tapToMove == null && playerState.Location.New.map != null)
                                {
                                    playerState.Location.New.tapToMove = new TapToMove(playerState.Location.New);
                                }

                                events.Warped.Raise(new WarpedEventArgs(player, playerState.Location.Old, playerState.Location.New));
                            }

                            // raise player leveled up a skill
                            foreach (var pair in playerState.Skills)
                            {
                                if (!pair.Value.IsChanged)
                                    continue;

                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log($"Events: player skill '{pair.Key}' changed from {pair.Value.Old} to {pair.Value.New}.", LogLevel.Trace);

                                events.LevelChanged.Raise(new LevelChangedEventArgs(player, pair.Key, pair.Value.Old, pair.Value.New));
                            }

                            // raise player inventory changed
                            if (playerState.Inventory.IsChanged)
                            {
                                var inventory = playerState.Inventory;

                                if (this.Monitor.IsVerbose)
                                    this.Monitor.Log("Events: player inventory changed.", LogLevel.Trace);
                                events.InventoryChanged.Raise(new InventoryChangedEventArgs(player, added: inventory.Added, removed: inventory.Removed, quantityChanged: inventory.QuantityChanged));
                            }
                        }
                    }

                    /*********
                    ** Game update
                    *********/
                    // game launched
                    bool isFirstTick = SGame.TicksElapsed == 0;
                    if (isFirstTick)
                    {
                        Context.IsGameLaunched = true;
                        events.GameLaunched.Raise(new GameLaunchedEventArgs());
                    }

                    // preloaded
                    if (Context.IsSaveLoaded && Context.LoadStage != LoadStage.Loaded && Context.LoadStage != LoadStage.Ready && Game1.dayOfMonth != 0)
                        this.OnLoadStageChanged(LoadStage.Loaded);
                }

                /*********
                ** Game update tick
                *********/
                {
                    bool isOneSecond = SGame.TicksElapsed % 60 == 0;
                    events.UnvalidatedUpdateTicking.RaiseEmpty();
                    events.UpdateTicking.RaiseEmpty();
                    if (isOneSecond)
                        events.OneSecondUpdateTicking.RaiseEmpty();
                    try
                    {
                        this.Input.ApplyOverrides(); // if mods added any new overrides since the update, process them now
                        SGame.TicksElapsed++;
                        base.Update(gameTime);
                    }
                    catch (Exception ex)
                    {
                        this.MonitorForGame.Log($"An error occured in the base update loop: {ex.GetLogSummary()}", LogLevel.Error);
                    }

                    events.UnvalidatedUpdateTicked.RaiseEmpty();
                    events.UpdateTicked.RaiseEmpty();
                    if (isOneSecond)
                        events.OneSecondUpdateTicked.RaiseEmpty();
                }

                /*********
                ** Update events
                *********/
                this.UpdateCrashTimer.Reset();
            }
            catch (Exception ex)
            {
                // log error
                this.Monitor.Log($"An error occured in the overridden update loop: {ex.GetLogSummary()}", LogLevel.Error);

                // exit if irrecoverable
                if (!this.UpdateCrashTimer.Decrement())
                    this.ExitGameImmediately("The game crashed when updating, and SMAPI was unable to recover the game.");
            }
        }

        /// <summary>The method called to draw everything to the screen.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <param name="target_screen">The render target, if any.</param>
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "copied from game code as-is")]
        protected override void _draw(GameTime gameTime, RenderTarget2D target_screen, RenderTarget2D toBuffer = null)
        {
            Context.IsInDrawLoop = true;
            try
            {
                if (SGameConsole.Instance.isVisible)
                {
                    Game1.game1.GraphicsDevice.SetRenderTarget(Game1.game1.screen);
                    Game1.game1.GraphicsDevice.Clear(Color.Black);
                    Game1.spriteBatch.Begin(SpriteSortMode.Deferred,
                        BlendState.AlphaBlend,
                        SamplerState.PointClamp,
                        null,
                        null,
                        null,
                        null);
                    SGameConsole.Instance.draw(Game1.spriteBatch);
                    Game1.spriteBatch.End();
                    Game1.game1.GraphicsDevice.SetRenderTarget(null);
                    Game1.spriteBatch.Begin(SpriteSortMode.Deferred,
                        BlendState.AlphaBlend,
                        SamplerState.LinearClamp,
                        DepthStencilState.Default,
                        RasterizerState.CullNone,
                        null,
                        null);
                    Game1.spriteBatch.Draw(Game1.game1.screen,
                        Vector2.Zero,
                        Game1.game1.screen.Bounds,
                        Color.White,
                        0f,
                        Vector2.Zero,
                        Game1.options.zoomLevel,
                        SpriteEffects.None,
                        1f);
                    Game1.spriteBatch.End();
                    return;
                }

                this.DrawImpl(gameTime, target_screen, toBuffer);
                this.DrawCrashTimer.Reset();
            }
            catch (Exception ex)
            {
                // log error
                this.Monitor.Log($"An error occured in the overridden draw loop: {ex.GetLogSummary()}", LogLevel.Error);

                // exit if irrecoverable
                if (!this.DrawCrashTimer.Decrement())
                {
                    this.ExitGameImmediately("The game crashed when drawing, and SMAPI was unable to recover the game.");
                    return;
                }
            }
            finally
            {
                // recover sprite batch
                try
                {
                    if (Game1.spriteBatch.IsOpen(this.Reflection))
                    {
                        this.Monitor.Log("Recovering sprite batch from error...");
                        Game1.spriteBatch.End();
                    }
                }
                catch (Exception innerEx)
                {
                    this.Monitor.Log($"Could not recover sprite batch state: {innerEx.GetLogSummary()}", LogLevel.Error);
                }
            }

            Context.IsInDrawLoop = false;
        }

        /// <summary>Replicate the game's draw logic with some changes for SMAPI.</summary>
        /// <param name="gameTime">A snapshot of the game timing state.</param>
        /// <param name="target_screen">The render target, if any.</param>
        /// <remarks>This implementation is identical to <see cref="Game1.Draw"/>, except for try..catch around menu draw code, private field references replaced by wrappers, and added events.</remarks>
//        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "copied from game code as-is")]
//        [SuppressMessage("ReSharper", "PossibleLossOfFraction", Justification = "copied from game code as-is")]
        [SuppressMessage("SMAPI.CommonErrors", "AvoidNetField", Justification = "copied from game code as-is")]
        [SuppressMessage("SMAPI.CommonErrors", "AvoidImplicitNetFieldCast", Justification = "copied from game code as-is")]
        private void DrawImpl(GameTime gameTime, RenderTarget2D target_screen, RenderTarget2D toBuffer = null)
        {
            var events = this.Events;
            if (Game1.skipNextDrawCall)
            {
                Game1.skipNextDrawCall = false;
            }
            else
            {
                this.DrawHudField.SetValue(false);
                this.DrawActiveClickableMenuField.SetValue(false);
                Game1.showingHealthBar = false;
                if (Game1._newDayTask != null)
                {
                    if (!Game1.showInterDayScroll)
                        return;
                    this.DrawSavingDotDotDot();
                }
                else
                {
                    if (target_screen != null && toBuffer == null)
                    {
                        this.GraphicsDevice.SetRenderTarget(target_screen);
                    }

                    if (this.IsSaving)
                    {
                        this.GraphicsDevice.Clear(Game1.bgColor);
                        this.renderScreenBuffer(BlendState.Opaque, toBuffer);
                        if (Game1.activeClickableMenu != null)
                        {
                            if (Game1.IsActiveClickableMenuNativeScaled)
                            {
                                Game1.BackupViewportAndZoom(divideByZoom: true);
                                Game1.SetSpriteBatchBeginNextID("A1");
                                this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                events.Rendering.RaiseEmpty();
                                try
                                {
                                    events.RenderingActiveMenu.RaiseEmpty();
                                    Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                    events.RenderedActiveMenu.RaiseEmpty();
                                }
                                catch (Exception ex)
                                {
                                    this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during save. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                    Game1.activeClickableMenu.exitThisMenu();
                                }

                                this._spriteBatchEndMethod.Invoke();
                                Game1.RestoreViewportAndZoom();
                            }
                            else
                            {
                                Game1.BackupViewportAndZoom();
                                Game1.SetSpriteBatchBeginNextID("A2");
                                this.SpriteBatchBeginMethod.Invoke(1f);
                                events.Rendering.RaiseEmpty();
                                try
                                {
                                    events.RenderingActiveMenu.RaiseEmpty();
                                    Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                    events.RenderedActiveMenu.RaiseEmpty();
                                }
                                catch (Exception ex)
                                {
                                    this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during save. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                    Game1.activeClickableMenu.exitThisMenu();
                                }

                                events.Rendered.RaiseEmpty();
                                this._spriteBatchEndMethod.Invoke();
                                Game1.RestoreViewportAndZoom();
                            }
                        }

                        if (Game1.overlayMenu == null)
                            return;
                        Game1.BackupViewportAndZoom();
                        Game1.SetSpriteBatchBeginNextID("B");
                        this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, null);
                        Game1.overlayMenu.draw(Game1.spriteBatch);
                        this._spriteBatchEndMethod.Invoke();
                        Game1.RestoreViewportAndZoom();
                    }
                    else
                    {
                        this.GraphicsDevice.Clear(Game1.bgColor);
                        if (Game1.activeClickableMenu != null && Game1.options.showMenuBackground && Game1.activeClickableMenu.showWithoutTransparencyIfOptionIsSet() && !this.takingMapScreenshot)
                        {
                            Matrix scale = Matrix.CreateScale(1f);
                            Game1.SetSpriteBatchBeginNextID("C");
                            this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, scale);
                            events.Rendering.RaiseEmpty();
                            try
                            {
                                Game1.activeClickableMenu.drawBackground(Game1.spriteBatch);
                                events.RenderingActiveMenu.RaiseEmpty();
                                Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                events.RenderedActiveMenu.RaiseEmpty();
                            }
                            catch (Exception ex)
                            {
                                this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                Game1.activeClickableMenu.exitThisMenu();
                            }

                            events.Rendered.RaiseEmpty();
                            this._spriteBatchEndMethod.Invoke();
                            this.drawOverlays(Game1.spriteBatch);
                            this.renderScreenBufferTargetScreen(target_screen);
                            if (Game1.overlayMenu == null)
                                return;
                            Game1.SetSpriteBatchBeginNextID("D");
                            this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                            Game1.overlayMenu.draw(Game1.spriteBatch);
                            this._spriteBatchEndMethod.Invoke();
                        }
                        else
                        {
                            if (Game1.emergencyLoading)
                            {
                                if (!Game1.SeenConcernedApeLogo)
                                {
                                    Game1.SetSpriteBatchBeginNextID("E");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    if (Game1.logoFadeTimer < 5000)
                                    {
                                        Game1.spriteBatch.Draw(Game1.staminaRect, new Rectangle(0, 0, Game1.viewport.Width, Game1.viewport.Height), Color.White);
                                    }

                                    if (Game1.logoFadeTimer > 4500)
                                    {
                                        float scale = Math.Min(1f, (Game1.logoFadeTimer - 4500) / 500f);
                                        Game1.spriteBatch.Draw(Game1.staminaRect, new Rectangle(0, 0, Game1.viewport.Width, Game1.viewport.Height), Color.Black * scale);
                                    }

                                    Game1.spriteBatch.Draw(
                                        Game1.titleButtonsTexture,
                                        new Vector2(Game1.viewport.Width / 2, Game1.viewport.Height / 2 - 90),
                                        new Rectangle(171 + (Game1.logoFadeTimer / 100 % 2 == 0 ? 111 : 0), 311, 111, 60),
                                        Color.White * (Game1.logoFadeTimer < 500 ? Game1.logoFadeTimer / 500f : Game1.logoFadeTimer > 4500 ? 1f - (Game1.logoFadeTimer - 4500) / 500f : 1f),
                                        0f,
                                        Vector2.Zero,
                                        3f,
                                        SpriteEffects.None,
                                        0.2f);
                                    Game1.spriteBatch.Draw(
                                        Game1.titleButtonsTexture,
                                        new Vector2(Game1.viewport.Width / 2 - 261, Game1.viewport.Height / 2 - 102),
                                        new Rectangle(Game1.logoFadeTimer / 100 % 2 == 0 ? 85 : 0, 306, 85, 69),
                                        Color.White * (Game1.logoFadeTimer < 500 ? Game1.logoFadeTimer / 500f : Game1.logoFadeTimer > 4500 ? 1f - (Game1.logoFadeTimer - 4500) / 500f : 1f),
                                        0f,
                                        Vector2.Zero,
                                        3f,
                                        SpriteEffects.None,
                                        0.2f);
                                    this._spriteBatchEndMethod.Invoke();
                                }

                                Game1.logoFadeTimer -= gameTime.ElapsedGameTime.Milliseconds;
                            }

                            if (Game1.gameMode == Game1.errorLogMode)
                            {
                                Game1.SetSpriteBatchBeginNextID("F");
                                this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                events.Rendering.RaiseEmpty();
                                Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3685"), new Vector2(16f, 16f), Color.HotPink);
                                Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:Game1.cs.3686"), new Vector2(16f, 32f), new Color(0, 255, 0));
                                Game1.spriteBatch.DrawString(Game1.dialogueFont, Game1.parseText(Game1.errorMessage, Game1.dialogueFont, Game1.graphics.GraphicsDevice.Viewport.Width), new Vector2(16f, 48f), Color.White);
                                events.Rendered.RaiseEmpty();
                                this._spriteBatchEndMethod.Invoke();
                            }
                            else if (Game1.currentMinigame != null)
                            {
                                Game1.currentMinigame.draw(Game1.spriteBatch);
                                if (Game1.globalFade && !Game1.menuUp && (!Game1.nameSelectUp || Game1.messagePause))
                                {
                                    Game1.SetSpriteBatchBeginNextID("G");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds,
                                        Color.Black * (Game1.gameMode == Game1.titleScreenGameMode ? 1f - Game1.fadeToBlackAlpha : Game1.fadeToBlackAlpha));
                                    this._spriteBatchEndMethod.Invoke();
                                }

                                this.drawOverlays(Game1.spriteBatch);
                                this.renderScreenBufferTargetScreen(target_screen);
                                if (Game1.currentMinigame is FishingGame && Game1.activeClickableMenu != null)
                                {
                                    Game1.SetSpriteBatchBeginNextID("A-A");
                                    this.SpriteBatchBeginMethod.Invoke(1f);
                                    Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                    this._spriteBatchEndMethod.Invoke();
                                    this.drawOverlays(Game1.spriteBatch);
                                }
                                else if (Game1.currentMinigame is FantasyBoardGame && Game1.activeClickableMenu != null)
                                {
                                    if (Game1.IsActiveClickableMenuNativeScaled)
                                    {
                                        Game1.BackupViewportAndZoom(true);
                                        Game1.SetSpriteBatchBeginNextID("A1");
                                        this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                        Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                        this._spriteBatchEndMethod.Invoke();
                                        Game1.RestoreViewportAndZoom();
                                    }
                                    else
                                    {
                                        Game1.BackupViewportAndZoom();
                                        Game1.SetSpriteBatchBeginNextID("A2");
                                        this.SpriteBatchBeginMethod.Invoke(1f);
                                        Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                        this._spriteBatchEndMethod.Invoke();
                                        Game1.RestoreViewportAndZoom();
                                    }
                                }

                                this.DrawVirtualJoypadMethod.Invoke();
                            }
                            else if (Game1.showingEndOfNightStuff)
                            {
                                this.renderScreenBuffer(BlendState.Opaque);
                                Game1.BackupViewportAndZoom(divideByZoom: true);
                                Game1.SetSpriteBatchBeginNextID("A-B");
                                this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                events.Rendering.RaiseEmpty();
                                if (Game1.activeClickableMenu != null)
                                {
                                    try
                                    {
                                        events.RenderingActiveMenu.RaiseEmpty();
                                        Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                        events.RenderedActiveMenu.RaiseEmpty();
                                    }
                                    catch (Exception ex)
                                    {
                                        this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during end-of-night-stuff. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                        Game1.activeClickableMenu.exitThisMenu();
                                    }
                                }

                                events.Rendered.RaiseEmpty();
                                this._spriteBatchEndMethod.Invoke();
                                this.drawOverlays(Game1.spriteBatch);
                                Game1.RestoreViewportAndZoom();
                            }
                            else if (Game1.gameMode == Game1.loadingMode || Game1.gameMode == Game1.playingGameMode && Game1.currentLocation == null)
                            {
                                this.SpriteBatchBeginMethod.Invoke(1f);
                                events.Rendering.RaiseEmpty();
                                this._spriteBatchEndMethod.Invoke();
                                this.DrawLoadingDotDotDotMethod.Invoke(gameTime);
                                this.SpriteBatchBeginMethod.Invoke(1f);
                                events.Rendered.RaiseEmpty();
                                this._spriteBatchEndMethod.Invoke();
                                this.drawOverlays(Game1.spriteBatch);
                                this.renderScreenBufferTargetScreen(target_screen);
                                if (Game1.overlayMenu != null)
                                {
                                    Game1.SetSpriteBatchBeginNextID("H");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    Game1.overlayMenu.draw(Game1.spriteBatch);
                                    this._spriteBatchEndMethod.Invoke();
                                }

                                //base.Draw(gameTime);
                            }
                            else
                            {
                                Rectangle rectangle;
                                byte batchOpens = 0;
                                if (Game1.gameMode == Game1.titleScreenGameMode)
                                {
                                    Game1.SetSpriteBatchBeginNextID("I");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    if (++batchOpens == 1)
                                        events.Rendering.RaiseEmpty();
                                }
                                else if (!Game1.drawGame)
                                {
                                    Game1.SetSpriteBatchBeginNextID("J");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    if (++batchOpens == 1)
                                        events.Rendering.RaiseEmpty();
                                }
                                else if (Game1.drawGame)
                                {
                                    if (Game1.drawLighting && Game1.currentLocation != null)
                                    {
                                        this.GraphicsDevice.SetRenderTarget(Game1.lightmap);
                                        this.GraphicsDevice.Clear(Color.White * 0f);
                                        Game1.SetSpriteBatchBeginNextID("K");
                                        this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, null, null, null, new Matrix?());
                                        if (++batchOpens == 1)
                                            events.Rendering.RaiseEmpty();
                                        Color color1 = !Game1.currentLocation.Name.StartsWith("UndergroundMine") || !(Game1.currentLocation is MineShaft)
                                            ? Game1.ambientLight.Equals(Color.White) || RainManager.Instance.isRaining && (bool) Game1.currentLocation.isOutdoors ? Game1.outdoorLight :
                                            Game1.ambientLight
                                            : ((MineShaft) Game1.currentLocation).getLightingColor(gameTime);
                                        Game1.spriteBatch.Draw(Game1.staminaRect, Game1.lightmap.Bounds, color1);
                                        foreach (LightSource currentLightSource in Game1.currentLightSources)
                                        {
                                            if (!RainManager.Instance.isRaining && !Game1.isDarkOut() || currentLightSource.lightContext.Value != LightSource.LightContext.WindowLight)
                                            {
                                                if (currentLightSource.PlayerID != 0L && currentLightSource.PlayerID != Game1.player.UniqueMultiplayerID)
                                                {
                                                    Farmer farmerMaybeOffline = Game1.getFarmerMaybeOffline(currentLightSource.PlayerID);
                                                    if (farmerMaybeOffline == null || farmerMaybeOffline.currentLocation != null && farmerMaybeOffline.currentLocation.Name != Game1.currentLocation.Name || farmerMaybeOffline.hidden)
                                                        continue;
                                                }
                                            }

                                            if (Utility.isOnScreen(currentLightSource.position, (int) (currentLightSource.radius * 64.0 * 4.0)))
                                            {
                                                Texture2D lightTexture = currentLightSource.lightTexture;
                                                Vector2 position = Game1.GlobalToLocal(Game1.viewport, currentLightSource.position) / (Game1.options.lightingQuality / 2);
                                                Rectangle? sourceRectangle = currentLightSource.lightTexture.Bounds;
                                                Color color = currentLightSource.color;
                                                Rectangle bounds = currentLightSource.lightTexture.Bounds;
                                                double x = bounds.Center.X;
                                                bounds = currentLightSource.lightTexture.Bounds;
                                                double y = bounds.Center.Y;
                                                Vector2 origin = new Vector2((float) x, (float) y);
                                                double num = (double) currentLightSource.radius / (Game1.options.lightingQuality / 2);
                                                Game1.spriteBatch.Draw(lightTexture, position, sourceRectangle, color, 0.0f, origin, (float) num, SpriteEffects.None, 0.9f);
                                            }
                                        }

                                        this._spriteBatchEndMethod.Invoke();
                                        this.GraphicsDevice.SetRenderTarget(target_screen);
                                    }

                                    if (Game1.bloomDay && Game1.bloom != null)
                                    {
                                        Game1.bloom.BeginDraw();
                                    }

                                    this.GraphicsDevice.Clear(Game1.bgColor);
                                    Game1.SetSpriteBatchBeginNextID("L");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    if (++batchOpens == 1)
                                        events.Rendering.RaiseEmpty();
                                    events.RenderingWorld.RaiseEmpty();
                                    this.SpriteBatchBeginNextIDField.SetValue("L1");
                                    if (Game1.background != null)
                                    {
                                        Game1.background.draw(Game1.spriteBatch);
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L2");
                                    Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                    this.SpriteBatchBeginNextIDField.SetValue("L3");
                                    try
                                    {
                                        if (Game1.currentLocation != null)
                                        {
                                            Game1.currentLocation.Map.GetLayer("Back").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, wrapAround: false, 4);
                                            this.SpriteBatchBeginNextIDField.SetValue("L4");
                                        }
                                    }
                                    catch (KeyNotFoundException exception)
                                    {
                                        this.CheckToReloadGameLocationAfterDrawFailMethod.Invoke("Back", exception);
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L5");
                                    if (Game1.currentLocation != null)
                                    {
                                        Game1.currentLocation.drawWater(Game1.spriteBatch);
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L6");
                                    this.FarmerShadowsField.GetValue().Clear();
                                    this.SpriteBatchBeginNextIDField.SetValue("L7");
                                    if (Game1.currentLocation != null && Game1.currentLocation.currentEvent != null && !Game1.currentLocation.currentEvent.isFestival && Game1.currentLocation.currentEvent.farmerActors.Count > 0)
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("L8");
                                        foreach (Farmer farmerActor in Game1.currentLocation.currentEvent.farmerActors)
                                        {
                                            if (farmerActor.IsLocalPlayer && Game1.displayFarmer || !farmerActor.hidden)
                                            {
                                                this.FarmerShadowsField.GetValue().Add(farmerActor);
                                            }
                                        }

                                        this.SpriteBatchBeginNextIDField.SetValue("L9");
                                    }
                                    else
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("L10");
                                        if (Game1.currentLocation != null)
                                        {
                                            this.SpriteBatchBeginNextIDField.SetValue("L11");
                                            foreach (Farmer farmer in Game1.currentLocation.farmers)
                                            {
                                                if (farmer.IsLocalPlayer && Game1.displayFarmer || !farmer.hidden)
                                                {
                                                    this.FarmerShadowsField.GetValue().Add(farmer);
                                                }
                                            }

                                            this.SpriteBatchBeginNextIDField.SetValue("L12");
                                        }
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L13");
                                    if (Game1.currentLocation != null && !Game1.currentLocation.shouldHideCharacters())
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("L14");
                                        if (Game1.CurrentEvent == null)
                                        {
                                            this.SpriteBatchBeginNextIDField.SetValue("L15");
                                            foreach (NPC character in Game1.currentLocation.characters)
                                            {
                                                try
                                                {
                                                    if (!character.swimming)
                                                    {
                                                        if (!character.HideShadow)
                                                        {
                                                            if (!character.IsInvisible)
                                                            {
                                                                if (!Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(character.getTileLocation()))
                                                                    Game1.spriteBatch.Draw(
                                                                        Game1.shadowTexture,
                                                                        Game1.GlobalToLocal(Game1.viewport, character.Position + new Vector2(character.Sprite.SpriteWidth * 4 / 2f, character.GetBoundingBox().Height + (character.IsMonster ? 0 : 12))),
                                                                        Game1.shadowTexture.Bounds,
                                                                        Color.White,
                                                                        0.0f,
                                                                        new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y),
                                                                        (float) (4.0 + character.yJumpOffset / 40.0) * (float) character.scale,
                                                                        SpriteEffects.None,
                                                                        Math.Max(0.0f, character.getStandingY() / 10000f) - 1E-06f);
                                                            }
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Dictionary<string, string> dictionary1 = new Dictionary<string, string>();
                                                    if (character != null)
                                                    {
                                                        dictionary1["name"] = character.name;
                                                        dictionary1["Sprite"] = (character.Sprite != null).ToString();
                                                        Dictionary<string, string> dictionary2 = dictionary1;
                                                        character.GetBoundingBox();
                                                        bool flag = true;
                                                        string str1 = flag.ToString();
                                                        dictionary2["BoundingBox"] = str1;
                                                        Dictionary<string, string> dictionary3 = dictionary1;
                                                        flag = true;
                                                        string str2 = flag.ToString();
                                                        dictionary3["shadowTexture.Bounds"] = str2;
                                                        Dictionary<string, string> dictionary4 = dictionary1;
                                                        flag = Game1.currentLocation != null;
                                                        string str3 = flag.ToString();
                                                        dictionary4["currentLocation"] = str3;
                                                    }

                                                    Dictionary<string, string> dictionary5 = dictionary1;
                                                    ErrorAttachmentLog[] errorAttachmentLogArray = Array.Empty<ErrorAttachmentLog>();
                                                    Crashes.TrackError(ex, dictionary5, errorAttachmentLogArray);
                                                }
                                            }

                                            this.SpriteBatchBeginNextIDField.SetValue("L16");
                                        }
                                        else
                                        {
                                            this.SpriteBatchBeginNextIDField.SetValue("L17");
                                            foreach (NPC actor in Game1.CurrentEvent.actors)
                                            {
                                                if (!actor.swimming && !actor.HideShadow && !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(actor.getTileLocation()))
                                                {
                                                    Game1.spriteBatch.Draw(Game1.shadowTexture, Game1.GlobalToLocal(Game1.viewport, actor.Position + new Vector2(actor.Sprite.SpriteWidth * 4 / 2f, actor.GetBoundingBox().Height + (!actor.IsMonster ? actor.Sprite.SpriteHeight <= 16 ? -4 : 12 : 0))), Game1.shadowTexture.Bounds, Color.White, 0f, new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y), (4f + actor.yJumpOffset / 40f) * (float) actor.scale, SpriteEffects.None, Math.Max(0f, actor.getStandingY() / 10000f) - 1E-06f);
                                                }
                                            }

                                            this.SpriteBatchBeginNextIDField.SetValue("L18");
                                        }

                                        this.SpriteBatchBeginNextIDField.SetValue("L19");
                                        foreach (Farmer farmerShadow in this.FarmerShadowsField.GetValue())
                                        {
                                            if (!Game1.multiplayer.isDisconnecting(farmerShadow.UniqueMultiplayerID) &&
                                                !farmerShadow.swimming &&
                                                !farmerShadow.isRidingHorse() &&
                                                (Game1.currentLocation == null || !Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(farmerShadow.getTileLocation())))
                                            {
                                                Game1.spriteBatch.Draw(
                                                    Game1.shadowTexture,
                                                    Game1.GlobalToLocal(farmerShadow.Position + new Vector2(32f, 24f)),
                                                    Game1.shadowTexture.Bounds,
                                                    Color.White,
                                                    0.0f,
                                                    new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y), (float) (4.0 - (!farmerShadow.running && !farmerShadow.UsingTool || farmerShadow.FarmerSprite.currentAnimationIndex <= 1 ? 0.0 : Math.Abs(FarmerRenderer.featureYOffsetPerFrame[farmerShadow.FarmerSprite.CurrentFrame]) * 0.5)),
                                                    SpriteEffects.None,
                                                    0.0f);
                                            }
                                        }

                                        this.SpriteBatchBeginNextIDField.SetValue("L20");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L21");
                                    try
                                    {
                                        if (Game1.currentLocation != null)
                                        {
                                            Game1.currentLocation.Map.GetLayer("Buildings").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, wrapAround: false, 4);
                                        }
                                    }
                                    catch (KeyNotFoundException exception2)
                                    {
                                        this.CheckToReloadGameLocationAfterDrawFailMethod.Invoke("Buildings", exception2);
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L22");
                                    Game1.mapDisplayDevice.EndScene();
                                    this.SpriteBatchBeginNextIDField.SetValue("L23");
                                    if (Game1.currentLocation != null && Game1.currentLocation.tapToMove.targetNPC != null)
                                    {
                                        Game1.spriteBatch.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, Game1.currentLocation.tapToMove.targetNPC.Position + new Vector2(Game1.currentLocation.tapToMove.targetNPC.Sprite.SpriteWidth * 4 / 2f - 32f, Game1.currentLocation.tapToMove.targetNPC.GetBoundingBox().Height + (!Game1.currentLocation.tapToMove.targetNPC.IsMonster ? 12 : 0) - 32)), new Rectangle(194, 388, 16, 16), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.58f);
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("L24");
                                    this._spriteBatchEndMethod.Invoke();
                                    this.SpriteBatchBeginNextIDField.SetValue("L25");
                                    Game1.SetSpriteBatchBeginNextID("M");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    this.SpriteBatchBeginNextIDField.SetValue("M1");
                                    if (Game1.currentLocation != null && !Game1.currentLocation.shouldHideCharacters())
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M2");
                                        if (Game1.CurrentEvent == null)
                                        {
                                            this.SpriteBatchBeginNextIDField.SetValue("M3");
                                            foreach (NPC character2 in Game1.currentLocation.characters)
                                            {
                                                if (!character2.swimming && !character2.HideShadow && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(character2.getTileLocation()))
                                                {
                                                    Game1.spriteBatch.Draw(
                                                        Game1.shadowTexture,
                                                        Game1.GlobalToLocal(Game1.viewport, character2.Position + new Vector2(character2.Sprite.SpriteWidth * 4 / 2f, character2.GetBoundingBox().Height + (!character2.IsMonster ? 12 : 0))),
                                                        Game1.shadowTexture.Bounds,
                                                        Color.White,
                                                        0f,
                                                        new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y),
                                                        (4f + character2.yJumpOffset / 40f) * (float) character2.scale, SpriteEffects.None,
                                                        Math.Max(0f, character2.getStandingY() / 10000f) - 1E-06f);
                                                }
                                            }

                                            this.SpriteBatchBeginNextIDField.SetValue("M4");
                                        }
                                        else
                                        {
                                            this.SpriteBatchBeginNextIDField.SetValue("M5");
                                            foreach (NPC actor2 in Game1.CurrentEvent.actors)
                                            {
                                                if (!actor2.swimming && !actor2.HideShadow && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(actor2.getTileLocation()))
                                                {
                                                    Game1.spriteBatch.Draw(
                                                        Game1.shadowTexture,
                                                        Game1.GlobalToLocal(Game1.viewport, actor2.Position + new Vector2(actor2.Sprite.SpriteWidth * 4 / 2f, actor2.GetBoundingBox().Height + (!actor2.IsMonster ? 12 : 0))),
                                                        Game1.shadowTexture.Bounds,
                                                        Color.White,
                                                        0f,
                                                        new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y),
                                                        (4f + actor2.yJumpOffset / 40f) * (float) actor2.scale,
                                                        SpriteEffects.None,
                                                        Math.Max(0f, actor2.getStandingY() / 10000f) - 1E-06f);
                                                }
                                            }

                                            this.SpriteBatchBeginNextIDField.SetValue("M6");
                                        }

                                        foreach (Farmer farmerShadow in this.FarmerShadowsField.GetValue())
                                        {
                                            this.SpriteBatchBeginNextIDField.SetValue("M7");
                                            float layerDepth = System.Math.Max(0.0001f, farmerShadow.getDrawLayer() + 0.00011f) - 0.0001f;
                                            if (!farmerShadow.swimming && !farmerShadow.isRidingHorse() && Game1.currentLocation != null && Game1.currentLocation.shouldShadowBeDrawnAboveBuildingsLayer(farmerShadow.getTileLocation()))
                                            {
                                                Game1.spriteBatch.Draw(
                                                    Game1.shadowTexture,
                                                    Game1.GlobalToLocal(farmerShadow.Position + new Vector2(32f, 24f)),
                                                    Game1.shadowTexture.Bounds,
                                                    Color.White,
                                                    0.0f,
                                                    new Vector2(Game1.shadowTexture.Bounds.Center.X, Game1.shadowTexture.Bounds.Center.Y),
                                                    (float) (4.0 - (!farmerShadow.running && !farmerShadow.UsingTool || farmerShadow.FarmerSprite.currentAnimationIndex <= 1 ? 0.0 : Math.Abs(FarmerRenderer.featureYOffsetPerFrame[farmerShadow.FarmerSprite.CurrentFrame]) * 0.5)),
                                                    SpriteEffects.None,
                                                    layerDepth);
                                            }

                                            this.SpriteBatchBeginNextIDField.SetValue("M8");
                                        }
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M9");
                                    if ((Game1.eventUp || Game1.killScreen) && !Game1.killScreen && Game1.currentLocation?.currentEvent != null)
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M10");
                                        Game1.currentLocation.currentEvent.draw(Game1.spriteBatch);
                                        this.SpriteBatchBeginNextIDField.SetValue("M11");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M12");
                                    if (Game1.currentLocation != null && Game1.player.currentUpgrade != null && Game1.player.currentUpgrade.daysLeftTillUpgradeDone <= 3 && Game1.currentLocation.Name.Equals("Farm"))
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M13");
                                        Game1.spriteBatch.Draw(
                                            Game1.player.currentUpgrade.workerTexture,
                                            Game1.GlobalToLocal(Game1.viewport, Game1.player.currentUpgrade.positionOfCarpenter),
                                            Game1.player.currentUpgrade.getSourceRectangle(),
                                            Color.White,
                                            0f,
                                            Vector2.Zero,
                                            1f,
                                            SpriteEffects.None,
                                            (Game1.player.currentUpgrade.positionOfCarpenter.Y + 48f) / 10000f);
                                        this.SpriteBatchBeginNextIDField.SetValue("M14");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M15");
                                    Game1.currentLocation?.draw(Game1.spriteBatch);
                                    foreach (Vector2 key in Game1.crabPotOverlayTiles.Keys)
                                    {
                                        Tile tile = Game1.currentLocation.Map.GetLayer("Buildings").Tiles[(int) key.X, (int) key.Y];
                                        if (tile != null)
                                        {
                                            Vector2 local = Game1.GlobalToLocal(Game1.viewport, key * 64f);
                                            Location location = new Location((int) local.X, (int) local.Y);
                                            Game1.mapDisplayDevice.DrawTile(tile, location, (float) (((double) key.Y * 64.0 - 1.0) / 10000.0));
                                        }
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M16");
                                    if (Game1.player.ActiveObject == null && (Game1.player.UsingTool || Game1.pickingTool) && Game1.player.CurrentTool != null && (!Game1.player.CurrentTool.Name.Equals("Seeds") || Game1.pickingTool))
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M17");
                                        Game1.drawTool(Game1.player);
                                        this.SpriteBatchBeginNextIDField.SetValue("M18");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M19");
                                    if (Game1.currentLocation != null && Game1.currentLocation.Name.Equals("Farm"))
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M20");
                                        this.drawFarmBuildings();
                                        this.SpriteBatchBeginNextIDField.SetValue("M21");
                                    }

                                    if (Game1.tvStation >= 0)
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M22");
                                        Game1.spriteBatch.Draw(
                                            Game1.tvStationTexture,
                                            Game1.GlobalToLocal(Game1.viewport, new Vector2(400f, 160f)),
                                            new Rectangle(Game1.tvStation * 24, 0, 24, 15),
                                            Color.White,
                                            0f,
                                            Vector2.Zero,
                                            4f,
                                            SpriteEffects.None,
                                            1E-08f);
                                        this.SpriteBatchBeginNextIDField.SetValue("M23");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M24");
                                    if (Game1.panMode)
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M25");
                                        Game1.spriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle((int) Math.Floor((Game1.getOldMouseX() + Game1.viewport.X) / 64.0) * 64 - Game1.viewport.X, (int) Math.Floor((Game1.getOldMouseY() + Game1.viewport.Y) / 64.0) * 64 - Game1.viewport.Y, 64, 64), Color.Lime * 0.75f);
                                        this.SpriteBatchBeginNextIDField.SetValue("M26");
                                        foreach (Warp warp in Game1.currentLocation?.warps)
                                        {
                                            Game1.spriteBatch.Draw(Game1.fadeToBlackRect, new Rectangle(warp.X * 64 - Game1.viewport.X, warp.Y * 64 - Game1.viewport.Y, 64, 64), Color.Red * 0.75f);
                                        }

                                        this.SpriteBatchBeginNextIDField.SetValue("M27");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M28");
                                    Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                    this.SpriteBatchBeginNextIDField.SetValue("M29");
                                    try
                                    {
                                        Game1.currentLocation?.Map.GetLayer("Front").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, wrapAround: false, 4);
                                        this.SpriteBatchBeginNextIDField.SetValue("M30");
                                    }
                                    catch (KeyNotFoundException exception3)
                                    {
                                        this.CheckToReloadGameLocationAfterDrawFailMethod.Invoke("Front", exception3);
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M31");
                                    Game1.mapDisplayDevice.EndScene();
                                    this.SpriteBatchBeginNextIDField.SetValue("M32");
                                    Game1.currentLocation?.drawAboveFrontLayer(Game1.spriteBatch);
                                    this.SpriteBatchBeginNextIDField.SetValue("M33");
                                    if (Game1.currentLocation != null &&
                                        Game1.currentLocation.tapToMove.targetNPC == null &&
                                        (Game1.displayHUD || Game1.eventUp) &&
                                        Game1.currentBillboard == 0 &&
                                        Game1.gameMode == Game1.playingGameMode &&
                                        !Game1.freezeControls &&
                                        !Game1.panMode &&
                                        !Game1.HostPaused)
                                    {
                                        this.SpriteBatchBeginNextIDField.SetValue("M34");
                                        this.DrawTapToMoveTargetMethod.Invoke();
                                        this.SpriteBatchBeginNextIDField.SetValue("M35");
                                    }

                                    this.SpriteBatchBeginNextIDField.SetValue("M36");
                                    this._spriteBatchEndMethod.Invoke();
                                    Game1.SetSpriteBatchBeginNextID("N");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    if (Game1.currentLocation != null &&
                                        Game1.displayFarmer &&
                                        Game1.player.ActiveObject != null &&
                                        (bool) Game1.player.ActiveObject.bigCraftable &&
                                        this.checkBigCraftableBoundariesForFrontLayer() &&
                                        Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), Game1.player.getStandingY()), Game1.viewport.Size) == null)
                                    {
                                        Game1.drawPlayerHeldObject(Game1.player);
                                    }
                                    else if (Game1.displayFarmer && Game1.player.ActiveObject != null)
                                    {
                                        if (Game1.currentLocation != null && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location((int) Game1.player.Position.X, (int) Game1.player.Position.Y - 38), Game1.viewport.Size) == null || Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location((int) Game1.player.Position.X, (int) Game1.player.Position.Y - 38), Game1.viewport.Size).TileIndexProperties.ContainsKey("FrontAlways"))
                                        {
                                            Layer layer1 = Game1.currentLocation.Map.GetLayer("Front");
                                            rectangle = Game1.player.GetBoundingBox();
                                            Location mapDisplayLocation1 = new Location(rectangle.Right, (int) Game1.player.Position.Y - 38);
                                            Size size1 = Game1.viewport.Size;
                                            if (layer1.PickTile(mapDisplayLocation1, size1) != null)
                                            {
                                                Layer layer2 = Game1.currentLocation.Map.GetLayer("Front");
                                                rectangle = Game1.player.GetBoundingBox();
                                                Location mapDisplayLocation2 = new Location(rectangle.Right, (int) Game1.player.Position.Y - 38);
                                                Size size2 = Game1.viewport.Size;
                                                if (layer2.PickTile(mapDisplayLocation2, size2).TileIndexProperties.ContainsKey("FrontAlways"))
                                                    goto label_183;
                                            }
                                            else
                                                goto label_183;
                                        }

                                        Game1.drawPlayerHeldObject(Game1.player);
                                    }

                                    label_183:
                                    if (Game1.currentLocation != null
                                        && (Game1.player.UsingTool || Game1.pickingTool)
                                        && Game1.player.CurrentTool != null
                                        && (!Game1.player.CurrentTool.Name.Equals("Seeds") || Game1.pickingTool)
                                        && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), (int) Game1.player.Position.Y - 38), Game1.viewport.Size) != null && Game1.currentLocation.Map.GetLayer("Front").PickTile(new Location(Game1.player.getStandingX(), Game1.player.getStandingY()), Game1.viewport.Size) == null)
                                        Game1.drawTool(Game1.player);
                                    if (Game1.currentLocation != null && Game1.currentLocation.Map.GetLayer("AlwaysFront") != null)
                                    {
                                        Game1.mapDisplayDevice.BeginScene(Game1.spriteBatch);
                                        try
                                        {
                                            Game1.currentLocation.Map.GetLayer("AlwaysFront").Draw(Game1.mapDisplayDevice, Game1.viewport, Location.Origin, wrapAround: false, 4);
                                        }
                                        catch (KeyNotFoundException exception4)
                                        {
                                            this.CheckToReloadGameLocationAfterDrawFailMethod.Invoke("AlwaysFront", exception4);
                                        }

                                        Game1.mapDisplayDevice.EndScene();
                                    }

                                    if (Game1.toolHold > 400f && Game1.player.CurrentTool.UpgradeLevel >= 1 && Game1.player.canReleaseTool)
                                    {
                                        Color color = Color.White;
                                        switch ((int) ((double) Game1.toolHold / 600.0) + 2)
                                        {
                                            case 1:
                                                color = Tool.copperColor;
                                                break;
                                            case 2:
                                                color = Tool.steelColor;
                                                break;
                                            case 3:
                                                color = Tool.goldColor;
                                                break;
                                            case 4:
                                                color = Tool.iridiumColor;
                                                break;
                                        }

                                        Game1.spriteBatch.Draw(Game1.littleEffect, new Rectangle((int) Game1.player.getLocalPosition(Game1.viewport).X - 2, (int) Game1.player.getLocalPosition(Game1.viewport).Y - (!Game1.player.CurrentTool.Name.Equals("Watering Can") ? 64 : 0) - 2, (int) (Game1.toolHold % 600f * 0.08f) + 4, 12), Color.Black);
                                        Game1.spriteBatch.Draw(Game1.littleEffect, new Rectangle((int) Game1.player.getLocalPosition(Game1.viewport).X, (int) Game1.player.getLocalPosition(Game1.viewport).Y - (!Game1.player.CurrentTool.Name.Equals("Watering Can") ? 64 : 0), (int) (Game1.toolHold % 600f * 0.08f), 8), color);
                                    }

                                    this.drawWeather(gameTime, target_screen);
                                    if (Game1.farmEvent != null)
                                    {
                                        Game1.farmEvent.draw(Game1.spriteBatch);
                                    }

                                    if (Game1.currentLocation != null && Game1.currentLocation.LightLevel > 0f && Game1.timeOfDay < 2000)
                                    {
                                        Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * Game1.currentLocation.LightLevel);
                                    }

                                    if (Game1.screenGlow)
                                    {
                                        Game1.spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Game1.screenGlowColor * Game1.screenGlowAlpha);
                                    }

                                    Game1.currentLocation?.drawAboveAlwaysFrontLayer(Game1.spriteBatch);
                                    if (Game1.player.CurrentTool != null && Game1.player.CurrentTool is FishingRod && ((Game1.player.CurrentTool as FishingRod).isTimingCast || (Game1.player.CurrentTool as FishingRod).castingChosenCountdown > 0f || (Game1.player.CurrentTool as FishingRod).fishCaught || (Game1.player.CurrentTool as FishingRod).showingTreasure))
                                    {
                                        Game1.player.CurrentTool.draw(Game1.spriteBatch);
                                    }

                                    this._spriteBatchEndMethod.Invoke();
                                    Game1.SetSpriteBatchBeginNextID("O");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.FrontToBack, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    if (Game1.eventUp && Game1.currentLocation != null && Game1.currentLocation.currentEvent != null)
                                    {
                                        Game1.currentLocation.currentEvent.drawAboveAlwaysFrontLayer(Game1.spriteBatch);
                                        foreach (NPC actor in Game1.currentLocation.currentEvent.actors)
                                        {
                                            if (actor.isEmoting)
                                            {
                                                Vector2 localPosition = actor.getLocalPosition(Game1.viewport);
                                                localPosition.Y -= 140f;
                                                if (actor.Age == 2)
                                                {
                                                    localPosition.Y += 32f;
                                                }
                                                else if (actor.Gender == 1)
                                                {
                                                    localPosition.Y += 10f;
                                                }

                                                Game1.spriteBatch.Draw(Game1.emoteSpriteSheet, localPosition, new Rectangle(actor.CurrentEmoteIndex * 16 % Game1.emoteSpriteSheet.Width, actor.CurrentEmoteIndex * 16 / Game1.emoteSpriteSheet.Width * 16, 16, 16), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, actor.getStandingY() / 10000f);
                                            }
                                        }
                                    }

                                    this._spriteBatchEndMethod.Invoke();
                                    if (Game1.drawLighting)
                                    {
                                        Game1.SetSpriteBatchBeginNextID("P");
                                        this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, this.LightingBlendField.GetValue(), SamplerState.LinearClamp, null, null, null, new Matrix?());
                                        Game1.spriteBatch.Draw(Game1.lightmap, Vector2.Zero, Game1.lightmap.Bounds, Color.White, 0f, Vector2.Zero, Game1.options.lightingQuality / 2, SpriteEffects.None, 1f);
                                        if (RainManager.Instance.isRaining && Game1.currentLocation != null && (bool) Game1.currentLocation.isOutdoors && !(Game1.currentLocation is Desert))
                                        {
                                            Game1.spriteBatch.Draw(Game1.staminaRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.OrangeRed * 0.45f);
                                        }

                                        this._spriteBatchEndMethod.Invoke();
                                    }

                                    Game1.SetSpriteBatchBeginNextID("Q");
                                    this._spriteBatchBeginMethod.Invoke(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, new Matrix?());
                                    events.RenderedWorld.RaiseEmpty();
                                    if (Game1.drawGrid)
                                    {
                                        int num = -Game1.viewport.X % 64;
                                        float num2 = -Game1.viewport.Y % 64;
                                        int num3 = num;
                                        while (true)
                                        {
                                            int num4 = num3;
                                            int width = Game1.graphics.GraphicsDevice.Viewport.Width;
                                            if (num4 < width)
                                            {
                                                int x = num3;
                                                int y = (int) num2;
                                                int height = Game1.graphics.GraphicsDevice.Viewport.Height;
                                                Rectangle destinationRectangle = new Rectangle(x, y, 1, height);
                                                Color color = Color.Red * 0.5f;
                                                Game1.spriteBatch.Draw(Game1.staminaRect, destinationRectangle, color);
                                                num3 += 64;
                                            }
                                            else
                                                break;
                                        }

                                        float num5 = num2;
                                        while (true)
                                        {
                                            double num4 = num5;
                                            double height = Game1.graphics.GraphicsDevice.Viewport.Height;
                                            if (num4 < height)
                                            {
                                                int x = num;
                                                int y = (int) num5;
                                                int width = Game1.graphics.GraphicsDevice.Viewport.Width;
                                                Rectangle destinationRectangle = new Rectangle(x, y, width, 1);
                                                Color color = Color.Red * 0.5f;
                                                Game1.spriteBatch.Draw(Game1.staminaRect, destinationRectangle, color);
                                                num5 += 64f;
                                            }
                                            else
                                                break;
                                        }
                                    }

                                    if (Game1.currentBillboard != 0 && !this.takingMapScreenshot)
                                        this.drawBillboard();
                                    if ((Game1.displayHUD || Game1.eventUp)
                                        && Game1.currentBillboard == 0
                                        && Game1.gameMode == Game1.playingGameMode
                                        && !Game1.freezeControls
                                        && !Game1.panMode
                                        && !Game1.HostPaused)
                                    {
                                        if (Game1.currentLocation != null
                                            && !Game1.eventUp
                                            && Game1.farmEvent == null
//                                            && Game1.currentBillboard == 0
//                                            && Game1.gameMode == Game1.playingGameMode
                                            && !this.takingMapScreenshot
                                            && Game1.isOutdoorMapSmallerThanViewport())
                                        {
                                            int width1 = -Math.Min(Game1.viewport.X, 4096);
                                            int height1 = Game1.graphics.GraphicsDevice.Viewport.Height;
                                            Rectangle destinationRectangle1 = new Rectangle(0, 0, width1, height1);
                                            Color black1 = Color.Black;
                                            Game1.spriteBatch.Draw(Game1.fadeToBlackRect, destinationRectangle1, black1);
                                            int x = -Game1.viewport.X + Game1.currentLocation.map.Layers[0].LayerWidth * 64;
                                            int width2 = Math.Min(4096, Game1.graphics.GraphicsDevice.Viewport.Width - (-Game1.viewport.X + Game1.currentLocation.map.Layers[0].LayerWidth * 64));
                                            int height2 = Game1.graphics.GraphicsDevice.Viewport.Height;
                                            Rectangle destinationRectangle2 = new Rectangle(x, 0, width2, height2);
                                            Color black2 = Color.Black;
                                            Game1.spriteBatch.Draw(Game1.fadeToBlackRect, destinationRectangle2, black2);
                                        }

                                        this.DrawHudField.SetValue(false);
                                        if ((Game1.displayHUD || Game1.eventUp) && Game1.currentBillboard == 0 && Game1.gameMode == 3 && !Game1.freezeControls && !Game1.panMode && !Game1.HostPaused && !this.takingMapScreenshot) this.DrawHudField.SetValue(true);
                                        this.DrawGreenPlacementBoundsMethod.Invoke();
                                    }
                                }

                                if (Game1.farmEvent != null)
                                {
                                    Game1.farmEvent.draw(Game1.spriteBatch);
                                }

                                if (Game1.dialogueUp && !Game1.nameSelectUp && !Game1.messagePause && (Game1.activeClickableMenu == null || !(Game1.activeClickableMenu is DialogueBox)))
                                {
                                    this.drawDialogueBox();
                                }

                                if (Game1.progressBar && !this.takingMapScreenshot)
                                {
                                    int x1 = (Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea().Width - Game1.dialogueWidth) / 2;
                                    rectangle = Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea();
                                    int y1 = rectangle.Bottom - 128;
                                    Rectangle destinationRectangle1 = new Rectangle(x1, y1, Game1.dialogueWidth, 32);
                                    Game1.spriteBatch.Draw(Game1.fadeToBlackRect, destinationRectangle1, Color.LightGray);
                                    int x2 = (Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea().Width - Game1.dialogueWidth) / 2;
                                    rectangle = Game1.graphics.GraphicsDevice.Viewport.GetTitleSafeArea();
                                    int y2 = rectangle.Bottom - 128;
                                    int width = (int) (Game1.pauseAccumulator / (double) Game1.pauseTime * Game1.dialogueWidth);
                                    Rectangle destinationRectangle2 = new Rectangle(x2, y2, width, 32);
                                    Game1.spriteBatch.Draw(Game1.staminaRect, destinationRectangle2, Color.DimGray);
                                }

                                if (RainManager.Instance.isRaining && Game1.currentLocation != null && (bool) Game1.currentLocation.isOutdoors && !(Game1.currentLocation is Desert))
                                {
                                    Rectangle bounds = Game1.graphics.GraphicsDevice.Viewport.Bounds;
                                    Color color = Color.Blue * 0.2f;
                                    Game1.spriteBatch.Draw(Game1.staminaRect, bounds, color);
                                }

                                if ((Game1.messagePause || Game1.globalFade) && Game1.dialogueUp && !this.takingMapScreenshot)
                                {
                                    this.drawDialogueBox();
                                }

                                if (!this.takingMapScreenshot)
                                {
                                    foreach (TemporaryAnimatedSprite overlayTempSprite in Game1.screenOverlayTempSprites)
                                    {
                                        overlayTempSprite.draw(Game1.spriteBatch, localPosition: true);
                                    }
                                }

                                if (Game1.debugMode)
                                {
                                    StringBuilder debugStringBuilder = this.DebugStringBuilderField.GetValue();
                                    debugStringBuilder.Clear();
                                    if (Game1.panMode)
                                    {
                                        debugStringBuilder.Append((Game1.getOldMouseX() + Game1.viewport.X) / 64);
                                        debugStringBuilder.Append(",");
                                        debugStringBuilder.Append((Game1.getOldMouseY() + Game1.viewport.Y) / 64);
                                    }
                                    else
                                    {
                                        debugStringBuilder.Append("player: ");
                                        debugStringBuilder.Append(Game1.player.getStandingX() / 64);
                                        debugStringBuilder.Append(", ");
                                        debugStringBuilder.Append(Game1.player.getStandingY() / 64);
                                    }

                                    debugStringBuilder.Append(" mouseTransparency: ");
                                    debugStringBuilder.Append(Game1.mouseCursorTransparency);
                                    debugStringBuilder.Append(" mousePosition: ");
                                    debugStringBuilder.Append(Game1.getMouseX());
                                    debugStringBuilder.Append(",");
                                    debugStringBuilder.Append(Game1.getMouseY());
                                    debugStringBuilder.Append(Environment.NewLine);
                                    debugStringBuilder.Append(" mouseWorldPosition: ");
                                    debugStringBuilder.Append(Game1.getMouseX() + Game1.viewport.X);
                                    debugStringBuilder.Append(",");
                                    debugStringBuilder.Append(Game1.getMouseY() + Game1.viewport.Y);
                                    debugStringBuilder.Append("debugOutput: ");
                                    debugStringBuilder.Append(Game1.debugOutput);
                                    Game1.spriteBatch.DrawString(Game1.smallFont, debugStringBuilder, new Vector2(this.GraphicsDevice.Viewport.GetTitleSafeArea().X, this.GraphicsDevice.Viewport.GetTitleSafeArea().Y + Game1.smallFont.LineSpacing * 8), Color.Red, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.09999999f);
                                }

                                if (Game1.showKeyHelp && !this.takingMapScreenshot)
                                {
                                    Game1.spriteBatch.DrawString(Game1.smallFont, Game1.keyHelpString, new Vector2(64f, Game1.viewport.Height - 64 - (Game1.dialogueUp ? 192 + (Game1.isQuestion ? Game1.questionChoices.Count * 64 : 0) : 0) - Game1.smallFont.MeasureString(Game1.keyHelpString).Y), Color.LightGray, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.9999999f);
                                }

                                if (Game1.activeClickableMenu != null)
                                {
                                    this.DrawActiveClickableMenuField.SetValue(true);
                                    if (Game1.activeClickableMenu is CarpenterMenu)
                                    {
                                        ((CarpenterMenu) Game1.activeClickableMenu).DrawPlacementSquares(Game1.spriteBatch);
                                    }
                                    else if (Game1.activeClickableMenu is MuseumMenu)
                                    {
                                        ((MuseumMenu) Game1.activeClickableMenu).DrawPlacementGrid(Game1.spriteBatch);
                                    }

                                    if (!Game1.IsActiveClickableMenuUnscaled && !Game1.IsActiveClickableMenuNativeScaled)
                                    {
                                        try
                                        {

                                            events.RenderingActiveMenu.RaiseEmpty();
                                            Game1.activeClickableMenu.draw(Game1.spriteBatch);
                                            events.RenderedActiveMenu.RaiseEmpty();
                                        }
                                        catch (Exception ex)
                                        {
                                            this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during end-of-night-stuff. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                            Game1.activeClickableMenu.exitThisMenu();
                                        }
                                    }

                                }
                                else if (Game1.farmEvent != null)
                                {
                                    Game1.farmEvent.drawAboveEverything(Game1.spriteBatch);
                                }

                                if (Game1.emoteMenu != null && !this.takingMapScreenshot)
                                    Game1.emoteMenu.draw(Game1.spriteBatch);
                                if (Game1.HostPaused)
                                {
                                    string s = Game1.content.LoadString("Strings\\StringsFromCSFiles:DayTimeMoneyBox.cs.10378");
                                    SpriteText.drawStringWithScrollCenteredAt(Game1.spriteBatch, s, 96, 32);
                                }

                                this._spriteBatchEndMethod.Invoke();
                                this.drawOverlays(Game1.spriteBatch, false);
                                this.renderScreenBuffer(BlendState.Opaque, toBuffer);
                                if (this.DrawHudField.GetValue())
                                {
                                    this.DrawDayTimeMoneyBoxMethod.Invoke();
                                    Game1.SetSpriteBatchBeginNextID("A-C");
                                    this.SpriteBatchBeginMethod.Invoke(1f);
                                    events.RenderingHud.RaiseEmpty();
                                    this.DrawHUD();
                                    events.RenderedHud.RaiseEmpty();
                                    if (Game1.currentLocation != null && !(Game1.activeClickableMenu is GameMenu) && !(Game1.activeClickableMenu is QuestLog))
                                        Game1.currentLocation.drawAboveAlwaysFrontLayerText(Game1.spriteBatch);

                                    this.DrawAfterMapMethod.Invoke();
                                    this._spriteBatchEndMethod.Invoke();
                                    if (TutorialManager.Instance != null)
                                    {
                                        Game1.SetSpriteBatchBeginNextID("A-D");
                                        this.SpriteBatchBeginMethod.Invoke(Game1.options.zoomLevel);
                                        TutorialManager.Instance.draw(Game1.spriteBatch);
                                        this._spriteBatchEndMethod.Invoke();
                                    }

                                    this.DrawToolbarMethod.Invoke();
                                    this.DrawMenuMouseCursorMethod.Invoke();
                                }

                                if (this.DrawHudField.GetValue() || Game1.player.CanMove) this.DrawVirtualJoypadMethod.Invoke();
                                this.DrawFadeToBlackFullScreenRectMethod.Invoke();
                                Game1.SetSpriteBatchBeginNextID("A-E");
                                this.SpriteBatchBeginMethod.Invoke(1f);
                                this.DrawChatBoxMethod.Invoke();
                                this._spriteBatchEndMethod.Invoke();
                                if (this.DrawActiveClickableMenuField.GetValue())
                                {
                                    try
                                    {
                                        if (Game1.activeClickableMenu is DialogueBox)
                                        {
                                            Game1.BackupViewportAndZoom(true);
                                            this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                            events.RenderingActiveMenu.RaiseEmpty();
                                            this._spriteBatchEndMethod.Invoke();
                                            Game1.RestoreViewportAndZoom();

                                            this.DrawDialogueBoxForPinchZoomMethod.Invoke();

                                            Game1.BackupViewportAndZoom(true);
                                            this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                            events.RenderedActiveMenu.RaiseEmpty();
                                            this._spriteBatchEndMethod.Invoke();
                                            Game1.RestoreViewportAndZoom();
                                        }
                                        if (Game1.IsActiveClickableMenuUnscaled && !(Game1.activeClickableMenu is DialogueBox))
                                        {
                                                Game1.BackupViewportAndZoom();
                                                this.SpriteBatchBeginMethod.Invoke(1f);
                                                events.RenderingActiveMenu.RaiseEmpty();
                                                this._spriteBatchEndMethod.Invoke();
                                                Game1.RestoreViewportAndZoom();

                                                this.DrawUnscaledActiveClickableMenuForPinchZoomMethod.Invoke();

                                                Game1.BackupViewportAndZoom();
                                                this.SpriteBatchBeginMethod.Invoke(1f);
                                                events.RenderedActiveMenu.RaiseEmpty();
                                                this._spriteBatchEndMethod.Invoke();
                                                Game1.RestoreViewportAndZoom();
                                        }
                                        if (Game1.IsActiveClickableMenuNativeScaled)
                                        {
                                            Game1.BackupViewportAndZoom(true);
                                            this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                            events.RenderingActiveMenu.RaiseEmpty();
                                            this._spriteBatchEndMethod.Invoke();
                                            Game1.RestoreViewportAndZoom();

                                            this.DrawNativeScaledActiveClickableMenuForPinchZoomMethod.Invoke();

                                            Game1.BackupViewportAndZoom(true);
                                            this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                            events.RenderedActiveMenu.RaiseEmpty();
                                            this._spriteBatchEndMethod.Invoke();
                                            Game1.RestoreViewportAndZoom();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        this.Monitor.Log($"The {Game1.activeClickableMenu.GetType().FullName} menu crashed while drawing itself during end-of-night-stuff. SMAPI will force it to exit to avoid crashing the game.\n{ex.GetLogSummary()}", LogLevel.Error);
                                        Game1.activeClickableMenu.exitThisMenu();
                                    }

                                    if (Game1.IsActiveClickableMenuNativeScaled)
                                        this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                    else
                                        this.SpriteBatchBeginMethod.Invoke(Game1.options.zoomLevel);
                                    events.Rendered.RaiseEmpty();
                                    this._spriteBatchEndMethod.Invoke();
                                }
                                else
                                {
                                    this.SpriteBatchBeginMethod.Invoke(Game1.options.zoomLevel);
                                    events.Rendered.RaiseEmpty();
                                    this._spriteBatchEndMethod.Invoke();
                                }

                                if (this.DrawHudField.GetValue() && Game1.hudMessages.Count > 0 && (!Game1.eventUp || Game1.isFestival()))
                                {
                                    Game1.SetSpriteBatchBeginNextID("A-F");
                                    this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                    this.DrawHUDMessagesMethod.Invoke();
                                    this._spriteBatchEndMethod.Invoke();
                                }

                                if (Game1.CurrentEvent != null && Game1.CurrentEvent.skippable && !Game1.CurrentEvent.skipped && (Game1.activeClickableMenu == null || Game1.activeClickableMenu != null && !(Game1.activeClickableMenu is MenuWithInventory)))
                                {
                                    Game1.SetSpriteBatchBeginNextID("A-G");
                                    this.SpriteBatchBeginMethod.Invoke(Game1.NativeZoomLevel);
                                    Game1.CurrentEvent.DrawSkipButton(Game1.spriteBatch);
                                    this._spriteBatchEndMethod.Invoke();
                                }

                                this.DrawTutorialUIMethod.Invoke();
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Immediately exit the game without saving. This should only be invoked when an irrecoverable fatal error happens that risks save corruption or game-breaking bugs.</summary>
        /// <param name="message">The fatal log message.</param>
        private void ExitGameImmediately(string message)
        {
            this.Monitor.LogFatal(message);
            this.CancellationToken.Cancel();
        }
    }
}
