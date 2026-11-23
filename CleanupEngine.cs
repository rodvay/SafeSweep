using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SafeSweep
{
    internal enum SafetyTier
    {
        Safe = 0,
        Caution = 1
    }

    internal sealed class ScannedFileEntry
    {
        public string FullPath;
        public string FileName;
        public long SizeBytes;
        public DateTime LastWriteTime;
    }

    internal sealed class TargetFolderStats
    {
        public string Path;
        public long Bytes;
        public int FileCount;
        public bool Exists;
        public bool PartialAccessDenied;
    }

    internal sealed class CleanupTargetSpec
    {
        public string DirectoryPath;
        public bool Recursive;
        public string[] AllowedFilePrefixes; // null = all files inside DirectoryPath
        public string RequiredExtension;     // e.g. ".db", null = any
        public int MinAgeMinutes;            // e.g. 15 for Temp folders to skip active installers

        public CleanupTargetSpec(string dir, bool recursive, int minAgeMinutes)
        {
            DirectoryPath = dir;
            Recursive = recursive;
            MinAgeMinutes = minAgeMinutes;
        }
    }

    internal sealed class CleanupCategory
    {
        public string Id;
        public string NameEn;
        public string NameTr;
        public string DescEn;
        public string DescTr;
        public string DisplayPath;
        public SafetyTier Tier;
        public bool IsRecycleBin;
        public string[] RunningProcessNames;
        public List<CleanupTargetSpec> Targets;

        // Live scan state
        public bool Checked;
        public bool HasScanned;
        public long TotalBytes;
        public long FileCount;
        public int AccessDeniedFolders;
        public bool IsAppRunning;
        public List<TargetFolderStats> FolderBreakdowns;
        public List<ScannedFileEntry> TopFiles;

        // Post-clean state
        public long LastFreedBytes;
        public long LastDeletedFiles;
        public long LastSkippedFiles;

        public CleanupCategory()
        {
            Targets = new List<CleanupTargetSpec>();
            FolderBreakdowns = new List<TargetFolderStats>();
            TopFiles = new List<ScannedFileEntry>();
        }

        public string GetName(bool isTurkish)
        {
            return isTurkish ? NameTr : NameEn;
        }

        public string GetDescription(bool isTurkish)
        {
            return isTurkish ? DescTr : DescEn;
        }
    }

    internal sealed class CleanSummary
    {
        public long TotalFreedBytes;
        public long TotalDeletedFiles;
        public long TotalSkippedFiles;
        public int CategoriesCleaned;
        public List<string> LogLines;

        public CleanSummary()
        {
            LogLines = new List<string>();
        }
    }

    internal static class RecycleBinNative
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SHQUERYRBINFO32
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO64
        {
            public uint cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin32(string pszRootPath, ref SHQUERYRBINFO32 pSHQueryRBINFO);

        [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin64(string pszRootPath, ref SHQUERYRBINFO64 pSHQueryRBINFO);

        [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        public static bool Query(out long totalBytes, out long itemCount)
        {
            totalBytes = 0;
            itemCount = 0;
            try
            {
                if (IntPtr.Size == 8)
                {
                    SHQUERYRBINFO64 info = new SHQUERYRBINFO64();
                    info.cbSize = (uint)Marshal.SizeOf(typeof(SHQUERYRBINFO64));
                    int hr = SHQueryRecycleBin64(null, ref info);
                    if (hr == 0)
                    {
                        totalBytes = Math.Max(0, info.i64Size);
                        itemCount = Math.Max(0, info.i64NumItems);
                        return true;
                    }
                }
                else
                {
                    SHQUERYRBINFO32 info = new SHQUERYRBINFO32();
                    info.cbSize = (uint)Marshal.SizeOf(typeof(SHQUERYRBINFO32));
                    int hr = SHQueryRecycleBin32(null, ref info);
                    if (hr == 0)
                    {
                        totalBytes = Math.Max(0, info.i64Size);
                        itemCount = Math.Max(0, info.i64NumItems);
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public static bool Empty()
        {
            try
            {
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                // S_OK (0) or S_FALSE / E_UNEXPECTED when already empty
                return hr == 0 || hr == 1 || unchecked((uint)hr) == 0x8000FFFF;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class SafetyGuard
    {
        // Cloud Files recall attributes (OneDrive / iCloud / Dropbox Files On-Demand)
        private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
        private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

        private static readonly HashSet<string> ProtectedRoots = BuildProtectedRoots();

        private static HashSet<string> BuildProtectedRoots()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Environment.SpecialFolder[] folders = new Environment.SpecialFolder[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos,
                Environment.SpecialFolder.Favorites,
                Environment.SpecialFolder.Startup
            };

            foreach (Environment.SpecialFolder sf in folders)
            {
                try
                {
                    string p = Environment.GetFolderPath(sf);
                    if (!string.IsNullOrEmpty(p))
                        set.Add(NormalizeDir(p));
                }
                catch
                {
                }
            }

            try
            {
                string userProf = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProf))
                {
                    set.Add(NormalizeDir(Path.Combine(userProf, "Downloads")));
                    set.Add(NormalizeDir(Path.Combine(userProf, "OneDrive")));
                }
            }
            catch
            {
            }

            return set;
        }

        public static string NormalizeDir(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";
            try
            {
                string full = Path.GetFullPath(path.Trim());
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return "";
            }
        }

        public static bool IsSafeTargetRoot(string dirPath)
        {
            string norm = NormalizeDir(dirPath);
            if (string.IsNullOrEmpty(norm))
                return false;

            // Reject drive roots (e.g. "C:" or "C:\") and shallow paths
            if (norm.Length <= 3 || norm.StartsWith(@"\\", StringComparison.Ordinal))
                return false;

            if (ProtectedRoots.Contains(norm))
                return false;

            // Reject if target root itself is a reparse point / symlink / junction
            if (Directory.Exists(norm) && IsReparsePointOrCloudPlaceholder(norm))
                return false;

            return true;
        }

        public static bool IsStrictlyInsideRoot(string candidatePath, string allowedRoot)
        {
            string normRoot = NormalizeDir(allowedRoot);
            string normCand = NormalizeDir(candidatePath);
            if (string.IsNullOrEmpty(normRoot) || string.IsNullOrEmpty(normCand))
                return false;

            if (string.Equals(normCand, normRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            string prefix = normRoot + Path.DirectorySeparatorChar;
            return normCand.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsReparsePointOrCloudPlaceholder(string path)
        {
            if (string.IsNullOrEmpty(path))
                return true;

            try
            {
                FileAttributes attrs = File.GetAttributes(path);
                int raw = (int)attrs;
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    return true;
                if ((attrs & FileAttributes.Offline) != 0)
                    return true;
                if ((raw & FILE_ATTRIBUTE_RECALL_ON_OPEN) != 0)
                    return true;
                if ((raw & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0)
                    return true;
                return false;
            }
            catch
            {
                // Fail-closed: if attributes cannot be read, treat as unsafe to traverse/delete.
                return true;
            }
        }

        public static bool IsTooFresh(FileInfo fi, int minAgeMinutes)
        {
            if (minAgeMinutes <= 0)
                return false;

            try
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-minAgeMinutes);
                if (fi.LastWriteTimeUtc > cutoff)
                    return true;
                if (fi.CreationTimeUtc > cutoff)
                    return true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        public static bool MatchesFileFilter(string fileName, CleanupTargetSpec spec)
        {
            if (spec == null)
                return false;

            if (!string.IsNullOrEmpty(spec.RequiredExtension))
            {
                if (!fileName.EndsWith(spec.RequiredExtension, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (spec.AllowedFilePrefixes != null && spec.AllowedFilePrefixes.Length > 0)
            {
                bool matched = false;
                for (int i = 0; i < spec.AllowedFilePrefixes.Length; i++)
                {
                    if (fileName.StartsWith(spec.AllowedFilePrefixes[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                    return false;
            }

            return true;
        }
    }

    internal static class CleanupEngine
    {
        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static List<CleanupCategory> BuildCatalog()
        {
            List<CleanupCategory> list = new List<CleanupCategory>();

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string userTemp = Path.GetTempPath();
            string localLow = Path.Combine(Path.GetDirectoryName(localApp) ?? "", "LocalLow");

            // 1. User Temp Files (SAFE, 15-min freshness guard)
            CleanupCategory cUserTemp = new CleanupCategory();
            cUserTemp.Id = "user_temp";
            cUserTemp.NameEn = "User Temporary Files";
            cUserTemp.NameTr = "Kullanıcı Geçici Dosyaları (Temp)";
            cUserTemp.DescEn = "App scratch & installer leftovers. Files modified in the last 15 min are skipped.";
            cUserTemp.DescTr = "Uygulama ve kurulum artıkları. Son 15 dk içinde kullanılan dosyalar korunur.";
            cUserTemp.DisplayPath = SafetyGuard.NormalizeDir(userTemp);
            cUserTemp.Tier = SafetyTier.Safe;
            AddTargetIfSafe(cUserTemp, userTemp, true, 15);
            list.Add(cUserTemp);

            // 2. Windows Temp Files (SAFE, 15-min freshness guard)
            string winTemp = Path.Combine(winDir, "Temp");
            CleanupCategory cWinTemp = new CleanupCategory();
            cWinTemp.Id = "win_temp";
            cWinTemp.NameEn = "Windows System Temp";
            cWinTemp.NameTr = "Windows Sistem Geçici Dosyaları";
            cWinTemp.DescEn = "System temporary logs & installer cache. Active files are safely skipped.";
            cWinTemp.DescTr = "Sistem geçici dosyaları ve kurulum günlükleri. Açık dosyalar atlanır.";
            cWinTemp.DisplayPath = winTemp;
            cWinTemp.Tier = SafetyTier.Safe;
            AddTargetIfSafe(cWinTemp, winTemp, true, 15);
            list.Add(cWinTemp);

            // 3. Windows Error Reporting & Crash Dumps (SAFE)
            CleanupCategory cWer = new CleanupCategory();
            cWer.Id = "wer_dumps";
            cWer.NameEn = "Crash Dumps & Error Reports";
            cWer.NameTr = "Hata Raporları ve Çökme Dökümleri";
            cWer.DescEn = "Old Windows Error Reporting logs and application crash dumps (.dmp).";
            cWer.DescTr = "Eski Windows hata raporları ve uygulama çökme döküm dosyaları (.dmp).";
            cWer.DisplayPath = Path.Combine(localApp, "CrashDumps") + "  +  WER";
            cWer.Tier = SafetyTier.Safe;
            AddTargetIfSafe(cWer, Path.Combine(localApp, "CrashDumps"), true, 5);
            AddTargetIfSafe(cWer, Path.Combine(localApp, @"Microsoft\Windows\WER\ReportArchive"), true, 5);
            AddTargetIfSafe(cWer, Path.Combine(localApp, @"Microsoft\Windows\WER\ReportQueue"), true, 5);
            AddTargetIfSafe(cWer, Path.Combine(progData, @"Microsoft\Windows\WER\ReportArchive"), true, 5);
            AddTargetIfSafe(cWer, Path.Combine(progData, @"Microsoft\Windows\WER\ReportQueue"), true, 5);
            list.Add(cWer);

            // 4. Windows Explorer Thumbnail & Icon Cache (SAFE, strict prefix allowlist)
            string explorerCacheDir = Path.Combine(localApp, @"Microsoft\Windows\Explorer");
            CleanupCategory cThumb = new CleanupCategory();
            cThumb.Id = "thumb_cache";
            cThumb.NameEn = "Windows Thumbnail Cache";
            cThumb.NameTr = "Windows Küçük Resim Önbelleği";
            cThumb.DescEn = "Only thumbcache_*.db & iconcache_*.db files. Explorer rebuilds them automatically.";
            cThumb.DescTr = "Yalnızca thumbcache_*.db ve iconcache_*.db dosyaları. Windows otomatik yeniler.";
            cThumb.DisplayPath = explorerCacheDir;
            cThumb.Tier = SafetyTier.Safe;
            if (SafetyGuard.IsSafeTargetRoot(explorerCacheDir))
            {
                CleanupTargetSpec ts = new CleanupTargetSpec(explorerCacheDir, false, 0);
                ts.AllowedFilePrefixes = new string[] { "thumbcache_", "iconcache_" };
                ts.RequiredExtension = ".db";
                cThumb.Targets.Add(ts);
            }
            list.Add(cThumb);

            // 5. Google Chrome Cache (STRICT cache-only allowlist)
            AddChromiumBrowser(
                list,
                "chrome_cache",
                "Google Chrome Cache",
                "Google Chrome Önbelleği",
                Path.Combine(localApp, @"Google\Chrome\User Data"),
                new string[] { "chrome" });

            // 6. Microsoft Edge Cache
            AddChromiumBrowser(
                list,
                "edge_cache",
                "Microsoft Edge Cache",
                "Microsoft Edge Önbelleği",
                Path.Combine(localApp, @"Microsoft\Edge\User Data"),
                new string[] { "msedge" });

            // 7. Brave Browser Cache
            AddChromiumBrowser(
                list,
                "brave_cache",
                "Brave Browser Cache",
                "Brave Tarayıcı Önbelleği",
                Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data"),
                new string[] { "brave" });

            // 8. Opera / Opera GX Cache
            CleanupCategory cOpera = new CleanupCategory();
            cOpera.Id = "opera_cache";
            cOpera.NameEn = "Opera / Opera GX Cache";
            cOpera.NameTr = "Opera / Opera GX Önbelleği";
            cOpera.DescEn = "Cached web media & bytecode only. Cookies, passwords & history untouched.";
            cOpera.DescTr = "Yalnızca web önbelleği. Çerezler, şifreler ve geçmiş kesinlikle korunur.";
            cOpera.DisplayPath = Path.Combine(localApp, @"Opera Software");
            cOpera.Tier = SafetyTier.Safe;
            cOpera.RunningProcessNames = new string[] { "opera" };
            string[] operaRoots = new string[]
            {
                Path.Combine(localApp, @"Opera Software\Opera Stable"),
                Path.Combine(localApp, @"Opera Software\Opera GX Stable")
            };
            foreach (string opRoot in operaRoots)
            {
                if (Directory.Exists(opRoot))
                {
                    AddTargetIfSafe(cOpera, Path.Combine(opRoot, "Cache"), true, 0);
                    AddTargetIfSafe(cOpera, Path.Combine(opRoot, "Code Cache"), true, 0);
                    AddTargetIfSafe(cOpera, Path.Combine(opRoot, "GPUCache"), true, 0);
                }
            }
            if (cOpera.Targets.Count > 0)
                list.Add(cOpera);

            // 9. Mozilla Firefox Cache (STRICT Local cache2 / startupCache / thumbnails only!)
            string ffProfilesRoot = Path.Combine(localApp, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ffProfilesRoot))
            {
                CleanupCategory cFf = new CleanupCategory();
                cFf.Id = "firefox_cache";
                cFf.NameEn = "Mozilla Firefox Cache";
                cFf.NameTr = "Mozilla Firefox Önbelleği";
                cFf.DescEn = "Local web cache (cache2) only. Bookmarks, cookies & logins are never touched.";
                cFf.DescTr = "Yalnızca yerel cache2 klasörü. Yer imleri, çerezler ve şifreler korunur.";
                cFf.DisplayPath = ffProfilesRoot;
                cFf.Tier = SafetyTier.Safe;
                cFf.RunningProcessNames = new string[] { "firefox" };
                try
                {
                    foreach (string profDir in Directory.GetDirectories(ffProfilesRoot))
                    {
                        if (SafetyGuard.IsReparsePointOrCloudPlaceholder(profDir))
                            continue;
                        AddTargetIfSafe(cFf, Path.Combine(profDir, "cache2"), true, 0);
                        AddTargetIfSafe(cFf, Path.Combine(profDir, "startupCache"), true, 0);
                        AddTargetIfSafe(cFf, Path.Combine(profDir, "thumbnails"), true, 0);
                    }
                }
                catch
                {
                }
                if (cFf.Targets.Count > 0)
                    list.Add(cFf);
            }

            // 10. Discord Cache
            string discordRoam = Path.Combine(roamApp, "discord");
            if (Directory.Exists(discordRoam))
            {
                CleanupCategory cDisc = new CleanupCategory();
                cDisc.Id = "discord_cache";
                cDisc.NameEn = "Discord App Cache";
                cDisc.NameTr = "Discord Uygulama Önbelleği";
                cDisc.DescEn = "Cached images & media only. Your login session and messages stay intact.";
                cDisc.DescTr = "Yalnızca önbelleğe alınan görseller. Oturumunuz ve mesajlarınız etkilenmez.";
                cDisc.DisplayPath = Path.Combine(discordRoam, "Cache");
                cDisc.Tier = SafetyTier.Safe;
                cDisc.RunningProcessNames = new string[] { "Discord" };
                AddTargetIfSafe(cDisc, Path.Combine(discordRoam, "Cache"), true, 0);
                AddTargetIfSafe(cDisc, Path.Combine(discordRoam, "Code Cache"), true, 0);
                AddTargetIfSafe(cDisc, Path.Combine(discordRoam, "GPUCache"), true, 0);
                AddTargetIfSafe(cDisc, Path.Combine(discordRoam, "DawnGraphiteCache"), true, 0);
                AddTargetIfSafe(cDisc, Path.Combine(discordRoam, "DawnWebGPUCache"), true, 0);
                list.Add(cDisc);
            }

            // 11. Steam & Spotify Media/Web Cache (strictly cache folders, never games or playlists)
            CleanupCategory cAppMedia = new CleanupCategory();
            cAppMedia.Id = "steam_spotify_cache";
            cAppMedia.NameEn = "Steam & Spotify Web Cache";
            cAppMedia.NameTr = "Steam ve Spotify Önbelleği";
            cAppMedia.DescEn = "Store browser & audio cache only. Installed games & playlists are never touched.";
            cAppMedia.DescTr = "Yalnızca mağaza tarayıcı ve ses önbelleği. Oyunlar ve listeler korunur.";
            cAppMedia.DisplayPath = Path.Combine(localApp, @"Steam\htmlcache") + "  +  Spotify";
            cAppMedia.Tier = SafetyTier.Safe;
            cAppMedia.RunningProcessNames = new string[] { "steam", "Spotify" };
            AddTargetIfExistsAndSafe(cAppMedia, Path.Combine(localApp, @"Steam\htmlcache"), true, 0);
            AddTargetIfExistsAndSafe(cAppMedia, @"C:\Program Files (x86)\Steam\config\htmlcache", true, 0);
            AddTargetIfExistsAndSafe(cAppMedia, Path.Combine(localApp, @"Spotify\Storage"), true, 0);
            AddTargetIfExistsAndSafe(cAppMedia, Path.Combine(localApp, @"Spotify\Browser\Cache"), true, 0);
            if (cAppMedia.Targets.Count > 0)
                list.Add(cAppMedia);

            // 12. Developer Package Caches (npm, pip, Yarn, NuGet — only if present)
            CleanupCategory cDev = new CleanupCategory();
            cDev.Id = "dev_caches";
            cDev.NameEn = "Developer Package Caches";
            cDev.NameTr = "Geliştirici Paket Önbellekleri";
            cDev.DescEn = "npm, pip, Yarn & NuGet download caches. Packages re-download cleanly if needed.";
            cDev.DescTr = "npm, pip, Yarn ve NuGet indirme önbellekleri. Gerektiğinde tekrar indirilir.";
            cDev.DisplayPath = "npm-cache · pip\\Cache · Yarn · NuGet";
            cDev.Tier = SafetyTier.Safe;
            AddTargetIfExistsAndSafe(cDev, Path.Combine(localApp, "npm-cache"), true, 0);
            AddTargetIfExistsAndSafe(cDev, Path.Combine(roamApp, "npm-cache"), true, 0);
            AddTargetIfExistsAndSafe(cDev, Path.Combine(localApp, @"pip\Cache"), true, 0);
            AddTargetIfExistsAndSafe(cDev, Path.Combine(localApp, @"Yarn\Cache"), true, 0);
            AddTargetIfExistsAndSafe(cDev, Path.Combine(localApp, @"NuGet\v3-cache"), true, 0);
            if (cDev.Targets.Count > 0)
                list.Add(cDev);

            // --- CAUTION TIER (Unchecked by default; user must opt in consciously) ---

            // 13. Recycle Bin (CAUTION — permanent empty of already deleted items)
            CleanupCategory cBin = new CleanupCategory();
            cBin.Id = "recycle_bin";
            cBin.NameEn = "Windows Recycle Bin";
            cBin.NameTr = "Geri Dönüşüm Kutusu";
            cBin.DescEn = "Permanently removes previously deleted items across all drives. Check before emptying.";
            cBin.DescTr = "Tüm sürücülerdeki silinmiş öğeleri kalıcı olarak boşaltır. Önce kontrol edin.";
            cBin.DisplayPath = "$Recycle.Bin (All Drives)";
            cBin.Tier = SafetyTier.Caution;
            cBin.IsRecycleBin = true;
            list.Add(cBin);

            // 14. DirectX & GPU Shader Caches (CAUTION — unchecked by default to avoid initial game stutter)
            CleanupCategory cShader = new CleanupCategory();
            cShader.Id = "gpu_shader_cache";
            cShader.NameEn = "DirectX & GPU Shader Cache";
            cShader.NameTr = "DirectX ve GPU Shader Önbelleği";
            cShader.DescEn = "Compiled graphics shaders (DX/AMD/NVIDIA). Games may stutter briefly while rebuilding.";
            cShader.DescTr = "Derlenmiş grafik shader'ları. Oyunlar ilk açılışta kısa süre yeniden derler.";
            cShader.DisplayPath = Path.Combine(localApp, "D3DSCache") + "  +  GPU Cache";
            cShader.Tier = SafetyTier.Caution;
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localApp, "D3DSCache"), true, 0);
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localApp, @"AMD\DxCache"), true, 0);
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localApp, @"AMD\GLCache"), true, 0);
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localApp, @"NVIDIA\DXCache"), true, 0);
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localApp, @"NVIDIA\GLCache"), true, 0);
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localLow, @"NVIDIA\PerDriverVersion\DXCache"), true, 0);
            AddTargetIfExistsAndSafe(cShader, Path.Combine(localApp, @"Intel\ShaderCache"), true, 0);
            if (cShader.Targets.Count > 0)
                list.Add(cShader);

            return list;
        }

        private static void AddChromiumBrowser(
            List<CleanupCategory> list,
            string id,
            string nameEn,
            string nameTr,
            string userDataDir,
            string[] processNames)
        {
            if (!Directory.Exists(userDataDir))
                return;

            CleanupCategory cat = new CleanupCategory();
            cat.Id = id;
            cat.NameEn = nameEn;
            cat.NameTr = nameTr;
            cat.DescEn = "Web & GPU cache only. Cookies, logins, history, bookmarks & extensions are untouched.";
            cat.DescTr = "Yalnızca web ve GPU önbelleği. Çerez, şifre, geçmiş ve eklentiler korunur.";
            cat.DisplayPath = userDataDir;
            cat.Tier = SafetyTier.Safe;
            cat.RunningProcessNames = processNames;

            // Root-level GPU shader caches inside User Data
            AddTargetIfExistsAndSafe(cat, Path.Combine(userDataDir, "GrShaderCache"), true, 0);
            AddTargetIfExistsAndSafe(cat, Path.Combine(userDataDir, "ShaderCache"), true, 0);
            AddTargetIfExistsAndSafe(cat, Path.Combine(userDataDir, "GraphiteDawnCache"), true, 0);

            // Profile-level cache-only folders (Default, Profile 1, Profile 2, Guest Profile, ...)
            try
            {
                foreach (string subDir in Directory.GetDirectories(userDataDir))
                {
                    string folderName = Path.GetFileName(subDir) ?? "";
                    bool isProfile =
                        string.Equals(folderName, "Default", StringComparison.OrdinalIgnoreCase) ||
                        folderName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(folderName, "Guest Profile", StringComparison.OrdinalIgnoreCase);

                    if (!isProfile || SafetyGuard.IsReparsePointOrCloudPlaceholder(subDir))
                        continue;

                    AddTargetIfExistsAndSafe(cat, Path.Combine(subDir, "Cache"), true, 0);
                    AddTargetIfExistsAndSafe(cat, Path.Combine(subDir, "Code Cache"), true, 0);
                    AddTargetIfExistsAndSafe(cat, Path.Combine(subDir, "GPUCache"), true, 0);
                    AddTargetIfExistsAndSafe(cat, Path.Combine(subDir, "DawnGraphiteCache"), true, 0);
                    AddTargetIfExistsAndSafe(cat, Path.Combine(subDir, "DawnWebGPUCache"), true, 0);
                }
            }
            catch
            {
            }

            if (cat.Targets.Count > 0)
                list.Add(cat);
        }

        private static void AddTargetIfSafe(CleanupCategory cat, string path, bool recursive, int minAgeMinutes)
        {
            if (SafetyGuard.IsSafeTargetRoot(path))
                cat.Targets.Add(new CleanupTargetSpec(SafetyGuard.NormalizeDir(path), recursive, minAgeMinutes));
        }

        private static void AddTargetIfExistsAndSafe(CleanupCategory cat, string path, bool recursive, int minAgeMinutes)
        {
            if (Directory.Exists(path) && SafetyGuard.IsSafeTargetRoot(path))
                cat.Targets.Add(new CleanupTargetSpec(SafetyGuard.NormalizeDir(path), recursive, minAgeMinutes));
        }

        public static bool IsAnyProcessRunning(string[] processNames)
        {
            if (processNames == null || processNames.Length == 0)
                return false;

            for (int i = 0; i < processNames.Length; i++)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(processNames[i]);
                    bool running = procs != null && procs.Length > 0;
                    if (procs != null)
                    {
                        for (int j = 0; j < procs.Length; j++)
                            procs[j].Dispose();
                    }
                    if (running)
                        return true;
                }
                catch
                {
                }
            }

            return false;
        }

        public static void ScanCategory(CleanupCategory cat, Func<bool> isCancelled)
        {
            cat.TotalBytes = 0;
            cat.FileCount = 0;
            cat.AccessDeniedFolders = 0;
            cat.FolderBreakdowns.Clear();
            cat.TopFiles.Clear();
            cat.IsAppRunning = IsAnyProcessRunning(cat.RunningProcessNames);

            if (cat.IsRecycleBin)
            {
                long rbBytes;
                long rbItems;
                if (RecycleBinNative.Query(out rbBytes, out rbItems))
                {
                    cat.TotalBytes = rbBytes;
                    cat.FileCount = rbItems;
                    TargetFolderStats st = new TargetFolderStats();
                    st.Path = "$Recycle.Bin";
                    st.Bytes = rbBytes;
                    st.FileCount = (int)Math.Min(int.MaxValue, rbItems);
                    st.Exists = true;
                    cat.FolderBreakdowns.Add(st);
                }
                cat.HasScanned = true;
                cat.Checked = false; // Caution items always start unchecked!
                return;
            }

            List<ScannedFileEntry> topHeap = new List<ScannedFileEntry>();

            foreach (CleanupTargetSpec spec in cat.Targets)
            {
                if (isCancelled != null && isCancelled())
                    break;

                TargetFolderStats st = new TargetFolderStats();
                st.Path = spec.DirectoryPath;
                st.Exists = Directory.Exists(spec.DirectoryPath);

                if (st.Exists && SafetyGuard.IsSafeTargetRoot(spec.DirectoryPath))
                {
                    ScanDirectoryIterative(spec.DirectoryPath, spec, st, topHeap, isCancelled);
                }

                if (st.Exists)
                {
                    cat.FolderBreakdowns.Add(st);
                    cat.TotalBytes += st.Bytes;
                    cat.FileCount += st.FileCount;
                    if (st.PartialAccessDenied)
                        cat.AccessDeniedFolders++;
                }
            }

            topHeap.Sort(delegate(ScannedFileEntry a, ScannedFileEntry b)
            {
                return b.SizeBytes.CompareTo(a.SizeBytes);
            });
            cat.TopFiles = topHeap;
            cat.HasScanned = true;

            // Only SAFE items with actual reclaimable space are checked by default.
            cat.Checked = (cat.Tier == SafetyTier.Safe && cat.TotalBytes > 0);
        }

        private static void ScanDirectoryIterative(
            string rootDir,
            CleanupTargetSpec spec,
            TargetFolderStats stats,
            List<ScannedFileEntry> topFiles,
            Func<bool> isCancelled)
        {
            Stack<string> dirs = new Stack<string>();
            dirs.Push(rootDir);

            while (dirs.Count > 0)
            {
                if (isCancelled != null && isCancelled())
                    return;

                string current = dirs.Pop();

                // Enumerate files in current directory
                string[] files;
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch (UnauthorizedAccessException)
                {
                    stats.PartialAccessDenied = true;
                    continue;
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < files.Length; i++)
                {
                    string filePath = files[i];
                    try
                    {
                        if (!SafetyGuard.IsStrictlyInsideRoot(filePath, rootDir))
                            continue;
                        if (SafetyGuard.IsReparsePointOrCloudPlaceholder(filePath))
                            continue;

                        string fileName = Path.GetFileName(filePath);
                        if (!SafetyGuard.MatchesFileFilter(fileName, spec))
                            continue;

                        FileInfo fi = new FileInfo(filePath);
                        if (!fi.Exists)
                            continue;
                        if (SafetyGuard.IsTooFresh(fi, spec.MinAgeMinutes))
                            continue;

                        long len = fi.Length;
                        stats.Bytes += len;
                        stats.FileCount++;

                        TrackTopFile(topFiles, filePath, fileName, len, fi.LastWriteTime, 25);
                    }
                    catch
                    {
                    }
                }

                if (!spec.Recursive)
                    continue;

                // Enumerate child directories — NEVER follow reparse points / symlinks / junctions!
                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(current);
                }
                catch (UnauthorizedAccessException)
                {
                    stats.PartialAccessDenied = true;
                    continue;
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < subDirs.Length; i++)
                {
                    string sub = subDirs[i];
                    if (!SafetyGuard.IsStrictlyInsideRoot(sub, rootDir))
                        continue;
                    if (SafetyGuard.IsReparsePointOrCloudPlaceholder(sub))
                        continue;
                    dirs.Push(sub);
                }
            }
        }

        private static void TrackTopFile(List<ScannedFileEntry> top, string fullPath, string fileName, long size, DateTime modified, int maxKeep)
        {
            if (size <= 0)
                return;

            if (top.Count < maxKeep)
            {
                ScannedFileEntry e = new ScannedFileEntry();
                e.FullPath = fullPath;
                e.FileName = fileName;
                e.SizeBytes = size;
                e.LastWriteTime = modified;
                top.Add(e);
                return;
            }

            int minIdx = 0;
            long minVal = top[0].SizeBytes;
            for (int i = 1; i < top.Count; i++)
            {
                if (top[i].SizeBytes < minVal)
                {
                    minVal = top[i].SizeBytes;
                    minIdx = i;
                }
            }

            if (size > minVal)
            {
                ScannedFileEntry e = new ScannedFileEntry();
                e.FullPath = fullPath;
                e.FileName = fileName;
                e.SizeBytes = size;
                e.LastWriteTime = modified;
                top[minIdx] = e;
            }
        }

        public static CleanSummary CleanSelected(
            List<CleanupCategory> categories,
            Action<string, int, int> onProgress,
            Func<bool> isCancelled)
        {
            CleanSummary summary = new CleanSummary();
            List<CleanupCategory> selected = new List<CleanupCategory>();
            foreach (CleanupCategory cat in categories)
            {
                if (cat.Checked && cat.TotalBytes > 0)
                    selected.Add(cat);
            }

            for (int i = 0; i < selected.Count; i++)
            {
                if (isCancelled != null && isCancelled())
                    break;

                CleanupCategory cat = selected[i];
                cat.LastFreedBytes = 0;
                cat.LastDeletedFiles = 0;
                cat.LastSkippedFiles = 0;

                if (onProgress != null)
                    onProgress(cat.NameEn, i, selected.Count);

                if (cat.IsRecycleBin)
                {
                    long beforeBytes;
                    long beforeItems;
                    RecycleBinNative.Query(out beforeBytes, out beforeItems);
                    if (RecycleBinNative.Empty())
                    {
                        long afterBytes;
                        long afterItems;
                        RecycleBinNative.Query(out afterBytes, out afterItems);
                        cat.LastFreedBytes = Math.Max(0, beforeBytes - afterBytes);
                        cat.LastDeletedFiles = Math.Max(0, beforeItems - afterItems);
                        cat.LastSkippedFiles = Math.Max(0, afterItems);
                    }
                    else
                    {
                        cat.LastSkippedFiles = beforeItems;
                    }
                }
                else
                {
                    foreach (CleanupTargetSpec spec in cat.Targets)
                    {
                        if (isCancelled != null && isCancelled())
                            break;

                        long freed;
                        long deleted;
                        long skipped;
                        CleanDirectorySafely(spec, out freed, out deleted, out skipped, isCancelled);
                        cat.LastFreedBytes += freed;
                        cat.LastDeletedFiles += deleted;
                        cat.LastSkippedFiles += skipped;
                    }
                }

                summary.TotalFreedBytes += cat.LastFreedBytes;
                summary.TotalDeletedFiles += cat.LastDeletedFiles;
                summary.TotalSkippedFiles += cat.LastSkippedFiles;
                summary.CategoriesCleaned++;

                // Re-scan category immediately so UI reflects exact remaining state
                ScanCategory(cat, isCancelled);
                cat.Checked = false;
            }

            if (onProgress != null)
                onProgress("", selected.Count, selected.Count);

            return summary;
        }

        /// <summary>
        /// Safely deletes eligible files inside spec.DirectoryPath without EVER:
        /// - deleting spec.DirectoryPath itself,
        /// - traversing symlinks / directory junctions / reparse points / cloud placeholders,
        /// - crossing outside spec.DirectoryPath,
        /// - deleting Temp files newer than spec.MinAgeMinutes,
        /// - crashing on locked/in-use files.
        /// Exposed internally so our automated NTFS junction safety test suite can verify it directly.
        /// </summary>
        public static void CleanDirectorySafely(
            CleanupTargetSpec spec,
            out long freedBytes,
            out long deletedFiles,
            out long skippedFiles,
            Func<bool> isCancelled)
        {
            freedBytes = 0;
            deletedFiles = 0;
            skippedFiles = 0;

            if (spec == null || string.IsNullOrEmpty(spec.DirectoryPath))
                return;

            string rootDir = SafetyGuard.NormalizeDir(spec.DirectoryPath);
            if (!Directory.Exists(rootDir) || !SafetyGuard.IsSafeTargetRoot(rootDir))
                return;

            // Collect safe subdirectories bottom-up so empty non-reparse child dirs can be pruned cleanly
            Stack<string> pending = new Stack<string>();
            Stack<string> postOrderDirs = new Stack<string>();
            pending.Push(rootDir);

            while (pending.Count > 0)
            {
                if (isCancelled != null && isCancelled())
                    return;

                string current = pending.Pop();

                string[] files;
                try
                {
                    files = Directory.GetFiles(current);
                }
                catch
                {
                    skippedFiles++;
                    continue;
                }

                for (int i = 0; i < files.Length; i++)
                {
                    if (isCancelled != null && isCancelled())
                        return;

                    string filePath = files[i];
                    try
                    {
                        if (!SafetyGuard.IsStrictlyInsideRoot(filePath, rootDir))
                        {
                            skippedFiles++;
                            continue;
                        }

                        if (SafetyGuard.IsReparsePointOrCloudPlaceholder(filePath))
                        {
                            skippedFiles++;
                            continue;
                        }

                        string fileName = Path.GetFileName(filePath);
                        if (!SafetyGuard.MatchesFileFilter(fileName, spec))
                            continue;

                        FileInfo fi = new FileInfo(filePath);
                        if (!fi.Exists)
                            continue;

                        if (SafetyGuard.IsTooFresh(fi, spec.MinAgeMinutes))
                        {
                            skippedFiles++;
                            continue;
                        }

                        long len = fi.Length;

                        // Clear ReadOnly attribute if set on disposable cache/temp file
                        if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                        {
                            fi.Attributes = fi.Attributes & ~FileAttributes.ReadOnly;
                        }

                        File.Delete(filePath);
                        freedBytes += len;
                        deletedFiles++;
                    }
                    catch
                    {
                        // Locked / in-use / permission denied -> safely skip
                        skippedFiles++;
                    }
                }

                if (!spec.Recursive)
                    continue;

                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(current);
                }
                catch
                {
                    skippedFiles++;
                    continue;
                }

                for (int i = 0; i < subDirs.Length; i++)
                {
                    string sub = subDirs[i];
                    if (!SafetyGuard.IsStrictlyInsideRoot(sub, rootDir))
                    {
                        skippedFiles++;
                        continue;
                    }
                    if (SafetyGuard.IsReparsePointOrCloudPlaceholder(sub))
                    {
                        // CRITICAL: Never enter and never delete symlinks/junctions!
                        skippedFiles++;
                        continue;
                    }

                    pending.Push(sub);
                    postOrderDirs.Push(sub);
                }
            }

            // Remove now-empty subdirectories strictly inside rootDir (NEVER rootDir itself!)
            while (postOrderDirs.Count > 0)
            {
                string dir = postOrderDirs.Pop();
                try
                {
                    if (!SafetyGuard.IsStrictlyInsideRoot(dir, rootDir))
                        continue;
                    if (SafetyGuard.IsReparsePointOrCloudPlaceholder(dir))
                        continue;

                    if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    {
                        Directory.Delete(dir, false);
                    }
                }
                catch
                {
                }
            }
        }

        public static string FormatBytes(long bytes, bool isTurkish)
        {
            System.Globalization.CultureInfo c = GetCulture(isTurkish);
            if (bytes < 1024)
                return string.Format(c, "{0} B", bytes);
            double kb = bytes / 1024.0;
            if (kb < 1024.0)
                return string.Format(c, "{0:N1} KB", kb);
            double mb = kb / 1024.0;
            if (mb < 1024.0)
                return string.Format(c, "{0:N1} MB", mb);
            double gb = mb / 1024.0;
            return string.Format(c, "{0:N2} GB", gb);
        }

        public static System.Globalization.CultureInfo GetCulture(bool isTurkish)
        {
            try
            {
                return System.Globalization.CultureInfo.GetCultureInfo(isTurkish ? "tr-TR" : "en-US");
            }
            catch
            {
                return System.Globalization.CultureInfo.InvariantCulture;
            }
        }
    }
}
