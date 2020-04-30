﻿using BeatSaberMarkupLanguage.OpenType;
using IPA.Utilities.Async;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace BeatSaberMarkupLanguage
{
    public static class FontManager
    {
        private class FontInfo
        {
            public string Path;
            public OpenTypeFont Info;
            public FontInfo(string path, OpenTypeFont info)
            {
                Path = path;
                Info = info;
            }
        }

        // family -> list of fonts in family
        private static Dictionary<string, List<FontInfo>> fontInfoLookup;
        // full name -> font info
        private static Dictionary<string, FontInfo> fontInfoLookupFullName;
        // path -> loaded font object
        private static readonly Dictionary<string, Font> loadedFontsCache = new Dictionary<string, Font>();
        // (unity font, has system fallback set) -> tmp font
        private static readonly Dictionary<(Font font, bool hasFallbacks), TMP_FontAsset> tmpFontCache = new Dictionary<(Font font, bool hasFallbacks), TMP_FontAsset>();

        /// <summary>
        /// The <see cref="Task"/> associated with an ongoing call to <see cref="AsyncLoadSystemFonts"/>.
        /// </summary>
        public static Task SystemFontLoadTask { get; private set; }

        /// <summary>
        /// Asynchronously loads all of the installed system fonts into <see cref="FontManager"/>.
        /// </summary>
        /// <remarks>
        /// Only one of this may be running at a time. If this has already been called, this will simply return the existing task.
        /// If <see cref="FontManager"/> has been initialized, this completes immediately.
        /// </remarks>
        /// <returns>a task representing the async operation</returns>
        public static Task AsyncLoadSystemFonts()
        {
            if (IsInitialized) return Task.CompletedTask;
            if (SystemFontLoadTask != null) return SystemFontLoadTask;
            var task = Task.Factory.StartNew(LoadSystemFonts).Unwrap();
            SystemFontLoadTask = task.ContinueWith(t =>
            {
                Logger.log.Debug("Font loading complete");
                Interlocked.CompareExchange(ref fontInfoLookupFullName, t.Result.fulls, null);
                Interlocked.CompareExchange(ref fontInfoLookup, t.Result.families, null);
                return Task.CompletedTask;
            }, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion).Unwrap();
            task.ContinueWith(t => Logger.log.Error($"Font loading errored: {t.Exception}"), TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.NotOnRanToCompletion);
            return task;
        }

        internal static Task Destroy()
        {
            fontInfoLookup = null;
            fontInfoLookupFullName = null;
            return UnityMainThreadTaskScheduler.Factory.StartNew(() => DestroyObjects(loadedFontsCache.Select(p => p.Value))).Unwrap()
                .ContinueWith(_ => loadedFontsCache.Clear())
                .ContinueWith(_ => DestroyObjects(tmpFontCache.Select(p => p.Value)), UnityMainThreadTaskScheduler.Default).Unwrap()
                .ContinueWith(_ => tmpFontCache.Clear());
        }

        private static async Task DestroyObjects(IEnumerable<UnityEngine.Object> objects)
        {
            foreach (var obj in objects)
            {
                UnityEngine.Object.Destroy(obj);
                await Task.Yield(); // yield back to the scheduler to allow more things to happen
            }
        }

        private static async Task<(Dictionary<string, List<FontInfo>> families, Dictionary<string, FontInfo> fulls)> LoadSystemFonts()
        {
            var paths = Font.GetPathsToOSFonts();

            var families = new Dictionary<string, List<FontInfo>>(paths.Length, StringComparer.InvariantCultureIgnoreCase);
            var fullNames = new Dictionary<string, FontInfo>(paths.Length, StringComparer.InvariantCultureIgnoreCase);

            foreach (var path in paths)
            {
                try
                {
                    AddFontFileToCache(families, fullNames, path);
                }
                catch (Exception e)
                {
                    Logger.log.Error(e);
                }

                await Task.Yield();
            }

            return (families, fullNames);
        }

        private static IEnumerable<FontInfo> AddFontFileToCache(Dictionary<string, List<FontInfo>> cache, Dictionary<string, FontInfo> fullCache, string path)
        {
            FontInfo AddFont(OpenTypeFont font)
            {
#if DEBUG
                Logger.log.Debug($"'{path}' = '{font.Family}' '{font.Subfamily}' ({font.FullName})");
#endif
                var fontInfo = new FontInfo(path, font);

                var list = GetListForFamily(cache, font.Family);
                list.Add(fontInfo);
                var name = font.FullName;
                if (fullCache.ContainsKey(name))
                {
                    Logger.log.Warn($"Duplcicate font with full name '{name}' at {path}");
                }
                else
                {
                    fullCache.Add(name, fontInfo);
                }
                return fontInfo;
            }

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = OpenTypeReader.For(fileStream);
            if (reader is OpenTypeCollectionReader colReader)
            {
                var collection = new OpenTypeCollection(colReader, lazyLoad: false);
                return collection.Select(AddFont).ToList();
            }
            else if (reader is OpenTypeFontReader fontReader)
            {
                var font = new OpenTypeFont(fontReader, lazyLoad: false);
                return Utilities.SingleEnumerable(AddFont(font));
            }
            else
            {
                Logger.log.Warn($"Font file '{path}' is not an OpenType file");
                return Enumerable.Empty<FontInfo>();
            }
        }

        private static List<FontInfo> GetListForFamily(Dictionary<string, List<FontInfo>> cache, string family)
        {
            if (!cache.TryGetValue(family, out var list))
                cache.Add(family, list = new List<FontInfo>());
            return list;
        }

        /// <summary>
        /// Adds a specified OpenType file to the font manager for lookup by name.
        /// </summary>
        /// <param name="path">the path to add to the manager</param>
        /// <returns>the <see cref="Font"/> the file contained</returns>
        /// <exception cref="ArgumentException">if the file pointed to by <paramref name="path"/> is not an OpenType file -or- 
        /// <paramref name="path"/> is not a valid file path</exception>
        /// <exception cref="FileNotFoundException">if the file does not exist</exception>
        public static Font AddFontFile(string path)
        {
            ThrowIfNotInitialized();

            lock (fontInfoLookup)
            {
                var set = AddFontFileToCache(fontInfoLookup, fontInfoLookupFullName, path);
                if (!set.Any())
                    throw new ArgumentException("File is not an OpenType font or collection", nameof(path));
                return GetFontFromCacheOrLoad(set.First());
            }
        }

        /// <summary>
        /// Gets whether or not <see cref="FontManager"/> is initialized.
        /// </summary>
        /// <remarks>
        /// You can <see langword="await"/> <see cref="AsyncLoadSystemFonts"/>, or <see cref="SystemFontLoadTask"/> if it is non-null.
        /// When they complete, <see cref="FontManager"/> will be initialized.
        /// </remarks>
        public static bool IsInitialized => fontInfoLookup != null;

        private static void ThrowIfNotInitialized()
        {
            if (!IsInitialized) throw new InvalidOperationException("FontManager not initialized");
        }

        /// <summary>
        /// Attemts to get a font given a family name, and optionally a subfamily name.
        /// </summary>
        /// <remarks>
        /// When <paramref name="subfamily"/> is <see langword="null"/>, <paramref name="fallbackIfNoSubfamily"/> is ignored,
        /// and always treated as if it were <see langword="true"/>.
        /// </remarks>
        /// <param name="family">the name of the font family to look for</param>
        /// <param name="font">the font with that family name, if any</param>
        /// <param name="subfamily">the font subfamily name</param>
        /// <param name="fallbackIfNoSubfamily">whether or not to fallback to the first font with the given family name
        /// if the given subfamily name was not found</param>
        /// <returns><see langword="true"/> if the font was found, <see langword="false"/> otherwise</returns>
        public static bool TryGetFontByFamily(string family, out Font font, string subfamily = null, bool fallbackIfNoSubfamily = false)
        {
            ThrowIfNotInitialized();

            if (subfamily == null) fallbackIfNoSubfamily = true;
            subfamily ??= "Regular"; // this is a typical default family name

            lock (fontInfoLookup)
            {
                if (fontInfoLookup.TryGetValue(family, out var fonts))
                {
                    var info = fonts.FirstOrDefault(p => p?.Info.Subfamily == subfamily);
                    if (info == null)
                    {
                        if (!fallbackIfNoSubfamily)
                        {
                            font = null;
                            return false;
                        }
                        else
                        {
                            info = fonts.First();
                        }
                    }

                    font = GetFontFromCacheOrLoad(info);
                    return true;
                }
                else
                {
                    font = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// Attempts to get a font by its full name.
        /// </summary>
        /// <param name="fullName">the full name of the font to look for</param>
        /// <param name="font">the font identified by <paramref name="fullName"/>, if any</param>
        /// <returns><see langword="true"/> if the font was found, <see langword="false"/> otherwise</returns>
        public static bool TryGetFontByFullName(string fullName, out Font font)
        {
            ThrowIfNotInitialized();

            lock (fontInfoLookup)
            {
                if (fontInfoLookupFullName.TryGetValue(fullName, out var info))
                {
                    font = GetFontFromCacheOrLoad(info);
                    return true;
                }
                else
                {
                    font = null;
                    return false;
                }
            }
        }

        private static Font GetFontFromCacheOrLoad(FontInfo info)
        {
            lock (loadedFontsCache)
            {
                if (!loadedFontsCache.TryGetValue(info.Path, out var font))
                {
                    font = new Font(info.Path);
                    font.name = info.Info.FullName;
                    loadedFontsCache.Add(info.Path, font);
                }
                return font;
            }
        }


        /// <summary>
        /// Gets the font fallback list provided by the OS for a given font name, if there is any.
        /// </summary>
        /// <remarks>
        /// If the OS specifies no fallbacks, then the result of this function will be empty.
        /// </remarks>
        /// <param name="fullname">the full name of the font to look up the fallbacks for</param>
        /// <returns>a list of fallbacks defined by the OS</returns>
        public static IEnumerable<string> GetOSFontFallbackList(string fullname)
        {
            using var syslinkKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\FontLink\SystemLink");
            if (syslinkKey == null) return Enumerable.Empty<string>();

            var keyVal = syslinkKey.GetValue(fullname);
            if (keyVal is string[] names)
            {
                // the format in this is '<filename>,<font full name>[,<some other stuff>]'
                return names.Select(s => s.Split(','))
                            .Select(a => a.Length > 1 ? a[1] : null)
                            .Where(s => s != null);
            }

            return Enumerable.Empty<string>();
        }
    }
}
