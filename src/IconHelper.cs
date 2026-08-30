// ─────────────────────────────────────────────────────────────────────────────
//  IconHelper.cs
//
//  Given the path to a shortcut, hand back a picture to show on its tile.
//
//  There's no single Windows call that does "give me the icon for this .lnk /
//  .url at 256px", so this walks a few routes:
//    folder → a generic folder icon from shell32.dll
//    .lnk   → read the shortcut's IconLocation / TargetPath, then extract from that
//    .url   → read IconFile / IconIndex from the file's text, extract from that
//             (with a Steam-specific fallback to Steam.exe's icon)
//    other  → extract straight from the file
//
//  All of them funnel into ExtractFromFile(), which asks Windows for a 256px
//  icon and, failing that, falls back to the old 32/48px call. GetIcon returns
//  null on failure and fills reasonIfFailed with a plain-English reason, which
//  the window writes to icon_debug.log.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.IO;
using System.Runtime.InteropServices;     // DllImport — calling native Windows functions
using System.Text;                         // StringBuilder — buffers the native calls write into
using System.Windows;                      // Int32Rect
using System.Windows.Interop;              // Imaging.CreateBitmapSourceFromHIcon
using System.Windows.Media;                // ImageSource, PixelFormats
using System.Windows.Media.Imaging;        // BitmapSource, CroppedBitmap, FormatConvertedBitmap

namespace FolderDock
{
    /// <summary>Resolves a displayable icon for a shortcut/file path. Static — no state.</summary>
    public static class IconHelper
    {
        // ── Native Windows functions (P/Invoke) ────────────────────────────
        // "extern" + [DllImport] = "this method actually lives in that DLL".

        /// <summary>
        /// Extracts icons from a file at a size we choose. Ask for 256×256: if the
        /// file has a 256px frame (modern .exe files, Steam per-game .ico files) we
        /// get it crisp; otherwise Windows scales its largest frame up.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int PrivateExtractIcons(
            string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, int[] piconid, int nIcons, int flags);

