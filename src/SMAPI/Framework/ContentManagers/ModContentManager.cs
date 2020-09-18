using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Toolkit.Serialization;
using StardewModdingAPI.Toolkit.Utilities;
using StardewValley;
using xTile;
using xTile.Format;
using xTile.ObjectModel;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ContentManagers
{
    /// <summary>A content manager which handles reading files from a SMAPI mod folder with support for unpacked files.</summary>
    internal class ModContentManager : BaseContentManager
    {
        /*********
        ** Fields
        *********/
        /// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
        private readonly JsonHelper JsonHelper;

        /// <summary>The mod display name to show in errors.</summary>
        private readonly string ModName;

        /// <summary>The game content manager used for map tilesheets not provided by the mod.</summary>
        private readonly IContentManager GameContentManager;

        /// <summary>The language code for language-agnostic mod assets.</summary>
        private readonly LanguageCode DefaultLanguage = Constants.DefaultLanguage;

        /// <summary>Reflector used to access xnbs on Android.
        private readonly Reflector Reflector;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
        /// <param name="gameContentManager">The game content manager used for map tilesheets not provided by the mod.</param>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="modName">The mod display name to show in errors.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        /// <param name="currentCulture">The current culture for which to localize content.</param>
        /// <param name="coordinator">The central coordinator which manages content managers.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        /// <param name="jsonHelper">Encapsulates SMAPI's JSON file parsing.</param>
        /// <param name="onDisposing">A callback to invoke when the content manager is being disposed.</param>
        public ModContentManager(string name, IContentManager gameContentManager, IServiceProvider serviceProvider, string modName, string rootDirectory, CultureInfo currentCulture, ContentCoordinator coordinator, IMonitor monitor, Reflector reflection, JsonHelper jsonHelper, Action<BaseContentManager> onDisposing)
            : base(name, serviceProvider, rootDirectory, currentCulture, coordinator, monitor, reflection, onDisposing, isNamespaced: true)
        {
            this.GameContentManager = gameContentManager;
            this.JsonHelper = jsonHelper;
            this.ModName = modName;
            this.Reflector = reflection;
        }

        /// <summary>Load an asset that has been processed by the content pipeline.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        public override T Load<T>(string assetName)
        {
            return this.Load<T>(assetName, this.DefaultLanguage, useCache: false);
        }

        /// <summary>Load an asset that has been processed by the content pipeline.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="language">The language code for which to load content.</param>
        public override T Load<T>(string assetName, LanguageCode language)
        {
            return this.Load<T>(assetName, language, useCache: false);
        }

        /// <summary>Load an asset that has been processed by the content pipeline.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset path relative to the loader root directory, not including the <c>.xnb</c> extension.</param>
        /// <param name="language">The language code for which to load content.</param>
        /// <param name="useCache">Whether to read/write the loaded asset to the asset cache.</param>
        public override T Load<T>(string assetName, LanguageCode language, bool useCache)
        {
            assetName = this.AssertAndNormalizeAssetName(assetName);

            // disable caching
            // This is necessary to avoid assets being shared between content managers, which can
            // cause changes to an asset through one content manager affecting the same asset in
            // others (or even fresh content managers). See https://www.patreon.com/posts/27247161
            // for more background info.
            if (useCache)
                throw new InvalidOperationException("Mod content managers don't support asset caching.");

            // disable language handling
            // Mod files don't support automatic translation logic, so this should never happen.
            if (language != this.DefaultLanguage)
                throw new InvalidOperationException("Localized assets aren't supported by the mod content manager.");

            // resolve managed asset key
            {
                if (this.Coordinator.TryParseManagedAssetKey(assetName, out string contentManagerID, out string relativePath))
                {
                    if (contentManagerID != this.Name)
                        throw new SContentLoadException($"Can't load managed asset key '{assetName}' through content manager '{this.Name}' for a different mod.");
                    assetName = relativePath;
                }
            }

            // get local asset
            SContentLoadException GetContentError(string reasonPhrase) => new SContentLoadException($"Failed loading asset '{assetName}' from {this.Name}: {reasonPhrase}");
            T asset;
            try
            {
                // get file
                FileInfo file = this.GetModFile(assetName);
                if (!file.Exists)
                    throw GetContentError("the specified path doesn't exist.");

                // load content
                switch (file.Extension.ToLower())
                {
                    // XNB file
                    case ".xnb":
                        {
                            asset = this.RawLoad<T>(assetName, useCache: false);
                            if (asset is Map map)
                            {
                                this.NormalizeTilesheetPaths(map);
                                this.FixCustomTilesheetPaths(map, relativeMapPath: assetName);
                            }
                        }
                        return this.ModedLoad<T>(assetName, language);

                    // unpacked data
                    case ".json":
                        {
                            if (!this.JsonHelper.ReadJsonFileIfExists(file.FullName, out asset))
                                throw GetContentError("the JSON file is invalid."); // should never happen since we check for file existence above
                        }
                        break;

                    // unpacked image
                    case ".png":
                        {
                            // validate
                            if (typeof(T) != typeof(Texture2D))
                                throw GetContentError($"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Texture2D)}'.");

                            // fetch & cache
                            using FileStream stream = File.OpenRead(file.FullName);

                            Texture2D texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
                            texture = this.PremultiplyTransparency(texture);
                            asset = (T)(object)texture;
                        }
                        break;

                    // unpacked map
                    case ".tbin":
                    case ".tmx":
                        {
                            // validate
                            if (typeof(T) != typeof(Map))
                                throw GetContentError($"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Map)}'.");

                            // fetch & cache
                            FormatManager formatManager = FormatManager.Instance;
                            Map map = formatManager.LoadMap(file.FullName);
                            this.NormalizeTilesheetPaths(map);
                            this.FixCustomTilesheetPaths(map, relativeMapPath: assetName);
                            asset = (T)(object)map;
                        }
                        break;

                    default:
                        throw GetContentError($"unknown file extension '{file.Extension}'; must be one of '.json', '.png', '.tbin', or '.xnb'.");
                }
            }
            catch (Exception ex) when (!(ex is SContentLoadException))
            {
                if (ex.GetInnermostException() is DllNotFoundException dllEx && dllEx.Message == "libgdiplus.dylib")
                    throw GetContentError("couldn't find libgdiplus, which is needed to load mod images. Make sure Mono is installed and you're running the game through the normal launcher.");
                throw new SContentLoadException($"The content manager failed loading content asset '{assetName}' from {this.Name}.", ex);
            }

            // track & return asset
            this.TrackAsset(assetName, asset, language, useCache);
            return asset;
        }

        /// <summary>Create a new content manager for temporary use.</summary>
        public override LocalizedContentManager CreateTemporary()
        {
            throw new NotSupportedException("Can't create a temporary mod content manager.");
        }

        /// <summary>Get the underlying key in the game's content cache for an asset. This does not validate whether the asset exists.</summary>
        /// <param name="key">The local path to a content file relative to the mod folder.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        public string GetInternalAssetKey(string key)
        {
            FileInfo file = this.GetModFile(key);
            string relativePath = PathUtilities.GetRelativePath(this.RootDirectory, file.FullName);
            return Path.Combine(this.Name, relativePath);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get whether an asset has already been loaded.</summary>
        /// <param name="normalizedAssetName">The normalized asset name.</param>
        protected override bool IsNormalizedKeyLoaded(string normalizedAssetName)
        {
            return this.Cache.ContainsKey(normalizedAssetName);
        }

        /// <summary>Get a file from the mod folder.</summary>
        /// <param name="path">The asset path relative to the content folder.</param>
        private FileInfo GetModFile(string path)
        {
            // try exact match
            FileInfo file = new FileInfo(Path.Combine(this.FullRootDirectory, path));

            // try with default extension
            if (!file.Exists && file.Extension.ToLower() != ".xnb")
            {
                FileInfo result = new FileInfo(file.FullName + ".xnb");
                if (result.Exists)
                    file = result;
            }

            return file;
        }

        /// <summary>Premultiply a texture's alpha values to avoid transparency issues in the game.</summary>
        /// <param name="texture">The texture to premultiply.</param>
        /// <returns>Returns a premultiplied texture.</returns>
        /// <remarks>Based on <a href="https://gamedev.stackexchange.com/a/26037">code by David Gouveia</a>.</remarks>
        private Texture2D PremultiplyTransparency(Texture2D texture)
        {
            // premultiply pixels
            Color[] data = new Color[texture.Width * texture.Height];
            texture.GetData(data);
            for (int i = 0; i < data.Length; i++)
            {
                var pixel = data[i];
                if (pixel.A == byte.MinValue || pixel.A == byte.MaxValue)
                    continue; // no need to change fully transparent/opaque pixels

                data[i] = new Color(pixel.R * pixel.A / byte.MaxValue, pixel.G * pixel.A / byte.MaxValue, pixel.B * pixel.A / byte.MaxValue, pixel.A); // slower version: Color.FromNonPremultiplied(data[i].ToVector4())
            }

            texture.SetData(data);
            return texture;
        }

        /// <summary>Normalize map tilesheet paths for the current platform.</summary>
        /// <param name="map">The map whose tilesheets to fix.</param>
        private void NormalizeTilesheetPaths(Map map)
        {
            foreach (TileSheet tilesheet in map.TileSheets)
                tilesheet.ImageSource = this.NormalizePathSeparators(tilesheet.ImageSource);
        }

        /// <summary>Fix custom map tilesheet paths so they can be found by the content manager.</summary>
        /// <param name="map">The map whose tilesheets to fix.</param>
        /// <param name="relativeMapPath">The relative map path within the mod folder.</param>
        /// <exception cref="ContentLoadException">A map tilesheet couldn't be resolved.</exception>
        /// <remarks>
        /// The game's logic for tilesheets in <see cref="Game1.setGraphicsForSeason"/> is a bit specialized. It boils
        /// down to this:
        ///  * If the location is indoors or the desert, or the image source contains 'path' or 'object', it's loaded
        ///    as-is relative to the <c>Content</c> folder.
        ///  * Else it's loaded from <c>Content\Maps</c> with a seasonal prefix.
        ///
        /// That logic doesn't work well in our case, mainly because we have no location metadata at this point.
        /// Instead we use a more heuristic approach: check relative to the map file first, then relative to
        /// <c>Content\Maps</c>, then <c>Content</c>. If the image source filename contains a seasonal prefix, try for a
        /// seasonal variation and then an exact match.
        ///
        /// While that doesn't exactly match the game logic, it's close enough that it's unlikely to make a difference.
        /// </remarks>
        private void FixCustomTilesheetPaths(Map map, string relativeMapPath)
        {
            // get map info
            if (!map.TileSheets.Any())
                return;
            relativeMapPath = this.AssertAndNormalizeAssetName(relativeMapPath); // Mono's Path.GetDirectoryName doesn't handle Windows dir separators
            string relativeMapFolder = Path.GetDirectoryName(relativeMapPath) ?? ""; // folder path containing the map, relative to the mod folder
            bool isOutdoors = map.Properties.TryGetValue("Outdoors", out PropertyValue outdoorsProperty) && outdoorsProperty != null;

            // fix tilesheets
            foreach (TileSheet tilesheet in map.TileSheets)
            {
                string imageSource = tilesheet.ImageSource;
                string errorPrefix = $"{this.ModName} loaded map '{relativeMapPath}' with invalid tilesheet path '{imageSource}'.";

                // validate tilesheet path
                if (Path.IsPathRooted(imageSource) || PathUtilities.GetSegments(imageSource).Contains(".."))
                    throw new SContentLoadException($"{errorPrefix} Tilesheet paths must be a relative path without directory climbing (../).");

                // load best match
                try
                {
                    if (!this.TryGetTilesheetAssetName(relativeMapFolder, imageSource, isOutdoors, out string assetName, out string error))
                        throw new SContentLoadException($"{errorPrefix} {error}");

                    tilesheet.ImageSource = assetName;
                }
                catch (Exception ex) when (!(ex is SContentLoadException))
                {
                    throw new SContentLoadException($"{errorPrefix} The tilesheet couldn't be loaded.", ex);
                }
            }
        }

        /// <summary>Get the actual asset name for a tilesheet.</summary>
        /// <param name="modRelativeMapFolder">The folder path containing the map, relative to the mod folder.</param>
        /// <param name="originalPath">The tilesheet path to load.</param>
        /// <param name="willSeasonalize">Whether the game will apply seasonal logic to the tilesheet.</param>
        /// <param name="assetName">The found asset name.</param>
        /// <param name="error">A message indicating why the file couldn't be loaded.</param>
        /// <returns>Returns whether the asset name was found.</returns>
        /// <remarks>See remarks on <see cref="FixCustomTilesheetPaths"/>.</remarks>
        private bool TryGetTilesheetAssetName(string modRelativeMapFolder, string originalPath, bool willSeasonalize, out string assetName, out string error)
        {
            assetName = null;
            error = null;

            // nothing to do
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                assetName = originalPath;
                return true;
            }

            // parse path
            string filename = Path.GetFileName(originalPath);
            bool isSeasonal = filename.StartsWith("spring_", StringComparison.CurrentCultureIgnoreCase)
                || filename.StartsWith("summer_", StringComparison.CurrentCultureIgnoreCase)
                || filename.StartsWith("fall_", StringComparison.CurrentCultureIgnoreCase)
                || filename.StartsWith("winter_", StringComparison.CurrentCultureIgnoreCase);
            string relativePath = originalPath;
            if (willSeasonalize && isSeasonal)
            {
                string dirPath = Path.GetDirectoryName(originalPath);
                relativePath = Path.Combine(dirPath, $"{Game1.currentSeason}_{filename.Substring(filename.IndexOf("_", StringComparison.CurrentCultureIgnoreCase) + 1)}");
            }

            // get relative to map file
            {
                string localKey = Path.Combine(modRelativeMapFolder, relativePath);
                if (this.GetModFile(localKey).Exists)
                {
                    assetName = this.GetInternalAssetKey(localKey);
                    return true;
                }
            }

            // get from game assets
            // Map tilesheet keys shouldn't include the "Maps/" prefix (the game will add it automatically) or ".png" extension.
            {
                string contentKey = relativePath;
                foreach (char separator in PathUtilities.PossiblePathSeparators)
                {
                    if (contentKey.StartsWith($"Maps{separator}"))
                    {
                        contentKey = contentKey.Substring(5);
                        break;
                    }
                }
                if (contentKey.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    contentKey = contentKey.Substring(0, contentKey.Length - 4);

                try
                {
                    this.GameContentManager.Load<Texture2D>(Path.Combine("Maps", contentKey), this.Language, useCache: true); // no need to bypass cache here, since we're not storing the asset
                    assetName = contentKey;
                    return true;
                }
                catch
                {
                    // ignore file-not-found errors
                    // TODO: while it's useful to suppress an asset-not-found error here to avoid
                    // confusion, this is a pretty naive approach. Even if the file doesn't exist,
                    // the file may have been loaded through an IAssetLoader which failed. So even
                    // if the content file doesn't exist, that doesn't mean the error here is a
                    // content-not-found error. Unfortunately XNA doesn't provide a good way to
                    // detect the error type.
                    if (this.GetContentFolderFileExists(contentKey))
                        throw;
                }
            }

            // not found
            error = "The tilesheet couldn't be found relative to either map file or the game's content folder.";
            return false;
        }

        /// <summary>Get whether a file from the game's content folder exists.</summary>
        /// <param name="key">The asset key.</param>
        private bool GetContentFolderFileExists(string key)
        {
            // get file path
            string path = Path.Combine(this.GameContentManager.FullRootDirectory, key);
            if (!path.EndsWith(".xnb"))
                path += ".xnb";

            // get file
            return new FileInfo(path).Exists;
        }

        public T ModedLoad<T>(string assetName, LanguageCode language)
        {
            if (language != LanguageCode.en)
            {
                string key = assetName + "." + this.LanguageCodeString(language);
                Dictionary<string, bool> _localizedAsset = this.Reflector.GetField<Dictionary<string, bool>>(this, "_localizedAsset").GetValue();
                if (!_localizedAsset.TryGetValue(key, out bool flag) | flag)
                {
                    try
                    {
                        _localizedAsset[key] = true;
                        return this.ModedLoad<T>(key);
                    }
                    catch (ContentLoadException)
                    {
                        _localizedAsset[key] = false;
                    }
                }
            }
            return this.ModedLoad<T>(assetName);
        }

        public T ModedLoad<T>(string assetName)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                throw new ArgumentNullException("assetName");
            }
            T local = default(T);
            string key = assetName.Replace('\\', '/');
            Dictionary<string, object> loadedAssets = this.Reflector.GetField<Dictionary<string, object>>(this, "loadedAssets").GetValue();
            if (loadedAssets.TryGetValue(key, out object obj2) && (obj2 is T))
            {
                return (T)obj2;
            }
            local = this.ReadAsset<T>(assetName, null);
            loadedAssets[key] = local;
            return local;
        }

        protected override Stream OpenStream(string assetName)
        {
            Stream stream;
            try
            {
                stream = new FileStream(Path.Combine(this.RootDirectory, assetName) + ".xnb", FileMode.Open, FileAccess.Read);
                MemoryStream destination = new MemoryStream();
                stream.CopyTo(destination);
                destination.Seek(0L, SeekOrigin.Begin);
                stream.Close();
                stream = destination;
            }
            catch (Exception exception3)
            {
                throw new ContentLoadException("Opening stream error.", exception3);
            }
            return stream;
        }
        protected new T ReadAsset<T>(string assetName, Action<IDisposable> recordDisposableObject)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                throw new ArgumentNullException("assetName");
            }
            ;
            string str = assetName;
            object obj2 = null;
            if (this.Reflector.GetField<IGraphicsDeviceService>(this, "graphicsDeviceService").GetValue() == null)
            {
                this.Reflector.GetField<IGraphicsDeviceService>(this, "graphicsDeviceService").SetValue(this.ServiceProvider.GetService(typeof(IGraphicsDeviceService)) as IGraphicsDeviceService);
            }
            Stream input = this.OpenStream(assetName);
            using (BinaryReader reader = new BinaryReader(input))
            {
                using (ContentReader reader2 = this.Reflector.GetMethod(this, "GetContentReaderFromXnb").Invoke<ContentReader>(assetName, input, reader, recordDisposableObject))
                {
                    MethodInfo method = reader2.GetType().GetMethod("ReadAsset", BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.NonPublic, null, new Type[] { }, new ParameterModifier[] { });
                    obj2 = method.MakeGenericMethod(new Type[] { typeof(T) }).Invoke(reader2, null);
                    if (obj2 is GraphicsResource graphics)
                    {
                        graphics.Name = str;
                    }
                }
            }
            if (obj2 == null)
            {
                throw new Exception("Could not load " + str + " asset!");
            }
            return (T)obj2;
        }

    }
}
