using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyAppsFolder
{
    public static class IconHelper
    {
        // PrivateExtractIcons lets us ask for a specific icon size (256x256). If the
        // file carries a 256px frame (modern app .exe files, Steam per-game .ico
        // files) we get it crisp; otherwise Windows scales its largest frame up.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int PrivateExtractIcons(
            string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, int[] piconid, int nIcons, int flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string def, StringBuilder retVal, int size, string filePath);

        private const int IconPixelSize = 256;

        public static ImageSource? GetIcon(string path, out string reasonIfFailed)
        {
            reasonIfFailed = "";

            try
            {
                if (Directory.Exists(path))
                    return ExtractFromFile(Path.Combine(Environment.SystemDirectory, "shell32.dll"), 3, out reasonIfFailed);

                string ext = Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".lnk")
                    return GetIconFromLnk(path, out reasonIfFailed);

                if (ext == ".url")
                    return GetIconFromUrl(path, out reasonIfFailed);

                if (File.Exists(path))
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

        private static ImageSource? GetIconFromLnk(string path, out string reasonIfFailed)
        {
            reasonIfFailed = "";
            try
            {
                dynamic? shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                dynamic shortcut = shell!.CreateShortcut(path);
                string target = (string)(shortcut.TargetPath ?? "");
                string iconLocation = (string)(shortcut.IconLocation ?? "");

                if (!string.IsNullOrWhiteSpace(iconLocation))
                {
                    int comma = iconLocation.LastIndexOf(',');
                    string iconFile = comma >= 0 ? iconLocation[..comma] : iconLocation;
                    int iconIdx = comma >= 0 && int.TryParse(iconLocation[(comma + 1)..], out int idx) ? idx : 0;

                    if (!string.IsNullOrWhiteSpace(iconFile))
                    {
                        if (File.Exists(iconFile))
                            return ExtractFromFile(iconFile, iconIdx, out reasonIfFailed);

                        reasonIfFailed = $"Shortcut's IconLocation points to a missing file: {iconFile}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(target))
                {
                    if (File.Exists(target))
                        return ExtractFromFile(target, 0, out reasonIfFailed);

                    if (reasonIfFailed == "")
                        reasonIfFailed = $"Shortcut's target does not exist: {target}";
                }

                if (reasonIfFailed == "")
                    reasonIfFailed = "Shortcut has no usable IconLocation or target (may point to a Store/UWP app)";

                return null;
            }
            catch (Exception ex)
            {
                reasonIfFailed = "Failed to resolve .lnk: " + ex.Message;
                return null;
            }
        }

        private static ImageSource? GetIconFromUrl(string path, out string reasonIfFailed)
        {
            reasonIfFailed = "";

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

            // Steam shortcuts sometimes lose their per-game icon file. Fall back
            // to the main Steam icon so it's at least recognizable.
            if (url.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var steamExe in new[] { @"C:\Program Files (x86)\Steam\Steam.exe", @"C:\Program Files\Steam\Steam.exe" })
                {
                    if (File.Exists(steamExe))
                        return ExtractFromFile(steamExe, 0, out _);
                }
            }

            return null;
        }

        private static ImageSource? ExtractFromFile(string file, int index, out string reasonIfFailed)
        {
            reasonIfFailed = "";

            // Preferred: high-resolution extraction at 256px.
            try
            {
                var hicons = new IntPtr[1];
                var ids = new int[1];
                int n = PrivateExtractIcons(file, index, IconPixelSize, IconPixelSize, hicons, ids, 1, 0);
                if (n > 0 && hicons[0] != IntPtr.Zero)
                {
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(hicons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src = TrimTransparentBorder(src);
                        src.Freeze();
                        return src;
                    }
                    finally { DestroyIcon(hicons[0]); }
                }
            }
            catch
            {
                // Fall through to the legacy path below.
            }

            // Fallback: the classic (smaller) extraction.
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            try
            {
                int count = ExtractIconEx(file, index, large, small, 1);
                IntPtr hIcon = large[0] != IntPtr.Zero ? large[0] : small[0];

                if (count <= 0 || hIcon == IntPtr.Zero)
                {
                    reasonIfFailed = $"Windows could not extract icon #{index} from '{file}' (file may be corrupt or that index doesn't exist)";
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
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }

        // A 256px extraction of a smaller icon comes back as the real image tucked
        // into the top-left of a 256x256 transparent canvas. Crop back to the
        // opaque content so it isn't rendered as a tiny blob with huge margins.
        private static BitmapSource TrimTransparentBorder(BitmapSource src)
        {
            try
            {
                var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
                var px = new byte[h * stride];
                bmp.CopyPixels(px, stride, 0);

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (px[y * stride + x * 4 + 3] > 8)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }

                if (maxX < 0) return src; // fully transparent - leave as-is

                int cw = maxX - minX + 1, ch = maxY - minY + 1;
                // Only bother if there's meaningful empty space to remove.
                if (cw >= w - 2 && ch >= h - 2) return src;

                return new CroppedBitmap(bmp, new Int32Rect(minX, minY, cw, ch));
            }
            catch
            {
                return src;
            }
        }
    }
}