        /// <summary>Older icon extractor — only gives ~32px (large) / ~16px (small). Used as a fallback.</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        /// <summary>Frees an icon handle returned by the calls above (they're unmanaged; the GC won't).</summary>
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>Reads one "key=value" from an old-style INI file. A .url shortcut IS such a file.</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);

        private const int IconPixelSize = 256;   // the size we ask PrivateExtractIcons for

        /// <summary>
        /// Main entry point. Returns a frozen image for <paramref name="path"/>, or
        /// null plus a reason in <paramref name="reasonIfFailed"/>.
        /// </summary>
        public static ImageSource? GetIcon(string path, out string reasonIfFailed)
        {
            reasonIfFailed = "";

            try
            {
                if (Directory.Exists(path))   // it's a folder → use shell32's generic folder icon (index 3)
                    return ExtractFromFile(Path.Combine(Environment.SystemDirectory, "shell32.dll"), 3, out reasonIfFailed);

                string ext = Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".lnk")            // Windows shortcut → resolve where it points
                    return GetIconFromLnk(path, out reasonIfFailed);

                if (ext == ".url")            // internet / Steam shortcut → parse its text
                    return GetIconFromUrl(path, out reasonIfFailed);

                if (File.Exists(path))        // any other real file → extract straight from it
                    return ExtractFromFile(path, 0, out reasonIfFailed);

                reasonIfFailed = "File does not exist";
                return null;
            }
            catch (Exception ex)
            {
                reasonIfFailed = "Unexpected error: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// A .lnk carries an explicit IconLocation ("file,index") and a TargetPath.
        /// Prefer the IconLocation; fall back to the target's own icon.
        /// </summary>
        private static ImageSource? GetIconFromLnk(string path, out string reasonIfFailed)
        {
            reasonIfFailed = "";
            try
            {
                // Same Windows Script Host COM object App uses to *create* shortcuts.
                dynamic? shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                dynamic shortcut = shell!.CreateShortcut(path);              // open (not create — it already exists)
                string target = (string)(shortcut.TargetPath ?? "");        // what it launches
                string iconLocation = (string)(shortcut.IconLocation ?? ""); // "C:\...\app.exe,0" or ",0" or ""

                if (!string.IsNullOrWhiteSpace(iconLocation))
                {
                    // Split "somePath,3" into the path and the index. Use the LAST
                    // comma so paths that themselves contain a comma still work.
                    int comma = iconLocation.LastIndexOf(',');
                    string iconFile = comma >= 0 ? iconLocation[..comma] : iconLocation;
                    int iconIdx = comma >= 0 && int.TryParse(iconLocation[(comma + 1)..], out int idx) ? idx : 0;

                    if (!string.IsNullOrWhiteSpace(iconFile))
                    {
                        if (File.Exists(iconFile))
                            return ExtractFromFile(iconFile, iconIdx, out reasonIfFailed);   // got it

                        reasonIfFailed = $"Shortcut's IconLocation points to a missing file: {iconFile}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(target))
                {
                    if (File.Exists(target))
                        return ExtractFromFile(target, 0, out reasonIfFailed);               // use the target's icon

                    if (reasonIfFailed == "")
                        reasonIfFailed = $"Shortcut's target does not exist: {target}";
                }

                if (reasonIfFailed == "")   // no icon location, no target → likely a Store/UWP app shortcut
                    reasonIfFailed = "Shortcut has no usable IconLocation or target (may point to a Store/UWP app)";

                return null;
            }
            catch (Exception ex)
            {
                reasonIfFailed = "Failed to resolve .lnk: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// A .url is a plain text INI file:
        ///   [InternetShortcut]
        ///   URL=steam://rungameid/1234
        ///   IconFile=C:\...\game.ico
        ///   IconIndex=0
        /// Read those three keys and extract from IconFile; if it's a Steam URL
        /// with no usable icon, fall back to Steam.exe's icon.
        /// </summary>
        private static ImageSource? GetIconFromUrl(string path, out string reasonIfFailed)
        {
            reasonIfFailed = "";

            // GetPrivateProfileString writes the value into the StringBuilder.
            var sbIcon = new StringBuilder(512);
            GetPrivateProfileString("InternetShortcut", "IconFile", "", sbIcon, sbIcon.Capacity, path);
            var sbIndex = new StringBuilder(64);
            GetPrivateProfileString("InternetShortcut", "IconIndex", "0", sbIndex, sbIndex.Capacity, path);
            var sbUrl = new StringBuilder(512);
            GetPrivateProfileString("InternetShortcut", "URL", "", sbUrl, sbUrl.Capacity, path);

            string iconFile = sbIcon.ToString();
            int iconIdx = int.TryParse(sbIndex.ToString(), out int idx) ? idx : 0;
            string url = sbUrl.ToString();

            if (!string.IsNullOrWhiteSpace(iconFile))
            {
                if (File.Exists(iconFile))
                    return ExtractFromFile(iconFile, iconIdx, out reasonIfFailed);

                reasonIfFailed = $"IconFile listed but missing on disk: {iconFile}";
            }
            else
            {
                reasonIfFailed = "No IconFile field in this .url";
            }

            // Steam sometimes drops the per-game .ico. If this points at Steam,
            // use Steam's own icon so the tile is at least recognisable.
            if (url.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var steamExe in new[]
                    { @"C:\Program Files (x86)\Steam\Steam.exe", @"C:\Program Files\Steam\Steam.exe" })
                {
                    if (File.Exists(steamExe))
                        return ExtractFromFile(steamExe, 0, out _);   // '_' = ignore the reason here
                }
            }

            return null;   // reasonIfFailed already set above
        }

        /// <summary>
        /// The one place icons are actually pulled from a file. Tries the 256px
        /// call first; on failure falls back to the old 32/48px one. Always frees
        /// the native icon handles it creates.
        /// </summary>
        private static ImageSource? ExtractFromFile(string file, int index, out string reasonIfFailed)
        {
            reasonIfFailed = "";

            // ── Preferred: high-resolution (256px) ────────────────────────
            try
            {
                var hicons = new IntPtr[1];   // receives 1 icon handle
                var ids = new int[1];         // receives its resource id (unused)
                int n = PrivateExtractIcons(file, index, IconPixelSize, IconPixelSize, hicons, ids, 1, 0);
                if (n > 0 && hicons[0] != IntPtr.Zero)
                {
                    try
                    {
                        // Native HICON → WPF bitmap.
                        var src = Imaging.CreateBitmapSourceFromHIcon(
                            hicons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src = TrimTransparentBorder(src);   // crop away the empty margin around a small icon
                        src.Freeze();                       // make it immutable → safe to use, faster
                        return src;
                    }
                    finally { DestroyIcon(hicons[0]); }     // always free the handle
                }
            }
            catch
            {
                // Something odd — try the legacy path instead of giving up.
            }

            // ── Fallback: classic ExtractIconEx (smaller icons) ───────────
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            try
            {
                int count = ExtractIconEx(file, index, large, small, 1);
                IntPtr hIcon = large[0] != IntPtr.Zero ? large[0] : small[0];   // prefer the large one

                if (count <= 0 || hIcon == IntPtr.Zero)
                {
                    reasonIfFailed = $"Windows could not extract icon #{index} from '{file}' " +
                                     "(file may be corrupt or that index doesn't exist)";
                    return null;
                }

                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch (Exception ex)
            {
                reasonIfFailed = $"Exception extracting icon from '{file}': {ex.Message}";
                return null;
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);   // free whichever handles we got
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }

        /// <summary>
        /// A 256px extraction of a smaller icon comes back as the real image tucked
        /// into the top-left of a 256×256 transparent canvas. Find the bounding box
        /// of the non-transparent pixels and crop to it, so the tile shows the icon
        /// filling its space rather than a tiny blob with huge margins.
        /// </summary>
        private static BitmapSource TrimTransparentBorder(BitmapSource src)
        {
            try
            {
                // Force a known pixel layout (Blue-Green-Red-Alpha, 1 byte each) so we can read the alpha directly.
                var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;   // stride = bytes per row
                var px = new byte[h * stride];
                bmp.CopyPixels(px, stride, 0);                                 // copy all pixels into our array

                // Track the tightest box that contains every visible pixel.
                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (px[y * stride + x * 4 + 3] > 8)   // +3 = the alpha byte; > 8 ≈ "not basically transparent"
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }

                if (maxX < 0) return src;   // nothing visible at all → leave it alone

                int cw = maxX - minX + 1, ch = maxY - minY + 1;   // cropped width / height
                if (cw >= w - 2 && ch >= h - 2) return src;        // already fills the canvas → don't bother

                return new CroppedBitmap(bmp, new Int32Rect(minX, minY, cw, ch));
            }
            catch
            {
                return src;   // any trouble → just use the untrimmed image
            }
        }
    }
}
